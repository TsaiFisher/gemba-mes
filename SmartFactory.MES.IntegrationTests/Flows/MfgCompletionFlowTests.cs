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
    /// 製造貫穿：原料入庫 → 工單完工 → 後沖領料 → 完工繳庫 → 成本結算。
    ///
    /// 釘的是「生產只是形態轉換，不得憑空增減存貨價值」：
    ///   - 原料在手量減少、成品在手量增加
    ///   - 存貨科目淨額 = 原料成本 + 本工單投入的人工與製費（加值），不多不少
    ///   - 人工／製費以 contra 科目貸記（§16.3 吸收分錄），每張傳票各自平衡
    ///   - 工單實際成本結算記錄產生，且 §3.8 後沖用量稽核無異常
    /// </summary>
    [Collection("Integration")]
    public class MfgCompletionFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFG";

        private const string Mat   = "FLOW-MFG-MAT";
        private const string Fg    = "FLOW-MFG-FG";
        private const string Batch = "FLOW-MFG-PB";
        private const string Wo    = "FLOW-MFG-WO";
        private const string Disp  = "FLOW-MFG-DP";

        private static readonly DateOnly StockDate = new(2097, 6, 1);

        public MfgCompletionFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "製造流程原料"), (Fg, "製造流程成品"));
            using var scope = NewScope(out var db);
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
        }

        /// <summary>
        /// 兩道工序、每道都報完 100 的工單：完工入庫、後沖、單位成本都要以**最後一道工序的 100** 計，
        /// 不是兩道報工的加總 200。原本取 Σ全部派工 QtyDone——成品入庫 200、後沖原料兩倍、
        /// 單位成本攤成一半，而每張傳票照樣平衡（2026-10-05 由工單進度報表的同一個加總追到這裡）。
        /// </summary>
        [Fact]
        public async Task WorkOrderCompletion_多工序以最後一道工序的完工量入庫與後沖()
        {
            const decimal woQty = 100m, requiredPerUnit = 3m, matCost = 40m;
            const decimal laborCost = 8_000m, ohCost = 12_000m;

            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFG-OPEN2", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = 1000m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = matCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFG-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg, RoutingId = "FLOW-MFG-RT",
                    Quantity = woQty, Status = WorkOrderStatus.Completed,
                    ActualLaborCost = laborCost, ActualOverheadCost = ohCost, PlantId = Plant,
                });
                foreach (var seq in new[] { 10, 20 })
                {
                    db.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = Wo, OperationSeq = seq, WorkCenterId = "FLOW-WC", UnitTime = 1m });
                    db.Dispatches.Add(new Dispatch
                    {
                        DispatchId = $"{Disp}-{seq}", WorkOrderId = Wo, OperationSeq = seq,
                        Quantity = woQty, QtyDone = woQty, Status = DispatchStatus.Closed, PlantId = Plant,
                    });
                }
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredPerUnit * woQty, UomCode = Uom, UnitCost = matCost,
                    IssueMethod = MaterialIssueMethod.Backflush, BackflushTiming = BackflushTiming.AtCompletion,
                });
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out var db))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            using (var scope = NewScope(out var db))
            {
                Assert.Equal(woQty, await QtyOnHandAsync(db, Plant, Fg));                         // 不是 200
                Assert.Equal(1000m - requiredPerUnit * woQty, await QtyOnHandAsync(db, Plant, Mat)); // 後沖 300，不是 600
                var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters().SingleAsync(c => c.WorkOrderId == Wo);
                Assert.Equal(requiredPerUnit * woQty * matCost, cost.ActualMaterial);
            }
        }

        [Fact]
        public async Task WorkOrderCompletion_FullChain_KeepsInventoryValueAndGlConsistent()
        {
            const decimal matQty      = 500m;
            const decimal matCost     = 40m;        // 原料單位成本
            const decimal woQty       = 100m;       // 工單數量
            const decimal qtyDone     = 100m;       // 全數完工
            const decimal requiredQty = 3m;         // 每個成品耗 3 個原料
            const decimal laborCost   = 8_000m;
            const decimal ohCost      = 12_000m;
            const decimal backflushed = requiredQty * qtyDone;          // 300
            const decimal matConsumed = backflushed * matCost;          // 12,000
            const decimal openingVal  = matQty * matCost;               // 20,000

            // ── ① 原料入庫（建立成本基礎）──────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFG-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = matQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = matCost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 工單（已完工待入庫）＋ 後沖用料 ＋ 派工完工量 ───────────────
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFG-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                db.WorkOrders.Add(new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFG-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Completed,
                    ActualLaborCost = laborCost, ActualOverheadCost = ohCost,
                    PlantId = Plant,
                });
                db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, WorkCenterId = "FLOW-WC", UnitTime = 1m,
                });
                db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty * woQty, UomCode = Uom, UnitCost = matCost,
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

            // ── ③ 完工入庫（後沖領料 + 繳庫 + 成本結算 + 吸收分錄）───────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);
            }

            // ── ④ 數量與狀態 ───────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                Assert.Equal(matQty - backflushed, await QtyOnHandAsync(db, Plant, Mat));
                Assert.Equal(qtyDone,              await QtyOnHandAsync(db, Plant, Fg));

                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Closed, wo.Status);
                Assert.Equal(matConsumed, wo.ActualMaterialCost);

                var cost = await db.WorkOrderActualCosts.IgnoreQueryFilters()
                    .SingleAsync(c => c.WorkOrderId == Wo);
                Assert.Equal(matConsumed, cost.ActualMaterial);
                Assert.Equal(laborCost,   cost.ActualLabor);
                Assert.Equal(ohCost,      cost.ActualOverhead);
            }

            // ── ⑤ 總帳：生產只是形態轉換，存貨淨增額＝人工＋製費 ────────────
            using (var scope = NewScope(out var db))
            {
                await AssertEveryEntryBalancedAsync(db, Plant);
                var gl = await GlNetByAccountAsync(db, Plant);

                // 存貨 = 原料入庫 20,000 − 後沖 12,000 + 繳庫(12,000+8,000+12,000)
                Assert.Equal(openingVal + laborCost + ohCost,
                             Net(gl, StandardAccounts.RawMaterialInventoryCode));

                // 人工／製費以 contra 科目貸記（淨額為負）
                Assert.Equal(-laborCost, Net(gl, StandardAccounts.DirectLaborAbsorbedCode));
                Assert.Equal(-ohCost,    Net(gl, StandardAccounts.MfgOverheadAbsorbedCode));

                Assert.Equal(0m, gl.Values.Sum());

                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditBackflushConsumptionAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
            }
        }
    }
}
