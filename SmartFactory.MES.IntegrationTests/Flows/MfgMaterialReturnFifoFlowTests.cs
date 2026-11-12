using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 的 2-8（退料）在 **FIFO** 計價下的版本。
    ///
    /// 為什麼要另開一支：<see cref="FlowTestBase"/> 把測試用的產品類別一律建成
    /// <c>CostMethod.MovingAverage</c>，於是**所有** Flows 測試都跑不到
    /// <c>InventoryPostingService</c> 的 FIFO 分支——而正式環境的原料一律 FIFO。
    /// 實機驗收因此踩到一個測試全綠、資料卻已經壞掉的缺陷（RUN-LOG W2-F8）：
    /// 退料是入庫，但不帶單價時會落到 <c>ConsumeLayersAsync</c>，
    /// 對一筆**入庫**去扣成本層——數量加回來、成本層被扣光，
    /// 之後這支料的每一次領用都以 0 元計價，而且沒有任何一張傳票不平衡、
    /// 沒有任何稽核會響（INV_ROLLFORWARD 只比數量，而數量是對的）。
    ///
    /// 兩層期初（不同單價）是刻意的：單一層的話「退料單價」不論取發料當下的
    /// 加權平均、還是取現存最舊那層，都會是同一個數字，測了等於沒測。
    /// </summary>
    [Collection("Integration")]
    public class MfgMaterialReturnFifoFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGFIFO";

        private const string FifoCategory = "FLOW-CAT-FIFO";
        private const string Mat          = "FLOW-MFGFIFO-MAT";
        private const string Fg           = "FLOW-MFGFIFO-FG";
        private const string Batch        = "FLOW-MFGFIFO-PB";
        private const string Wo           = "FLOW-MFGFIFO-WO";
        private const string IssueSlip    = "FLOW-MFGFIFO-MI";
        private const string RtnSlip      = "FLOW-MFGFIFO-MX";

        private static readonly DateOnly StockDate = new(2097, 9, 1);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgMaterialReturnFifoFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Fg, "FIFO 退料流程成品"));

            using var scope = NewScope(out var db);
            await db.MaterialSlipLines.IgnoreQueryFilters()
                .Where(l => l.SlipId == IssueSlip || l.SlipId == RtnSlip).ExecuteDeleteAsync();
            await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SlipId == IssueSlip || s.SlipId == RtnSlip).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();

            // FIFO 專屬類別與原料（基底只建移動平均那一組）
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == FifoCategory))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = FifoCategory, CategoryName = "流程測試類別（FIFO）",
                    CostMethod = CostMethod.Fifo, IsActive = true,
                });
            if (!await db.Products.IgnoreQueryFilters().AnyAsync(p => p.ProductCode == Mat))
                db.Products.Add(new Product
                {
                    ProductCode = Mat, ProductName = "FIFO 退料流程原料", CategoryCode = FifoCategory,
                    PurchaseUom = Uom, StockUom = Uom, SalesUom = Uom, IsActive = true,
                });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Return_AddsCostLayerAtIssueTimeCost_AndKeepsLayerLedgerInSyncWithOnHand()
        {
            // 兩層期初：60 個 @ 50、40 個 @ 100
            const decimal qtyA = 60m, priceA = 50m;
            const decimal qtyB = 40m, priceB = 100m;
            const decimal issuedQty = 80m;      // 吃光 A 層 60 ＋ B 層 20
            const decimal returnedQty = 10m;

            // 發料當下的加權平均 = (60×50 + 20×100) / 80 = 62.5
            const decimal issueUnitCost = (qtyA * priceA + 20m * priceB) / issuedQty;
            const decimal returnValue   = returnedQty * issueUnitCost;   // 625

            await SeedOpeningLayerAsync("FLOW-MFGFIFO-OPEN-A", 1, qtyA, priceA);
            await SeedOpeningLayerAsync("FLOW-MFGFIFO-OPEN-B", 2, qtyB, priceB);
            await SeedWorkOrderAsync();

            await SeedSlipAsync(IssueSlip, "MI-FIFO-1", MaterialSlipCategory.InternalIssue,
                                issuedQty, fromLoc: Loc, toLoc: null);
            (await Slips.IssueAsync(IssueSlip)).AssertOk($"過帳 {IssueSlip}");

            using (var scope = NewScope(out var db))
            {
                var mv = await db.StockMovements.IgnoreQueryFilters().SingleAsync(m => m.DocNo == IssueSlip);
                Assert.Equal(-1, mv.Direction);
                Assert.Equal(issueUnitCost, mv.UnitCost);
            }

            await SeedSlipAsync(RtnSlip, "MX-FIFO-1", MaterialSlipCategory.InternalReturn,
                                returnedQty, fromLoc: Loc, toLoc: Loc);
            (await Slips.IssueAsync(RtnSlip)).AssertOk($"過帳 {RtnSlip}");

            using (var scope = NewScope(out var db))
            {
                var rtn = await db.StockMovements.IgnoreQueryFilters().SingleAsync(m => m.DocNo == RtnSlip);
                Assert.Equal(1, rtn.Direction);
                Assert.Equal(issueUnitCost, rtn.UnitCost);
                Assert.Equal(returnValue, rtn.TotalCost);

                // 數量：100 − 80 + 10 = 30
                var onHand = await QtyOnHandAsync(db, Plant, Mat);
                Assert.Equal(qtyA + qtyB - issuedQty + returnedQty, onHand);

                // 成本層必須與在手同步——這正是 W2-F8 壞掉的地方：
                // 退料若走 ConsumeLayersAsync，層數量會變成 20-10=10 而在手是 30。
                var layerQty = await db.InventoryCostLayers.IgnoreQueryFilters()
                    .Where(l => l.PlantId == Plant && l.ProductCode == Mat)
                    .SumAsync(l => l.QtyRemaining);
                Assert.Equal(onHand, layerQty);

                // 價值：剩下的 B 層 20 個 @100 ＋ 退回的 10 個 @62.5
                var layerValue = await db.InventoryCostLayers.IgnoreQueryFilters()
                    .Where(l => l.PlantId == Plant && l.ProductCode == Mat)
                    .SumAsync(l => l.QtyRemaining * l.UnitCost);
                Assert.Equal(20m * priceB + returnValue, layerValue);
            }
        }

        [Fact]
        public async Task Return_WithoutAnyPostedIssue_IsRejected_RatherThanCorruptingCostLayers()
        {
            await SeedOpeningLayerAsync("FLOW-MFGFIFO-OPEN-C", 1, 10m, 50m);
            await SeedWorkOrderAsync();

            // 沒有任何已過帳的發料單就直接退料：退回來的料值多少錢無從得知。
            // 舊行為是照樣過帳並倒扣成本層；正確行為是擋下來。
            await SeedSlipAsync(RtnSlip, "MX-FIFO-2", MaterialSlipCategory.InternalReturn,
                                5m, fromLoc: Loc, toLoc: Loc);
            (await Slips.IssueAsync(RtnSlip)).AssertRejected($"無發料紀錄的退料 {RtnSlip}");

            using var scope = NewScope(out var db);
            Assert.Empty(await db.StockMovements.IgnoreQueryFilters().Where(m => m.DocNo == RtnSlip).ToListAsync());
            Assert.Equal(10m, await db.InventoryCostLayers.IgnoreQueryFilters()
                .Where(l => l.PlantId == Plant && l.ProductCode == Mat).SumAsync(l => l.QtyRemaining));
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task SeedOpeningLayerAsync(string docNo, int lineNo, decimal qty, decimal price)
        {
            using var scope = NewScope(out var db);
            await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
            {
                PlantId = Plant, DocNo = docNo, LineNo = lineNo,
                ProductCode = Mat, LocationCode = Loc, LotNo = "",
                Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                SetDocPosted               = () => { },
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedWorkOrderAsync()
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-MFGFIFO-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = 10m, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                RoutingId = "FLOW-MFGFIFO-RT", Quantity = 10m,
                Status = WorkOrderStatus.InProgress, PlantId = Plant,
            });
            db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WCF", UnitTime = 1m,
            });
            db.WorkOrderMaterials.Add(new WorkOrderMaterial
            {
                WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                RequiredQty = 100m, UomCode = Uom, IssueMethod = MaterialIssueMethod.Push,
            });
            await db.SaveChangesAsync();
        }

        private async Task SeedSlipAsync(
            string slipId, string slipNo, MaterialSlipCategory category,
            decimal qty, string? fromLoc, string? toLoc)
        {
            using var scope = NewScope(out var db);
            db.MaterialSlips.Add(new MaterialSlip
            {
                SlipId = slipId, SlipNo = slipNo, Category = category,
                Status = MaterialSlipStatus.Confirmed, SlipDate = StockDate,
                SourceDocType = "WorkOrder", SourceDocId = Wo, PlantId = Plant,
                Lines =
                {
                    new MaterialSlipLine
                    {
                        SlipId = slipId, LineNo = 1, SourceLineId = "10_1",
                        ProductCode = Mat, RequestedQty = qty, ConfirmedQty = qty, UomCode = Uom,
                        FromWarehouseCode = fromLoc == null ? null : Wh,
                        FromLocationCode  = fromLoc,
                        ToWarehouseCode   = toLoc == null ? null : Wh,
                        ToLocationCode    = toLoc,
                        LotNo = "",
                    },
                },
            });
            await db.SaveChangesAsync();
        }
    }
}
