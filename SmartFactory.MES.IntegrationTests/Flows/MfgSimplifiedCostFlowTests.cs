using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收 Wave S 的 S-9（`manual-test-plan.md` §10.1）：**簡化模式下工單的料工費三段**
    /// 都要有值且可追溯。
    ///
    /// <para><b>這一支補的是「下游後果」，不是模式判定。</b>「簡化模式會自動投產」已由
    /// <c>MfgSimplifiedModeTests.Expand_SimplifiedOn_AutoReleasesWorkOrder</c> 與
    /// <c>MrpAutoExpandServiceTests</c> 涵蓋，本測試從那裡結束的地方接手：
    /// 自動投產→領料→開工累工費→完工入庫，走完整條鏈之後三段成本合不合得起來。
    /// 故直接呼叫 <c>AutoReleaseAsync</c>（＝<c>MrpEngine.AutoReleaseIfSimplifiedAsync</c>
    /// 對每張工單做的事），不重跑一次 MRP 展開。</para>
    ///
    /// <para><b>為什麼 §10.1 說 S-9 是「S-6 ④⑥ 沒做到時唯一會顯性爆出來的地方」</b>：
    /// ④ 標準成本快照與 ⑥ Push 領料單各有專測（<c>WorkOrderReleaseServiceTests</c>），
    /// 但那兩支證明的是「投產當下有產生」。⑥ 真正的失效形狀是**產生了卻沒人領**或
    /// **根本沒產生**——兩者的症狀都是 <c>ActualMaterialCost</c> 短計，而畫面上完全看不出異常，
    /// 要到成本或月結對帳才會現形。這裡把那個對帳點釘成測試。</para>
    ///
    /// <para>⚠️ 三段一起斷言，不分三支：先紅的那一條會把另外兩條藏起來，
    /// 而 S-9 要問的正是「三段是不是同一筆帳」。</para>
    /// </summary>
    [Collection("Integration")]
    public class MfgSimplifiedCostFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGX";

        private const string Mat = "FLOW-MFGX-MAT";
        private const string Fg  = "FLOW-MFGX-FG";
        private const string Batch = "FLOW-MFGX-PB";
        private const string Wo    = "FLOW-MFGX-WO";
        private const string Disp  = "FLOW-MFGX-DP";
        private const string Wc    = "FLOW-MFGX-WC";
        private const string RawCat = "FLOW-MFGX-CAT";

        // 工作中心費率：兩腿都非 0，否則「三段都有值」這個斷言測不到東西
        private const decimal LaborRate   = 300m;   // 元/小時
        private const decimal MachineRate = 600m;   // 元/小時

        private static readonly DateOnly StockDate = new(2098, 3, 4);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgSimplifiedCostFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "簡化模式用料"), (Fg, "簡化模式成品"));

            using var scope = NewScope(out var db);

            var slipIds = await db.MaterialSlips.IgnoreQueryFilters()
                .Where(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo)
                .Select(s => s.SlipId).ToListAsync();
            if (slipIds.Count > 0)
            {
                await db.MaterialSlipLines.IgnoreQueryFilters()
                    .Where(l => slipIds.Contains(l.SlipId)).ExecuteDeleteAsync();
                await db.MaterialSlips.IgnoreQueryFilters()
                    .Where(s => slipIds.Contains(s.SlipId)).ExecuteDeleteAsync();
            }

            await db.ProductionSessions.IgnoreQueryFilters()
                .Where(s => s.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();

            // 基底不管工作中心，得自己來回收拾——留著會讓下一輪撞主鍵
            await db.Set<WorkCenter>().IgnoreQueryFilters()
                .Where(w => w.WorkCenterCode == Wc).ExecuteDeleteAsync();
            db.Set<WorkCenter>().Add(new WorkCenter
            {
                WorkCenterCode = Wc, WorkCenterName = "簡化模式工作中心",
                StandardLaborCost = LaborRate, MachineHourlyRate = MachineRate,
                IsProductionLine = false, PlantId = Plant,
            });

            // 領料要解得出來源儲位，靠的是產品類別預設倉儲。另開類別而不動共用的 FLOW-CAT——
            // 那個是全域的，改它會波及其他流程測試。
            if (!await db.ProductCategories.IgnoreQueryFilters().AnyAsync(c => c.CategoryCode == RawCat))
                db.ProductCategories.Add(new ProductCategory
                {
                    CategoryCode = RawCat, CategoryName = "簡化模式原料類別",
                    CostMethod = CostMethod.MovingAverage, IsActive = true,
                    DefaultWarehouseCode = Wh, DefaultLocationCode = Loc,
                });
            await db.SaveChangesAsync();

            var mat = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.ProductCode == Mat);
            mat.CategoryCode = RawCat;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 訂單核准→自動投產→領料→開工下工→完工入庫，走完之後料工費三段都要有值，
        /// 而且每一段都對得回它的來源：料＝領用量×單價、工＝工時×人工費率、費＝工時×機器費率。
        /// </summary>
        [Fact]
        public async Task SimplifiedAutoRelease_ThroughCompletion_BooksAllThreeCostLegs()
        {
            const decimal openQty = 500m, matCost = 50m;
            const decimal woQty = 100m, pushReq = 200m;
            const decimal workedHours = 2m;

            await SeedOpeningStockAsync(openQty, matCost, Mat);
            await SeedPlannedWorkOrderAsync(woQty, pushReq, matCost);

            // ── ④ 標準成本快照：跳過的話成本差異分析與 CST 成本鏈全空 ──────────
            using (var scope = NewScope(out var db))
            {
                var preview = await Svc<IWorkOrderReleaseService>(scope).AutoReleaseAsync(Wo);
                Assert.Equal(WorkOrderReleaseOutcome.Released, preview.Outcome);

                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Released, wo.Status);
                Assert.True(wo.StandardMaterialCost > 0 && wo.StandardLaborCost > 0 && wo.StandardOverheadCost > 0,
                    $"標準成本三段應皆非 0，實得 料={wo.StandardMaterialCost}／" +
                    $"工={wo.StandardLaborCost}／費={wo.StandardOverheadCost}");
            }

            // ── ⑥ Push 領料單：自動產生了，還得真的領得出去 ────────────────────
            string slipId;
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleOrDefaultAsync(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo);
                Assert.True(slip != null, "簡化模式自動投產應產生 Push 領料單（§25.3 ⑥）——" +
                                          "沒有的話庫存不扣、ActualMaterialCost 短計，而畫面完全看不出異常");
                slipId = slip!.SlipId;

                // 自動產生的是 Draft 且**不帶來源儲位**（`CreatePushMaterialSlips` 只寫品號與數量），
                // 由現場補——這是真實流程，不是測試的權宜
                Assert.All(slip.Lines, l => Assert.True(string.IsNullOrEmpty(l.FromLocationCode)));
            }

            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters()
                    .Include(s => s.Lines).SingleAsync(s => s.SlipId == slipId);
                Assert.True(await Slips.SaveAsync(new MaterialSlip
                {
                    SlipId = slip.SlipId, SlipNo = slip.SlipNo, Category = slip.Category,
                    Status = slip.Status, SlipDate = StockDate,
                    SourceDocType = slip.SourceDocType, SourceDocId = slip.SourceDocId,
                    Lines =
                    {
                        new MaterialSlipLine
                        {
                            SlipId = slip.SlipId, LineNo = 1, ProductCode = Mat,
                            RequestedQty = pushReq, UomCode = Uom,
                            FromWarehouseCode = Wh, FromLocationCode = Loc, LotNo = "",
                        },
                    },
                }), "補來源儲位存檔應成功");
            }
            await IssueSlipAsync(slipId);

            // ── 工費兩腿：一段真實的上下工 ─────────────────────────────────────
            await AccrueOneSessionAsync(woQty, workedHours);

            // ── 完工入庫：回寫 ActualMaterialCost ──────────────────────────────
            using (var scope = NewScope(out var db))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);

                var expectedMat   = pushReq * matCost;          // 200 × 50 = 10,000
                var expectedLabor = workedHours * LaborRate;    //   2 × 300 =    600
                var expectedOvh   = workedHours * MachineRate;  //   2 × 600 =  1,200

                // 三段一起報：先紅的那一條會把另外兩條藏起來，而這支測的正是「三段是同一筆帳」
                Assert.True(
                    wo.ActualMaterialCost == expectedMat
                 && wo.ActualLaborCost    == expectedLabor
                 && wo.ActualOverheadCost == expectedOvh,
                    $"實際成本三段應可追溯回來源。" +
                    $"料 {wo.ActualMaterialCost}（應為 {expectedMat}＝領用 {pushReq} × 單價 {matCost}）、" +
                    $"工 {wo.ActualLaborCost}（應為 {expectedLabor}＝工時 {workedHours} × 費率 {LaborRate}）、" +
                    $"費 {wo.ActualOverheadCost}（應為 {expectedOvh}＝工時 {workedHours} × 費率 {MachineRate}）");

                // 完工入庫那筆異動的單位成本要把三段都吃進去——只算兩段也會是個「有值」的
                // 數字，看不出少了誰；而成品的帳面價值就是從這裡來的
                var it = await db.StockMovements.IgnoreQueryFilters()
                    .Where(m => m.PlantId == Plant && m.ProductCode == Fg && m.TypeCode == "IT")
                    .SingleAsync();
                Assert.Equal((expectedMat + expectedLabor + expectedOvh) / woQty, it.UnitCost);
            }
        }

        // ── 前置 ─────────────────────────────────────────────────────────────

        private async Task SeedOpeningStockAsync(decimal qty, decimal cost, params string[] products)
        {
            using var scope = NewScope(out var db);
            var posting = Svc<IInventoryPostingService>(scope);
            int line = 1;
            foreach (var code in products)
                await posting.PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGX-OPEN", LineNo = line++,
                    ProductCode = code, LocationCode = Loc, LotNo = "",
                    Qty = qty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = cost,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
            await db.SaveChangesAsync();
        }

        /// <summary>建一張 Planned 工單——MRP 展開的產物，也是 AutoReleaseAsync 的輸入。</summary>
        private async Task SeedPlannedWorkOrderAsync(decimal woQty, decimal pushReq, decimal unitCost)
        {
            using var scope = NewScope(out var db);
            db.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = Batch, MrpRunId = "FLOW-MFGX-MRP", MrpRunLineSeq = 1,
                ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
            });

            var wo = new WorkOrder
            {
                WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                RoutingId = "FLOW-MFGX-RT", Quantity = woQty,
                Status = WorkOrderStatus.Planned, PlantId = Plant, CreatedByRoleId = RoleId,
                PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
            var op = new WorkOrderOperation
            {
                WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10",
                WorkCenterId = Wc, UnitTime = 1m,
            };
            op.Materials.Add(new WorkOrderMaterial
            {
                WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                RequiredQty = pushReq, UomCode = Uom, UnitCost = unitCost,
                IssueMethod = MaterialIssueMethod.Push,
                BackflushTiming = BackflushTiming.AtCompletion,
            });
            wo.Operations.Add(op);
            db.WorkOrders.Add(wo);
            await db.SaveChangesAsync();
        }

        private async Task IssueSlipAsync(string slipId)
        {
            (await Slips.ApproveAsync(slipId)).AssertOk($"核准 {slipId}");
            (await Slips.MarkPickingAsync(slipId)).AssertOk($"開始揀料 {slipId}");
            (await Slips.ConfirmAsync(slipId)).AssertOk($"確認揀料 {slipId}");
            (await Slips.IssueAsync(slipId)).AssertOk($"過帳 {slipId}");
        }

        /// <summary>一段固定時長的上下工，並把工單推到「已完工待入庫」。</summary>
        private async Task AccrueOneSessionAsync(decimal woQty, decimal hours)
        {
            using var scope = NewScope(out var db);

            // 派工必須先存在——AccrueOnSessionStopAsync 是靠 session→dispatch→工序→工作中心
            // 一路找費率的，任何一環查不到就「靜靜地什麼都不累計」，不會報錯
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                Quantity = woQty, QtyDone = woQty,
                Status = DispatchStatus.Closed, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            await db.SaveChangesAsync();

            var start = StockDate.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
            var session = new ProductionSession
            {
                SessionId = "FLOW-MFGX-SESS", DispatchId = Disp, WorkOrderId = Wo,
                Status = ProductionSessionStatus.Closed,
                StartAt = start, EndAt = start.AddHours((double)hours),
                OperatorId = UserId, MachineCode = "FLOW-MFGX-M", PlantId = Plant,
            };
            db.ProductionSessions.Add(session);
            await Svc<IProductionCostAccrualService>(scope).AccrueOnSessionStopAsync(session);

            var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
            wo.Status = WorkOrderStatus.Completed;
            await db.SaveChangesAsync();
        }
    }
}
