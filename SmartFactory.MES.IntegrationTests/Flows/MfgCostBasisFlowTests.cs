using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 Wave 2 的 2-6：工單料成本必須等於**實際領料成本**，
    /// 而不是 BOM／建單當下的標準量推算。
    ///
    /// 與 <see cref="MfgCompletionFlowTests"/> 的分工：那支的原料只入庫一次，
    /// 且 <c>WorkOrderMaterial.UnitCost</c> 被設成與庫存成本同一個數字——「用工單快照價」
    /// 和「用實際出庫成本」兩種口徑在那個情境下必然相等，測不出差別。本測試刻意讓兩者
    /// 分開：原料分兩批以不同單價入庫（移動平均被拉高），而工單的用料單價停在建單當下
    /// 的舊價。這是實務常態——工單開立到實際投產之間，原料行情會動。
    ///
    /// 判準只有一條，且與發料方式無關：**生產是形態轉換，不得憑空增減存貨價值**。
    /// 出庫按實際成本扣、入庫按工單成本進，兩個口徑只要不同，差額就會憑空出現在
    /// 存貨科目裡，而且沒有任何一張傳票會不平衡（每張各自平衡，錯的是金額本身）。
    /// </summary>
    [Collection("Integration")]
    public class MfgCostBasisFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGC";

        private const string Mat   = "FLOW-MFGC-MAT";
        private const string Fg    = "FLOW-MFGC-FG";
        private const string Batch = "FLOW-MFGC-PB";
        private const string Wo    = "FLOW-MFGC-WO";
        private const string Disp  = "FLOW-MFGC-DP";

        private static readonly DateOnly StockDate = new(2097, 7, 1);

        public MfgCostBasisFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "成本口徑原料"), (Fg, "成本口徑成品"));
            using var scope = NewScope(out var db);
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
        }

        [Fact]
        public async Task Backflush_MaterialCostFollowsActualIssueCost_NotWorkOrderSnapshotPrice()
        {
            const decimal batch1Qty = 300m, batch1Price = 40m;   // 建單當下的行情
            const decimal batch2Qty = 300m, batch2Price = 60m;   // 投產前又進了一批較貴的
            const decimal woQty = 100m, qtyDone = 100m;
            const decimal requiredQty = 3m;                      // 每個成品耗 3 個原料
            const decimal laborCost = 8_000m, ohCost = 12_000m;

            const decimal backflushed = requiredQty * qtyDone;                       // 300
            const decimal openingVal  = batch1Qty * batch1Price + batch2Qty * batch2Price;  // 30,000
            // 移動平均：(300×40 + 300×60) / 600 = 50
            const decimal movingAvg   = 50m;
            const decimal actualIssueCost = backflushed * movingAvg;                 // 15,000

            // ── ① 原料兩批入庫，單價不同 → 移動平均被拉高 ────────────────────
            // 一批一個 scope：移動均價是查資料庫算的，同一個未存檔的交易裡連續兩批
            // 會讀不到前一批（第二批會算成「只有自己」）。正式路徑每次上架都是獨立請求，
            // 這裡照著做，否則測到的是測試自己的交易邊界而不是系統行為。
            foreach (var (qty, price, seq) in new[] { (batch1Qty, batch1Price, 1), (batch2Qty, batch2Price, 2) })
            {
                using var scope = NewScope(out var db);
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = $"FLOW-MFGC-OPEN{seq}", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = price,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
            {
                var inv = await db.Inventories.IgnoreQueryFilters()
                    .SingleAsync(i => i.PlantId == Plant && i.ProductCode == Mat);
                Assert.Equal(movingAvg, inv.UnitCost);   // 前提成立，後面的比較才有意義
            }

            // ── ② 工單：用料單價停在建單當下的舊價（40），刻意與現行庫存成本不同 ──
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFGC-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGC-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Completed,
                    ActualLaborCost = laborCost, ActualOverheadCost = ohCost,
                    PlantId = Plant,
                });
                db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WCC", UnitTime = 1m,
                });
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty * woQty, UomCode = Uom,
                    UnitCost = batch1Price,                       // ← 建單快照價，非現行成本
                    IssueMethod = MaterialIssueMethod.Backflush,
                    BackflushTiming = BackflushTiming.AtCompletion,
                });
                db.Dispatches.Add(new Dispatch
                {
                    DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                    Quantity = woQty, QtyDone = qtyDone,
                    Status = DispatchStatus.Closed, PlantId = Plant,
                });
                await db.SaveChangesAsync();
            }

            // ── ③ 完工入庫 ─────────────────────────────────────────────────
            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            // ── ④ 成本口徑：料成本＝實際領料成本 ────────────────────────────
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(actualIssueCost, wo.ActualMaterialCost);

                var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                    .SingleAsync(c => c.WorkOrderId == Wo);
                Assert.Equal(actualIssueCost, cost.ActualMaterial);
            }

            // ── ⑤ 總帳：形態轉換不得憑空增減存貨價值 ────────────────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 存貨淨額 = 期初原料總值 + 本工單投入的人工與製費，不多不少
                Assert.Equal(openingVal + laborCost + ohCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));
                Assert.Equal(0m, gl.Values.Sum());

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditBackflushConsumptionAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }
    }
}
