using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 跨模組貫穿流程測試的共用基底（真 PostgreSQL）。
    ///
    /// 為什麼要有這一層：既有測試多半是「單一服務 × 單一規則」，跨模組的接縫
    /// （庫存↔應付↔總帳↔稽核）只在各自那一端被斷言過，沒有任何一支測試
    /// 從頭到尾跑完一條單據鏈再回頭檢查「帳到底平不平」。本基底提供：
    ///   - 每條流程一個專屬廠別（資料互不干擾，可重複執行）
    ///   - 與 HTTP 無關的廠別／角色上下文設定（否則全域查詢過濾會讓服務看不到資料）
    ///   - 總帳與庫存的通用斷言（借貸平衡、逐科目淨額、在手量）
    ///
    /// 用法：子類覆寫 Plant，於建構子呼叫 ResetAsync().GetAwaiter().GetResult()。
    /// </summary>
    public abstract class FlowTestBase : IDisposable
    {
        protected const string UserId   = "user1";
        protected const string RoleId   = "1";     // 對齊種子 admin 角色，否則資料權限過濾看不到單據
        protected const string Supplier = "FLOW-SUP";
        protected const string Customer = "FLOW-CUS";
        protected const string Uom      = "PC";
        protected const string Category = "FLOW-CAT";

        // 倉庫／儲位／銀行帳戶的主鍵不含廠別（StorageLocation 為 (WhCode, LocationCode)、
        // BankAccount 為 AccountId），多條流程若共用同一組代碼會互相撞 PK、
        // 且各自的 Purge 會刪掉別條流程的主檔。故一律以廠別前綴隔離。
        protected string Wh   => $"{Plant}-WH";
        protected string Loc  => $"{Plant}-LOC";
        protected string Bank => $"{Plant}-BK";

        protected readonly TestWebApplicationFactory Factory;

        protected FlowTestBase(TestWebApplicationFactory factory) => Factory = factory;

        /// <summary>本流程專屬廠別；子類必須各自不同，測試才能平行且可重跑。</summary>
        protected abstract string Plant { get; }

        /// <summary>
        /// 本流程還會動到的其他廠別（跨廠調撥才需要）。清場與收尾一併涵蓋，
        /// 否則對面廠的庫存異動與傳票會留在共用測試庫裡，被孤兒過帳稽核報成爛帳。
        /// </summary>
        protected virtual IEnumerable<string> ExtraPlants => Array.Empty<string>();

        private IEnumerable<string> AllPlants => new[] { Plant }.Concat(ExtraPlants);

        /// <summary>
        /// 取得一個已設好廠別／角色上下文的 scope。
        /// 服務與 DbContext 同 scope，故服務內未 IgnoreQueryFilters 的查詢也看得到資料。
        /// </summary>
        protected IServiceScope NewScope(out ApplicationDbContext db)
        {
            var scope = Factory.Services.CreateScope();
            db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return scope;
        }

        protected T Svc<T>(IServiceScope scope) where T : notnull
            => scope.ServiceProvider.GetRequiredService<T>();

        /// <summary>清空本廠別的所有流程產物並重建主檔，讓每個測試從乾淨狀態出發。</summary>
        protected async Task ResetAsync(params (string Code, string Name)[] products)
        {
            using var scope = NewScope(out var db);
            foreach (var p in AllPlants) await PurgeAsync(db, p);
            await SeedMastersAsync(db, products);

            // 單位小數位有一份 10 分鐘的行程級快取（UomService），而
            // GetDecimalPlacesAsync 對**查不到的單位一律回 0 位**，
            // 於是 RoundQtyAsync 會把 12.5 靜靜捨成 13。
            //
            // 本基底是直接用 DbContext 建 Uom 的，不像 UomController 建檔後會順手清快取，
            // 所以只要有任何測試在 PC 建立**之前**先讓那份快取載入過，
            // 之後整輪的數量都會被當成整數捨入——而且不會有任何錯誤訊息。
            //
            // 2026-09-08 實證：CI（每次都是新庫，PC 一開始不存在）紅、
            // 本機（PC 早就躺在共用測試庫裡）綠，同一份程式碼。
            // 唯一會現形的是全套測試裡唯一帶小數量的那支（12.5 → 13，
            // 撞上採購單超收守門），其餘都是整數量、捨了也看不出來。
            //
            // ⚠️ 這也是為什麼「本機全綠」對這一類缺陷完全沒有證據力：
            // 差別不在程式，在那個庫有沒有被跑過。
            scope.ServiceProvider.GetRequiredService<IUomService>()
                 .InvalidateDecimalPlacesCache();
        }

        /// <summary>
        /// 測試結束再清一次。
        ///
        /// 原本只在建構子 Reset（跑之前清），跑完的產物會一直留在共用測試庫裡，
        /// 造成兩個實際踩到的後果：
        ///   ① 流程測試的期初入庫是直接呼叫 PostLineDetailAsync 而給一個假單號
        ///      （FLOW-*-OPEN），那批庫存異動沒有任何單據對應——孤兒過帳稽核
        ///      （已掛進 RunAllAsync）會把它報成爛帳，而它其實是測試自己的殘留。
        ///   ② 單據日期落在未來年份，各列表預設日期 DESC 每頁 15 筆，殘留會長期
        ///      佔住第一頁，把 UI 測試當下建的單擠到第二頁去。
        /// </summary>
        public void Dispose()
        {
            try
            {
                // 清資料之前先驗帳：xUnit 把 Dispose 擲出的例外記成這一支測試失敗
                AssertLedgerInvariantsAsync().GetAwaiter().GetResult();
            }
            finally
            {
                using var scope = NewScope(out var db);
                foreach (var p in AllPlants) PurgeAsync(db, p).GetAwaiter().GetResult();
                GC.SuppressFinalize(this);
            }
        }

        // ── 每支流程測試結束時自動驗帳 ─────────────────────────────────────────

        /// <summary>
        /// 這支測試要略過的不變量（稽核代碼，如 "INV_ROLLFORWARD"）與理由。只給「測試本身就是在製造那種異常」的用，
        /// 例如測稽核會不會抓到不平衡；一般流程不得覆寫——略過等於這支測試不再守帳。
        /// </summary>
        protected virtual IReadOnlyDictionary<string, string> SkippedInvariants { get; } = new Dictionary<string, string>();

        private readonly Dictionary<string, string> _expectedViolations = new();

        /// <summary>
        /// 這一支測試<b>本來就在製造</b>某一種帳務異常（例如驗證稽核抓不抓得到），結束時不把它當成失敗。
        /// 在測試方法裡呼叫，只影響這一支；理由必填——它會被人讀。
        /// </summary>
        protected void ExpectInvariantViolation(string auditType, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("理由必填", nameof(reason));
            _expectedViolations[auditType] = reason;
        }

        /// <summary>
        /// 「任何時刻都必須成立」的帳務不變量（2026-10-21 起每支流程測試結束時自動檢查）。
        ///
        /// <para><b>為什麼</b>：原本各流程測試只斷言自己關心的幾個數字，有 9 個類別完全沒跑全域檢核——
        /// 某支測試順帶弄錯了另一個科目、另一個儲位的數量，它照樣綠。掛在基底上，131 支一次全變成驗帳，
        /// 以後新增的也自動適用。</para>
        ///
        /// <para><b>只挑跟流程進度無關的</b>：出貨未開票、GR/IR 未沖、工單未領料這類在流程中途本來就成立，
        /// 不能拿來當不變量。這裡的每一條，只要有一筆不符就是帳錯了：每張傳票借貸平衡、不重複立帳、
        /// 庫存異動加總＝在手、成本層總量＝在手、總帳存貨＝明細帳價值、採購已收／訂單已出＝來源單據加總、不超收不超出、
        /// 沒有孤兒過帳、退貨後應收應付已回沖。</para>
        /// </summary>
        protected async Task AssertLedgerInvariantsAsync()
        {
            using var scope = NewScope(out var db);
            var audit = scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.ACT.Services.IDataAuditService>();
            var problems = new List<string>();
            foreach (var plant in AllPlants)
            {
                // 每一張傳票都平衡（不像 AssertEveryEntryBalancedAsync 要求至少一張：有些流程不產生傳票）
                var unbalanced = await db.JournalEntries.IgnoreQueryFilters()
                    .Where(j => j.PlantId == plant)
                    .Select(j => new { j.JournalId, Debit = j.Lines.Sum(l => l.Debit), Credit = j.Lines.Sum(l => l.Credit) })
                    .Where(e => e.Debit != e.Credit)
                    .ToListAsync();
                problems.AddRange(unbalanced.Select(e => $"[{plant}] 傳票 {e.JournalId} 借貸不平衡：借 {e.Debit} / 貸 {e.Credit}"));

                var findings = new List<SmartFactory.MES.Modules.ACT.Services.AuditFailureDetail>();
                findings.AddRange(await audit.AuditJournalBalanceAsync(plant));
                findings.AddRange(await audit.AuditDuplicateJournalAsync(plant));
                findings.AddRange(await audit.AuditInventoryRollForwardAsync(plant));
                findings.AddRange(await audit.AuditFifoLayerBalanceAsync(plant));
                // 總帳存貨科目＝明細帳價值（扣在製／客供／借出）。殘差不為 0 時一併印出 INV_GL_DOC_DIFF——那是找來源用的指路明細
                findings.AddRange(await audit.AuditInventoryGlSubledgerAsync(plant));
                findings.AddRange(await audit.AuditPoReceivedQtyDriftAsync(plant));
                findings.AddRange(await audit.AuditSoShippedQtyDriftAsync(plant));
                findings.AddRange(await audit.AuditPoOverReceiptAsync(plant));
                findings.AddRange(await audit.AuditSoOverShipmentAsync(plant));
                findings.AddRange(await audit.AuditUnreversedReturnSubledgerAsync(plant));
                findings.AddRange((await audit.AuditOrphanPostingAsync(plant))
                    // 流程測試常直接呼叫過帳服務、給沒有單據的假單號：FLOW-* 一律是（含期初 FLOW-*-OPEN），
                    // 另有幾支期初用 RPT-OPEN-1、SRT-GS-1-OPEN（見 Dispose 上方說明）。真單據的孤兒仍照抓
                    .Where(f => !f.DocId.StartsWith("FLOW-", StringComparison.Ordinal)
                             && !f.DocId.Contains("-OPEN", StringComparison.Ordinal)));

                // 指路明細跟著它所解釋的殘差走：預期殘差的測試不必再另外預期明細
                static string Kind(string t) => t == "INV_GL_DOC_DIFF" ? "INV_GL_SUBLEDGER" : t;
                problems.AddRange(findings
                    .Where(f => !SkippedInvariants.ContainsKey(Kind(f.AuditType)) && !_expectedViolations.ContainsKey(Kind(f.AuditType)))
                    .Select(f => $"[{plant}] {f.AuditType} {f.DocId} {f.ProductCode}：差 {f.DifferenceAmount}（{f.ErrorDescription}）"));
            }
            Assert.True(problems.Count == 0,
                "流程結束時帳務不變量不成立（FlowTestBase 自動檢查）：\n  " + string.Join("\n  ", problems));
        }

        private static async Task PurgeAsync(ApplicationDbContext db, string plant)
        {
            var wh = $"{plant}-WH";
            // 由子表往父表刪；全部以本廠別為界。JournalLines/各單據明細多由 DB 串聯刪除，
            // 但明細表未必都帶 PlantId，故一律先以父鍵撈出子列再刪，避免殘留。
            var journalIds = await db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.PlantId == plant).Select(j => j.JournalId).ToListAsync();
            if (journalIds.Count > 0)
            {
                await db.JournalLines.IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalId)).ExecuteDeleteAsync();
                await db.JournalEntries.IgnoreQueryFilters()
                    .Where(j => j.PlantId == plant).ExecuteDeleteAsync();
            }

            await db.APPayments.IgnoreQueryFilters().Where(p => p.PlantId == plant).ExecuteDeleteAsync();
            await db.ARReceipts.IgnoreQueryFilters().Where(r => r.PlantId == plant).ExecuteDeleteAsync();
            // 折讓單明細指回發票明細，須先於發票刪
            await db.ARCreditNotes.IgnoreQueryFilters().Where(n => n.PlantId == plant).ExecuteDeleteAsync();
            await db.APDebitNotes.IgnoreQueryFilters().Where(n => n.PlantId == plant).ExecuteDeleteAsync();
            await db.APInvoices.IgnoreQueryFilters().Where(i => i.PlantId == plant).ExecuteDeleteAsync();
            await db.ARInvoices.IgnoreQueryFilters().Where(i => i.PlantId == plant).ExecuteDeleteAsync();

            await db.StockMovements.IgnoreQueryFilters().Where(m => m.PlantId == plant).ExecuteDeleteAsync();
            await db.InventoryCostLayers.IgnoreQueryFilters().Where(l => l.PlantId == plant).ExecuteDeleteAsync();
            await db.Inventories.IgnoreQueryFilters().Where(i => i.PlantId == plant).ExecuteDeleteAsync();

            // 銷退單先於出貨單刪：銷退明細以 (GoodsShipmentId, ShipmentLineNo) 指回出貨明細。
            await db.SalesReturns.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();
            await db.GoodsShipments.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();
            // 進貨退出單先於進貨單刪，理由與上一組完全對稱（明細以 (GoodsReceiptId, ReceiptLineNo) 指回）。
            // 2026-09-08 補：這一列原本漏了，而漏的後果不是紅燈是**下一次才紅**——
            // 本輪測試留下的退貨單會在下一輪撞 PK_GoodsReturns，錯誤指向建單那一行，
            // 看起來像測試自己寫錯。既有的採購退貨測試各用自己的廠別，所以一直沒撞到。
            await db.GoodsReturns.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();
            await db.GoodsReceipts.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();
            await db.SalesOrders.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();
            await db.PurchaseOrders.IgnoreQueryFilters().Where(d => d.PlantId == plant).ExecuteDeleteAsync();

            await db.BankAccounts.IgnoreQueryFilters().Where(b => b.PlantId == plant).ExecuteDeleteAsync();
            await db.StorageLocations.IgnoreQueryFilters().Where(l => l.WhCode == wh).ExecuteDeleteAsync();
            await db.Warehouses.IgnoreQueryFilters().Where(w => w.PlantId == plant).ExecuteDeleteAsync();
        }

        private async Task SeedMastersAsync(ApplicationDbContext db, (string Code, string Name)[] products)
        {
            if (!await db.Plants.IgnoreQueryFilters().AnyAsync(p => p.Id == Plant))
                db.Plants.Add(new SmartFactory.MES.Models.Plant
                {
                    Id = Plant, Name = $"流程測試廠 {Plant}",
                });

            if (!await db.Uoms.IgnoreQueryFilters().AnyAsync(u => u.UomCode == Uom))
                db.Uoms.Add(new Uom { UomCode = Uom, UomName = "個", DecimalPlaces = 4 });

            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == Category))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = Category, CategoryName = "流程測試類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                });

            foreach (var (code, name) in products)
                if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == code))
                    db.Products.Add(new Product
                    {
                        ProductCode = code, ProductName = name, CategoryCode = Category,
                        PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                    });

            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Supplier))
                db.Companies.Add(new Company
                {
                    CompanyCode = Supplier, CompanyName = "流程測試供應商",
                    IsSupplier = true, IsActive = true,
                });
            if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.CompanyCode == Customer))
                db.Companies.Add(new Company
                {
                    CompanyCode = Customer, CompanyName = "流程測試客戶",
                    IsCustomer = true, IsActive = true,
                });

            db.Warehouses.Add(new Warehouse { WhCode = Wh, WhName = "流程測試倉", PlantId = Plant });
            db.StorageLocations.Add(new StorageLocation { WhCode = Wh, LocationCode = Loc, LocationName = "流程測試儲位" });
            db.BankAccounts.Add(new BankAccount
            {
                AccountId = Bank, BankName = "流程測試銀行", AccountNo = "0000",
                Balance = 1_000_000m, IsActive = true, PlantId = Plant,
            });

            await db.SaveChangesAsync();
        }

        // ── 斷言輔助 ────────────────────────────────────────────────────────

        /// <summary>本廠別總帳逐科目淨額（借 − 貸）。</summary>
        protected static async Task<Dictionary<string, decimal>> GlNetByAccountAsync(
            ApplicationDbContext db, string plant)
            => await db.JournalLines.IgnoreQueryFilters()
                .Where(l => l.JournalEntry!.PlantId == plant)
                .GroupBy(l => l.AccountCode)
                .Select(g => new { Account = g.Key, Net = g.Sum(x => x.Debit) - g.Sum(x => x.Credit) })
                .ToDictionaryAsync(x => x.Account, x => x.Net);

        /// <summary>每一張傳票都必須借貸平衡（不是只看全廠合計，那會互相掩蓋）。</summary>
        protected static async Task AssertEveryEntryBalancedAsync(ApplicationDbContext db, string plant)
        {
            var perEntry = await db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.PlantId == plant)
                .Select(j => new
                {
                    j.JournalId,
                    Debit  = j.Lines.Sum(l => l.Debit),
                    Credit = j.Lines.Sum(l => l.Credit),
                })
                .ToListAsync();

            Assert.NotEmpty(perEntry);
            Assert.All(perEntry, e =>
                Assert.True(e.Debit == e.Credit,
                    $"傳票 {e.JournalId} 借貸不平衡：借 {e.Debit} / 貸 {e.Credit}"));
        }

        protected static async Task<decimal> QtyOnHandAsync(
            ApplicationDbContext db, string plant, string product)
            => await db.Inventories.IgnoreQueryFilters()
                .Where(i => i.PlantId == plant && i.ProductCode == product)
                .SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m;

        protected static decimal Net(Dictionary<string, decimal> gl, string account)
            => gl.TryGetValue(account, out var v) ? v : 0m;
    }
}
