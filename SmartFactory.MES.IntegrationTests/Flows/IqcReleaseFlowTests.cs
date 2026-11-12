using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §8 的 5-5：品檢——IQC 進料檢驗放行認列 AP。
    ///
    /// 這條鏈橫跨四個模組（PUR 進貨 → INV 品質隔離 → QCR 檢驗 → FIN 應付），
    /// 每一段各自都有測試，但沒有任何一支從「貨進待驗倉」一路跑到「應付被認列」。
    /// 中間那個接縫正是最容易錯的地方：**應付認列的時機已經從「進貨單完成整批認列」
    /// 改成「進正常倉逐次累加」**（見 `IAPInvoiceService.AccrueForReceivedQtyAsync`），
    /// 所以非免驗品的認列點在 IQC 放行那一刻，不在收貨那一刻。
    ///
    /// 兩個方向都要釘：
    ///   ① 合格放行 → 貨進正常倉、應付跟著認列、暫估應付歸零；
    ///   ② **不合格 → 什麼都不許發生**。貨留在待驗倉、應付一毛不得認列。
    ///      ②比①危險：認列了應付就等於承認這批貨要付錢，而它其實正要退回去。
    ///      而且不合格是靜態的——沒有任何動作、沒有任何分錄，錯了也不會有人察覺。
    /// </summary>
    [Collection("Integration")]
    public class IqcReleaseFlowTests : FlowTestBase
    {
        protected override string Plant => PlantId;

        private const string Mat = "FLOW-W5Q-MAT";
        private const string Po  = "FLOW-W5Q-PO";

        private static readonly DateOnly OrderDate = new(2094, 3, 1);
        private static readonly DateOnly RecvDate  = new(2094, 3, 10);

        private const decimal Qty = 100m, Price = 40m, TaxRate = 0.05m;
        private const decimal Net = Qty * Price;        // 4,000
        private const decimal Tax = Net * TaxRate;      //   200
        private const decimal Gross = Net + Tax;        // 4,200

        private string QcWh  => $"QC-{Plant}";
        private string QcLoc => $"QC-{Plant}-Q01";

        public IqcReleaseFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "IQC 流程測試料"));

            using var scope = NewScope(out var db);
            await db.MobileIqcRecords.IgnoreQueryFilters()
                .Where(r => r.PlantId == Plant).ExecuteDeleteAsync();
            await db.InspectionOrders.IgnoreQueryFilters()
                .Where(o => o.PlantId == Plant).ExecuteDeleteAsync();
            await db.StockTransfers.IgnoreQueryFilters()
                .Where(t => t.PlantId == Plant).ExecuteDeleteAsync();

            // 待驗倉：QuarantineWarehouseSeeder 只在應用啟動時補，而本廠是測試建構子才建的，
            // 第一次跑的時候還不存在——自己建，且要在 FlowTestBase 清掉倉庫之後。
            await db.StorageLocations.IgnoreQueryFilters()
                .Where(l => l.WhCode == QcWh).ExecuteDeleteAsync();
            await db.Warehouses.IgnoreQueryFilters()
                .Where(w => w.WhCode == QcWh).ExecuteDeleteAsync();
            db.Warehouses.Add(new Warehouse
            {
                WhCode = QcWh, WhName = "待驗倉", WarehouseType = WarehouseType.Quarantine,
                IsMrpWarehouse = false, IsActive = true, PlantId = Plant,
            });
            db.StorageLocations.Add(new StorageLocation
            {
                WhCode = QcWh, LocationCode = QcLoc, LocationName = "待驗區", IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 合格：貨從待驗倉放行到正常倉，應付在**那一刻**才被認列。
        /// 收貨當下不得認列——貨還在待驗倉，能不能收都還不知道。
        /// </summary>
        [Fact]
        public async Task IqcPass_ReleasesStockAndOnlyThenAccruesPayable()
        {
            var grId = await SeedReceiptIntoQuarantineAsync();

            // ── 收貨後、檢驗前：貨在待驗倉，應付一毛未認列 ─────────────────
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(Qty, await QtyAtAsync(db, QcLoc));
                Assert.Equal(0m,  await QtyAtAsync(db, Loc));

                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(-Net, Net(gl, StandardAccounts.GrIrAPClearingCode));   // 貨到帳未認
                Assert.Equal(0m,   Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Empty(await db.APInvoices.IgnoreQueryFilters()
                    .Where(i => i.PlantId == Plant).ToListAsync());
            }

            // ── IQC 合格 ────────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var outcome = await IqcServiceFor(scope, db).SubmitAsync(new IqcSubmitInput(
                    GoodsReceiptId: grId, LineNo: 1, ProductCode: Mat,
                    LotSize: Qty, SampleQty: 5m, FailQty: 0m,
                    VerificationLevel: 4, Remarks: null), Plant);

                Assert.True(outcome.Success, outcome.Message);
                Assert.Equal("Pass", outcome.Result);
            }

            using (var scope = NewScope(out var db))
            {
                // 貨搬進正常倉
                Assert.Equal(0m,  await QtyAtAsync(db, QcLoc));
                Assert.Equal(Qty, await QtyAtAsync(db, Loc));

                // 應付在放行之後才認列，且金額是整批（放行量 == 收貨量）
                var invoice = await db.APInvoices.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(i => i.PlantId == Plant);
                Assert.Equal(Gross, invoice.TotalAmount);

                var gl = await GlNetByAccountAsync(db, Plant);
                Assert.Equal(0m,     Net(gl, StandardAccounts.GrIrAPClearingCode));   // 暫估沖平
                Assert.Equal(-Gross, Net(gl, StandardAccounts.AccountsPayableCode));
                Assert.Equal(Tax,    Net(gl, StandardAccounts.InputVatCode));

                await AssertEveryEntryBalancedAsync(db, Plant);

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditGrIrAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }

        /// <summary>
        /// 不合格：什麼都不許發生。
        ///
        /// 這是靜態的失效——沒有動作、沒有分錄，錯了也不會有任何訊號。
        /// 若這裡誤認列了應付，等於承認這批貨要付錢，而它其實正準備退回供應商；
        /// 之後退貨過帳沖 2152，2100 卻留著一筆沒有貨對應的應付。
        /// </summary>
        [Fact]
        public async Task IqcFail_LeavesStockInQuarantineAndAccruesNothing()
        {
            var grId = await SeedReceiptIntoQuarantineAsync();

            Dictionary<string, decimal> before;
            using (var scope = NewScope(out var db))
                before = await GlNetByAccountAsync(db, Plant);

            using (var scope = NewScope(out var db))
            {
                var outcome = await IqcServiceFor(scope, db).SubmitAsync(new IqcSubmitInput(
                    GoodsReceiptId: grId, LineNo: 1, ProductCode: Mat,
                    LotSize: Qty, SampleQty: 5m, FailQty: 1m,   // Ac=0：一件不良即拒批
                    VerificationLevel: 4, Remarks: null), Plant);

                Assert.True(outcome.Success, outcome.Message);
                Assert.Equal("Fail", outcome.Result);
            }

            using (var scope = NewScope(out var db))
            {
                // 貨原地不動
                Assert.Equal(Qty, await QtyAtAsync(db, QcLoc));
                Assert.Equal(0m,  await QtyAtAsync(db, Loc));

                // 帳一個字都不許動
                var after = await GlNetByAccountAsync(db, Plant);
                foreach (var account in before.Keys.Union(after.Keys))
                    Assert.Equal(Net(before, account), Net(after, account));

                Assert.Empty(await db.APInvoices.IgnoreQueryFilters()
                    .Where(i => i.PlantId == Plant).ToListAsync());
                Assert.Empty(await db.StockTransfers.IgnoreQueryFilters()
                    .Where(t => t.PlantId == Plant).ToListAsync());
            }
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 自行組出 IQC 服務，只為了換掉 <see cref="IUserContext"/>。
        ///
        /// `MobileIqcRecord.InspectorId` 是 NOT NULL，值取自 `_userContext.UserId`——
        /// 直接從 DI 解析會拿到沒有登入使用者的實例，寫入當場撞 23502。
        /// 這順帶說明一件事：**IQC 登錄硬性要求有登入使用者**，
        /// 任何背景排程或整合介面要呼叫它，都得先自備一個身分。
        /// </summary>
        private static IIqcInspectionService IqcServiceFor(
            IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, PlantId, RoleId);
            return new SmartFactory.MES.Modules.QCR.Services.IqcInspectionService(
                db,
                sp.GetRequiredService<IUserContext>(),
                sp.GetRequiredService<ISamplingPlanService>(),
                sp.GetRequiredService<IDocNumberService>(),
                sp.GetRequiredService<IInventoryPostingService>(),
                sp.GetRequiredService<IApprovalService>(),
                sp.GetRequiredService<IDateTimeService>(),
                sp.GetRequiredService<IAPInvoiceService>());
        }

        private const string PlantId = "ZZW5Q";

        /// <summary>
        /// 已上架到待驗倉（進貨單已過帳）的進貨要出現在待檢清單；進貨單沒填目的倉時，
        /// 合格放行到品號類別的預設倉儲並認列應付（2026-10-24：原本已過帳就從清單消失，
        /// 沒填目的倉時放行又靜默不動作——ADM PE-2609-00013 在待驗倉卡了七週）。
        /// </summary>
        [Fact]
        public async Task PostedIntoQuarantine_ListedAsPending_PassReleasesToCategoryDefault()
        {
            await SetCategoryDefaultAsync(Wh, Loc);
            try
            {
                var grId = await SeedReceiptIntoQuarantineAsync(withTarget: false);
                using (var scope = NewScope(out var db))
                {
                    var pending = await IqcServiceFor(scope, db).GetPendingLinesAsync(Plant);
                    Assert.Contains(pending, p => p.GoodsReceiptId == grId && p.LineNo == 1);
                    var outcome = await IqcServiceFor(scope, db).SubmitAsync(new IqcSubmitInput(
                        GoodsReceiptId: grId, LineNo: 1, ProductCode: Mat,
                        LotSize: Qty, SampleQty: 5m, FailQty: 0m, VerificationLevel: 4, Remarks: null), Plant);
                    Assert.True(outcome.Success, outcome.Message);
                }
                using (var scope = NewScope(out var db))
                {
                    Assert.Equal(0m, await QtyAtAsync(db, QcLoc));
                    Assert.Equal(Qty, await QtyAtAsync(db, Loc));
                    Assert.Equal(Gross, (await db.APInvoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.PlantId == Plant)).TotalAmount);
                    Assert.DoesNotContain(await IqcServiceFor(scope, db).GetPendingLinesAsync(Plant), p => p.GoodsReceiptId == grId);
                }
            }
            finally { await SetCategoryDefaultAsync(null, null); }
        }

        /// <summary>進貨單沒填目的倉、品號類別也沒有預設倉儲：合格要擋下並說明，不可記成合格卻把貨留在待驗倉。</summary>
        [Fact]
        public async Task PassWithoutReleaseTarget_IsRejected_NothingRecorded()
        {
            await SetCategoryDefaultAsync(null, null);
            var grId = await SeedReceiptIntoQuarantineAsync(withTarget: false);
            using (var scope = NewScope(out var db))
            {
                var outcome = await IqcServiceFor(scope, db).SubmitAsync(new IqcSubmitInput(
                    GoodsReceiptId: grId, LineNo: 1, ProductCode: Mat,
                    LotSize: Qty, SampleQty: 5m, FailQty: 0m, VerificationLevel: 4, Remarks: null), Plant);
                Assert.False(outcome.Success);
                Assert.Equal("NO_RELEASE_TARGET", outcome.ErrorCode);
            }
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(Qty, await QtyAtAsync(db, QcLoc));
                Assert.False(await db.MobileIqcRecords.AnyAsync(r => r.GoodsReceiptId == grId));
            }
        }

        private async Task SetCategoryDefaultAsync(string? wh, string? loc)
        {
            using var scope = NewScope(out var db);
            var cat = await db.ProductCategories.IgnoreQueryFilters().SingleAsync(c => c.CategoryCode == Category);
            cat.DefaultWarehouseCode = wh;
            cat.DefaultLocationCode = loc;
            await db.SaveChangesAsync();
        }

        private async Task<decimal> QtyAtAsync(SmartFactory.MES.Data.ApplicationDbContext db, string location)
            => await db.Inventories.IgnoreQueryFilters()
                .Where(i => i.PlantId == Plant && i.ProductCode == Mat && i.LocationCode == location)
                .SumAsync(i => (decimal?)i.QtyOnHand) ?? 0m;

        /// <summary>採購單 → 進貨單 → **上架到待驗倉**，停在「等檢驗」。</summary>
        private async Task<string> SeedReceiptIntoQuarantineAsync(bool withTarget = true)
        {
            using (var scope = NewScope(out var db))
            {
                var po = new PurchaseOrder
                {
                    PurchaseOrderId = Po, OrderDate = OrderDate, CreatedBy = UserId,
                    SupplierCode = Supplier, ApprovalStatus = DocumentApprovalStatus.Approved,
                    PlantId = Plant, CreatedByRoleId = RoleId,
                };
                po.Lines.Add(new PurchaseOrderLine
                {
                    PurchaseOrderId = Po, LineNo = 1, ProductCode = Mat,
                    OrderQty = Qty, UomCode = Uom, UnitPrice = Price, TaxRate = TaxRate,
                });
                db.PurchaseOrders.Add(po);
                await db.SaveChangesAsync();
            }

            string grId;
            using (var scope = NewScope(out var db))
            {
                var gr = await Svc<IPurchaseService>(scope).CreateGoodsReceiptAsync(new GoodsReceiptFormVm
                {
                    ReceiptDate = RecvDate, CreatedBy = UserId, SupplierCode = Supplier,
                    Lines =
                    [
                        new GoodsReceiptLineVm
                        {
                            LineNo = 1, PurchaseOrderId = Po, PoLineNo = 1, ProductCode = Mat,
                            ReceiptQty = Qty, StockQty = Qty,
                            UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                            TaxRate = TaxRate, LineAmount = Net, TotalAmount = Gross,
                            // 放行的目的地：IQC 合格時系統會把貨從待驗倉搬到這裡
                            WhCode = withTarget ? Wh : null, LocationCode = withTarget ? Loc : null,
                        }
                    ],
                }, UserId, Plant);
                grId = gr.GoodsReceiptId;
            }

            using (var scope = NewScope(out var db))
            {
                var gr = await db.GoodsReceipts.IgnoreQueryFilters()
                    .Include(g => g.Lines).SingleAsync(g => g.GoodsReceiptId == grId);
                var line = gr.Lines.Single();

                // 上架紀錄：貨實際落在待驗倉，這是 ReleaseFromQuarantineAsync 的來源
                db.Set<GoodsReceiptLocationDetail>().Add(new GoodsReceiptLocationDetail
                {
                    GoodsReceiptId = grId, LineNo = 1,
                    WhCode = QcWh, LocationCode = QcLoc, LotNo = "", Qty = Qty,
                });

                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = grId, LineNo = line.LineNo,
                    ProductCode = Mat, LocationCode = QcLoc, LotNo = "",
                    Qty = line.StockQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = RecvDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = Price,
                    UpdateQtyPutAwayAsync      = q => { line.QtyPutAway = q; return Task.CompletedTask; },
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => gr.Status = DocStatus.Posted,
                });
                await db.SaveChangesAsync();
            }

            return grId;
        }
    }
}
