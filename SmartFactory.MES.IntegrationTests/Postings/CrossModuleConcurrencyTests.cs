using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using ActM = SmartFactory.MES.Modules.ACT.Models;
using InvM = SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Postings
{
    /// <summary>
    /// 跨模組並行衝突（Phase 5）。
    ///
    /// 既有的並發測試全部是「同一張單被按兩次」——同模組、同單據、同動作。
    /// 真正會出錯而沒人測的是**兩個模組同時動同一筆資料**：各自的狀態守門都通過了
    /// （兩邊看的是不同單據的狀態），撞在一起的是它們共同寫入的那一列。
    ///
    /// 兩個場景各對應一個共用列：
    ///   ① 同批庫存被揀料 vs 被盤點差異調整 → 共用 <c>Inventory</c> 那一列
    ///   ② 同張發票被兩張付款單核銷      → 共用 <c>APInvoice</c> 那一列
    ///
    /// 兩者都以 xmin 樂觀鎖收口。②的 xmin 是本階段才補上的：核銷是
    /// 「讀未結餘額 → 驗證 → 累加」的讀改寫，兩張付款單各自讀到 PaidAmount = 0
    /// 都通過驗證，接著各自寫入絕對值——後到者覆蓋先到者，發票只認一次付款，
    /// 而銀行帳戶被扣了兩次、兩張付款單都是 Paid。帳面自洽，只有對帳才看得出來。
    ///
    /// 真 PG 才有 xmin（InMemory 沒有這個系統欄位），故置於整合測試。
    /// 掛 "Integration" collection：與既有 xmin 並發測試同一組，不新增 collection
    /// （新增第三個會與既有兩個平行共用同一測試庫，觸發種子雙 23505 競態）。
    /// </summary>
    [Collection("Integration")]
    public class CrossModuleConcurrencyTests
    {
        private const string Plant   = "P1";
        private const string RoleId  = "1";
        private const string Prefix  = "XMOD";
        private const string Product = Prefix + "-P1";
        private const string Loc     = Prefix + "-L1";

        private readonly TestWebApplicationFactory _factory;

        public CrossModuleConcurrencyTests(TestWebApplicationFactory factory) => _factory = factory;

        private ApplicationDbContext Db(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return db;
        }

        // ── ① 同批庫存：揀料 vs 盤點差異調整 ────────────────────────────────

        /// <summary>
        /// 揀料出庫與盤點差異調整同時寫同一筆庫存 → 後到者必須被擋下。
        ///
        /// 盤點清冊只記 <c>SnapshotAt</c>（凍結時間點），**不鎖庫存**——也就是說
        /// 盤點期間揀料照樣扣得動。這個設計本身沒問題（否則盤點期間全廠停工），
        /// 但收尾時盤點差異是拿「快照 vs 實盤」算出來的：若差異調整以自己載入時的
        /// 庫存快照寫回絕對值，中途那筆揀料就被無聲抹掉——帳面數量變成盤點當時的值，
        /// 而料已經領走了。擋住它的是 <c>Inventory.xmin</c>，不是任何單據狀態守門
        /// （兩邊各自的單據狀態檢查都會通過，它們看的是不同的單）。
        /// </summary>
        [Fact]
        public async Task Picking_And_CountAdjustment_OnSameLot_SecondWriteIsBlocked()
        {
            await SeedInventoryAsync(onHand: 100m);

            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = Db(scopeA);
            var dbB = Db(scopeB);

            // 兩邊各自載入同一筆庫存（持相同 xmin 快照）
            var invA = await dbA.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.ProductCode == Product && i.LocationCode == Loc);
            var invB = await dbB.Inventories.IgnoreQueryFilters()
                .FirstAsync(i => i.ProductCode == Product && i.LocationCode == Loc);

            // A：揀料出庫 30
            invA.QtyOnHand -= 30m;
            await dbA.SaveChangesAsync();

            // B：盤點差異調整，以自己那份快照寫回「實盤 = 95」（盤盈 −5）。
            //    無並發權杖時這一步會靜默成功，A 揀走的 30 就此消失，帳面憑空多出 25。
            //
            //    寫回的值刻意不等於快照值（95 而非 100）：EF 判定沒有任何屬性變更時
            //    根本不會發出 UPDATE，於是並發權杖也不會作用——測試會以「沒有拋例外」
            //    失敗，看起來像 xmin 沒生效，其實是這支測試自己什麼都沒改。
            invB.QtyOnHand = 95m;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

            // 確認落地的是 A 的結果，B 完全沒有生效
            using var verify = _factory.Services.CreateScope();
            var final = await Db(verify).Inventories.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(i => i.ProductCode == Product && i.LocationCode == Loc);
            Assert.Equal(70m, final.QtyOnHand);
        }

        // ── ② 同張發票：兩張付款單並行核銷 ──────────────────────────────────

        /// <summary>
        /// 兩張付款單同時核銷同一張應付發票 → 後到者必須被擋下。
        ///
        /// 這一格在補 <c>APInvoice.xmin</c> 之前是真的會過的：
        /// <c>ConfirmAPPaymentAsync</c> 先驗「核銷金額 ≤ TotalAmount − PaidAmount」再
        /// <c>PaidAmount += 金額</c>，兩張付款單各自讀到 PaidAmount = 0 都通過驗證，
        /// 然後各自把絕對值寫回去——發票停在「付了一次」，但兩張付款單都是 Paid、
        /// 銀行餘額被扣了兩次。供應商收到兩筆錢而應付帳款只沖掉一筆。
        /// </summary>
        [Fact]
        public async Task TwoPayments_SettlingSameApInvoice_SecondWriteIsBlocked()
        {
            const string invoiceId = Prefix + "-API1";
            await SeedApInvoiceAsync(invoiceId, total: 1000m);

            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = Db(scopeA);
            var dbB = Db(scopeB);

            var invA = await dbA.APInvoices.IgnoreQueryFilters().FirstAsync(i => i.APInvoiceId == invoiceId);
            var invB = await dbB.APInvoices.IgnoreQueryFilters().FirstAsync(i => i.APInvoiceId == invoiceId);

            // 兩邊都看到未結餘額 1000，各自核銷 1000（都會通過服務層的餘額驗證）
            Assert.Equal(1000m, invA.TotalAmount - invA.PaidAmount);
            Assert.Equal(1000m, invB.TotalAmount - invB.PaidAmount);

            invA.PaidAmount += 1000m;
            invA.Status = APInvoiceStatus.Paid;
            await dbA.SaveChangesAsync();

            invB.PaidAmount += 1000m;
            invB.Status = APInvoiceStatus.Paid;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

            // 只認一次核銷（1000 而非 2000，也不是被覆蓋成別的值）
            using var verify = _factory.Services.CreateScope();
            var final = await Db(verify).APInvoices.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(i => i.APInvoiceId == invoiceId);
            Assert.Equal(1000m, final.PaidAmount);
        }

        /// <summary>收款端對稱：兩張收款單同時核銷同一張應收發票。</summary>
        [Fact]
        public async Task TwoReceipts_SettlingSameArInvoice_SecondWriteIsBlocked()
        {
            const string invoiceId = Prefix + "-ARI1";
            await SeedArInvoiceAsync(invoiceId, total: 500m);

            using var scopeA = _factory.Services.CreateScope();
            using var scopeB = _factory.Services.CreateScope();
            var dbA = Db(scopeA);
            var dbB = Db(scopeB);

            var invA = await dbA.ARInvoices.IgnoreQueryFilters().FirstAsync(i => i.ARInvoiceId == invoiceId);
            var invB = await dbB.ARInvoices.IgnoreQueryFilters().FirstAsync(i => i.ARInvoiceId == invoiceId);

            invA.ReceivedAmount += 500m;
            invA.Status = ARInvoiceStatus.Received;
            await dbA.SaveChangesAsync();

            invB.ReceivedAmount += 500m;
            invB.Status = ARInvoiceStatus.Received;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

            using var verify = _factory.Services.CreateScope();
            var final = await Db(verify).ARInvoices.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(i => i.ARInvoiceId == invoiceId);
            Assert.Equal(500m, final.ReceivedAmount);
        }

        // ── ③ 作廢反沖：與 Confirm 走同一組權杖 ──────────────────────────────
        //
        // ①② 證明的是「核銷會撞」，而核銷的兩條路徑（Confirm／Void）都寫同一批
        // 有 xmin 的實體。Confirm 的 catch 一開始就補了，Void 沒有——撞到就是 500。
        // 下面兩格證的是「Void 真的會拋 DbUpdateConcurrencyException」，
        // 也就是 controller 那兩個新 catch 不是死碼（見 reference-concurrency-catch-audit：
        // catch 區塊必須確認 try 內的 SaveChanges 真的觸及帶權杖的實體）。
        //
        // 造衝突的手法：先把發票讀進服務所用的同一個 scoped DbContext，
        // 服務內的 FindAsync 就會直接拿這個已追蹤實例（連同它此刻的 xmin 快照），
        // 中間讓另一個 scope 搶先改同一列把 xmin 推進，服務 SaveChanges 時即比中 0 列。

        /// <summary>付款單作廢時發票已被他人改動 → 必須拋並發例外（而非靜默覆蓋）。</summary>
        [Fact]
        public async Task VoidApPayment_WhenInvoiceChangedConcurrently_ThrowsConcurrency()
        {
            const string invoiceId = Prefix + "-API3";
            const string paymentId = Prefix + "-APP3";
            await SeedApInvoiceAsync(invoiceId, total: 1000m, paid: 1000m, status: APInvoiceStatus.Paid);
            await SeedPaidApPaymentAsync(paymentId, invoiceId, amount: 1000m);
            await EnsureTodayPeriodPostableAsync();

            using var scope = _factory.Services.CreateScope();
            var db  = Db(scope);
            var svc = scope.ServiceProvider.GetRequiredService<IFinPaymentService>();

            await db.APInvoices.FirstAsync(i => i.APInvoiceId == invoiceId);   // 進追蹤，凍結 xmin 快照
            await TouchApInvoiceInOtherScopeAsync(invoiceId);                  // 他人搶先改 → xmin 前進

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => svc.VoidAPPaymentAsync(paymentId));

            // 反沖沒有半套落地：發票仍是已付，付款單仍是 Paid
            using var verify = _factory.Services.CreateScope();
            var vdb = Db(verify);
            var inv = await vdb.APInvoices.AsNoTracking().FirstAsync(i => i.APInvoiceId == invoiceId);
            var pmt = await vdb.APPayments.AsNoTracking().FirstAsync(p => p.APPaymentId == paymentId);
            Assert.Equal(1000m, inv.PaidAmount);
            Assert.Equal(APPaymentStatus.Paid, pmt.Status);
        }

        /// <summary>收款端對稱。</summary>
        [Fact]
        public async Task VoidArReceipt_WhenInvoiceChangedConcurrently_ThrowsConcurrency()
        {
            const string invoiceId = Prefix + "-ARI3";
            const string receiptId = Prefix + "-ARR3";
            await SeedArInvoiceAsync(invoiceId, total: 500m, received: 500m, status: ARInvoiceStatus.Received);
            await SeedReceivedArReceiptAsync(receiptId, invoiceId, amount: 500m);
            await EnsureTodayPeriodPostableAsync();

            using var scope = _factory.Services.CreateScope();
            var db  = Db(scope);
            var svc = scope.ServiceProvider.GetRequiredService<IFinPaymentService>();

            await db.ARInvoices.FirstAsync(i => i.ARInvoiceId == invoiceId);
            await TouchArInvoiceInOtherScopeAsync(invoiceId);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => svc.VoidARReceiptAsync(receiptId));

            using var verify = _factory.Services.CreateScope();
            var vdb = Db(verify);
            var inv = await vdb.ARInvoices.AsNoTracking().FirstAsync(i => i.ARInvoiceId == invoiceId);
            var rec = await vdb.ARReceipts.AsNoTracking().FirstAsync(r => r.ARReceiptId == receiptId);
            Assert.Equal(500m, inv.ReceivedAmount);
            Assert.Equal(ARReceiptStatus.Received, rec.Status);
        }

        // ── 種子（皆可重跑：先刪同鍵）────────────────────────────────────────

        /// <summary>
        /// 作廢以「當下日期」入帳（<c>VoidXxxAsync</c> 用 <c>GetLocalDate()</c>，不是單據日），
        /// 故不能像其他種子那樣用 2097 繞開期間鎖——今天所屬期間若是 Closed／Closing，
        /// 服務會先拋 <c>InvalidOperationException</c>，測試就永遠測不到並發那一段。
        /// 無期間設定時 <c>AssertCanPostAsync</c> 本來就放行，此處只處理「有但不是 Open」。
        /// </summary>
        private async Task EnsureTodayPeriodPostableAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db    = Db(scope);
            var today = DateOnly.FromDateTime(DateTime.Now);

            var period = await db.AccountingPeriods.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.StartDate <= today && p.EndDate >= today);
            if (period == null || period.Status == ActM.PeriodStatus.Open) return;

            period.Status    = ActM.PeriodStatus.Open;
            period.ClosedAt  = null;
            period.ClosedBy  = null;
            period.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        private async Task TouchApInvoiceInOtherScopeAsync(string invoiceId)
        {
            using var scope = _factory.Services.CreateScope();
            var db  = Db(scope);
            var inv = await db.APInvoices.FirstAsync(i => i.APInvoiceId == invoiceId);
            inv.Remarks   = "concurrent-touch";
            inv.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        private async Task TouchArInvoiceInOtherScopeAsync(string invoiceId)
        {
            using var scope = _factory.Services.CreateScope();
            var db  = Db(scope);
            var inv = await db.ARInvoices.FirstAsync(i => i.ARInvoiceId == invoiceId);
            inv.Remarks   = "concurrent-touch";
            inv.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        /// <summary>已付款的付款單（不掛銀行帳戶，把衝突面收斂到發票那一列）。</summary>
        private async Task SeedPaidApPaymentAsync(string paymentId, string invoiceId, decimal amount)
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await db.APPaymentAllocations.IgnoreQueryFilters()
                .Where(a => a.APPaymentId == paymentId).ExecuteDeleteAsync();
            await db.APPayments.IgnoreQueryFilters()
                .Where(p => p.APPaymentId == paymentId).ExecuteDeleteAsync();

            db.APPayments.Add(new APPayment
            {
                APPaymentId = paymentId, SupplierCode = Prefix + "-S",
                PaymentDate = new DateOnly(2097, 6, 20),
                Amount = amount, AmountLocal = amount, ExchangeRate = 1m,
                Status = APPaymentStatus.Paid, BankAccountId = null,
                PlantId = Plant, CreatedByRoleId = RoleId,
                Allocations =
                {
                    new APPaymentAllocation
                    {
                        APPaymentId = paymentId, APInvoiceId = invoiceId,
                        AllocatedAmount = amount, AllocatedAmountLocal = amount,
                    }
                },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedReceivedArReceiptAsync(string receiptId, string invoiceId, decimal amount)
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await db.ARReceiptAllocations.IgnoreQueryFilters()
                .Where(a => a.ARReceiptId == receiptId).ExecuteDeleteAsync();
            await db.ARReceipts.IgnoreQueryFilters()
                .Where(r => r.ARReceiptId == receiptId).ExecuteDeleteAsync();

            db.ARReceipts.Add(new ARReceipt
            {
                ARReceiptId = receiptId, CustomerCode = Prefix + "-C",
                ReceiptDate = new DateOnly(2097, 6, 20),
                Amount = amount, AmountLocal = amount, ExchangeRate = 1m,
                Status = ARReceiptStatus.Received, BankAccountId = null,
                PlantId = Plant, CreatedByRoleId = RoleId,
                Allocations =
                {
                    new ARReceiptAllocation
                    {
                        ARReceiptId = receiptId, ARInvoiceId = invoiceId,
                        AllocatedAmount = amount, AllocatedAmountLocal = amount,
                    }
                },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedInventoryAsync(decimal onHand)
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await db.Inventories.IgnoreQueryFilters()
                .Where(i => i.ProductCode == Product).ExecuteDeleteAsync();

            db.Inventories.Add(new InvM.Inventory
            {
                PlantId = Plant, ProductCode = Product, LocationCode = Loc, LotNo = "",
                QtyOnHand = onHand, QtyReserved = 0m, UnitCost = 1m,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedApInvoiceAsync(
            string invoiceId, decimal total,
            decimal paid = 0m, APInvoiceStatus status = APInvoiceStatus.Open)
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await db.APInvoiceLines.IgnoreQueryFilters()
                .Where(l => l.APInvoiceId == invoiceId).ExecuteDeleteAsync();
            // 上一輪留下的核銷明細會 FK 參照這張發票，不先清就刪不掉（重跑必炸）
            await db.APPaymentAllocations.IgnoreQueryFilters()
                .Where(a => a.APInvoiceId == invoiceId).ExecuteDeleteAsync();
            await db.APInvoices.IgnoreQueryFilters()
                .Where(i => i.APInvoiceId == invoiceId).ExecuteDeleteAsync();

            db.APInvoices.Add(new APInvoice
            {
                APInvoiceId = invoiceId, SupplierCode = Prefix + "-S",
                InvoiceDate = new DateOnly(2097, 6, 15), DueDate = new DateOnly(2097, 7, 15),
                TotalAmount = total, AmountLocal = total, PaidAmount = paid,
                Status = status, ExchangeRate = 1m,
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedArInvoiceAsync(
            string invoiceId, decimal total,
            decimal received = 0m, ARInvoiceStatus status = ARInvoiceStatus.Open)
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            await db.ARInvoiceLines.IgnoreQueryFilters()
                .Where(l => l.ARInvoiceId == invoiceId).ExecuteDeleteAsync();
            await db.ARReceiptAllocations.IgnoreQueryFilters()
                .Where(a => a.ARInvoiceId == invoiceId).ExecuteDeleteAsync();
            await db.ARInvoices.IgnoreQueryFilters()
                .Where(i => i.ARInvoiceId == invoiceId).ExecuteDeleteAsync();

            db.ARInvoices.Add(new ARInvoice
            {
                ARInvoiceId = invoiceId, CustomerCode = Prefix + "-C",
                InvoiceDate = new DateOnly(2097, 6, 15), DueDate = new DateOnly(2097, 7, 15),
                TotalAmount = total, AmountLocal = total, ReceivedAmount = received,
                Status = status, ExchangeRate = 1m,
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            await db.SaveChangesAsync();
        }
    }
}
