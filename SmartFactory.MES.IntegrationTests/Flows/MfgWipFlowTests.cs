using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 人工驗收作業書 §4.1 Wave 2 的 2-7：**WIP 餘額**。
    ///
    /// 驗收書原寫「檢視 WIP 餘額與結轉傳票／完工後 WIP 應正確結轉至成品存貨」，但這套系統
    /// 沒有在製品科目，也沒有 WIP 結轉傳票：會計科目表只有單一存貨科目 1401，生產是形態轉換，
    /// 發料與完工的庫存異動根本不入總帳（<c>BuildJournalLines</c> 對 Production／Outsource 回空），
    /// 完工傳票只把加工成本（人工＋製費）借進存貨、貸記 5110／5120 吸收。
    /// WIP 因此是**評價構念**而非科目餘額：跨期由 <see cref="WipSnapshot"/> 承載，
    /// 正確性由 §3.10 的結構性守門與金額恆等式（<c>WIP_IDENTITY_DRIFT</c>）守。
    /// 本測試釘的是後者，不是驗收書那句話。
    ///
    /// 情境是 WIP 的定義本身——**料已領、工單還沒完工**。既有的 <c>WipCarryForwardTests</c>
    /// 雖然也驗恆等式，但它是直接把 <c>WorkOrder.ActualMaterialCost</c> 種在工單上；
    /// 本測試從發料過帳一路走真正的入口，讓「這個欄位在完工前到底有沒有值」自己說話。
    /// </summary>
    [Collection("Integration")]
    public class MfgWipFlowTests : FlowTestBase
    {
        protected override string Plant => "ZZMFGW";

        private const string Mat   = "FLOW-MFGW-MAT";
        private const string Fg    = "FLOW-MFGW-FG";
        private const string Batch = "FLOW-MFGW-PB";
        private const string Wo    = "FLOW-MFGW-WO";
        private const string Period = "209710";

        private static readonly DateOnly StockDate = new(2097, 10, 5);

        private MaterialSlipDriver Slips => new(Factory, Plant, RoleId);

        public MfgWipFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "WIP 流程原料"), (Fg, "WIP 流程成品"));
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

            await db.WipSnapshots.IgnoreQueryFilters().Where(w => w.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();

            // 期間留 Open：本測試要先過帳再重估，Closing 會擋住一般過帳（僅允許調整分錄）。
            if (!await db.AccountingPeriods.IgnoreQueryFilters().AnyAsync(p => p.PeriodId == Period))
                db.AccountingPeriods.Add(new AccountingPeriod
                {
                    PeriodId = Period, PlantId = Plant, Year = 2097, Month = 10,
                    StartDate = new DateOnly(2097, 10, 1), EndDate = new DateOnly(2097, 10, 31),
                    Status = PeriodStatus.Open,
                });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task IssuedButNotCompleted_MaterialStaysInWip_AndIdentityBalances()
        {
            const decimal openingQty = 600m, openingPrice = 50m;
            const decimal woQty = 100m, requiredQty = 300m;
            const decimal issuedValue = requiredQty * openingPrice;   // 15,000

            // ── ① 期初庫存 ──────────────────────────────────────────────────
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGW-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = openingQty, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = openingPrice,
                    UpdateQtyPutAwayAsync      = _ => Task.CompletedTask,
                    CheckAllLinesCompleteAsync = () => Task.FromResult(true),
                    SetDocPosted               = () => { },
                });
                await db.SaveChangesAsync();
            }

            // ── ② 工單投產核准（自動建立 Push 備料單）────────────────────────
            using (var scope = NewScope(out var db))
            {
                db.ProductionBatches.Add(new ProductionBatch
                {
                    BatchId = Batch, MrpRunId = "FLOW-MFGW-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                var wo = new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGW-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Planned, PlantId = Plant,
                    PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                };
                var op = new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10",
                    WorkCenterId = "FLOW-WCW", UnitTime = 1m,
                };
                op.Materials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty, UomCode = Uom, UnitCost = openingPrice,
                    IssueMethod = MaterialIssueMethod.Push,
                });
                wo.Operations.Add(op);
                db.WorkOrders.Add(wo);
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out _))
                Assert.Equal(WorkOrderReleaseOutcome.Released,
                    (await Svc<IWorkOrderReleaseService>(scope).ApplyAsync(Wo, confirmed: true)).Outcome);

            // ── ③ 走完備料單狀態機發料（守門本身由 MfgMaterialSlipFlowTests 驗）──
            string slipId;
            using (var scope = NewScope(out var db))
            {
                var slip = await db.MaterialSlips.IgnoreQueryFilters().Include(s => s.Lines)
                    .SingleAsync(s => s.SourceDocType == "WorkOrder" && s.SourceDocId == Wo);
                slipId = slip.SlipId;

                // 自動建單填不出發料儲位，非補不可（見 MfgMaterialSlipFlowTests 的說明）
                var line = slip.Lines.Single();
                line.FromWarehouseCode = Wh;
                line.FromLocationCode   = Loc;
                line.LotNo              = "";

                // 自動建單的單據日期取「建單當天」（真實今天），本測試的會計期間在 2097，
                // 不改的話發料異動會落在期間外，恆等式的發料腿看不到它。
                slip.SlipDate = StockDate;
                await db.SaveChangesAsync();
            }

            (await Slips.ApproveAsync(slipId)).AssertOk("核准");
            (await Slips.MarkPickingAsync(slipId)).AssertOk("開始揀料");
            (await Slips.ConfirmAsync(slipId)).AssertOk("確認揀料完成");
            (await Slips.IssueAsync(slipId)).AssertOk("過帳發料");

            // ── ④ 開工：工單進入在製，**尚未完工** ───────────────────────────
            //    開工／報工有自己的入口與測試，這裡只要工單處於在製狀態即可。
            using (var scope = NewScope(out var db))
            {
                var wo = await db.WorkOrders.IgnoreQueryFilters().SingleAsync(w => w.WorkOrderId == Wo);
                wo.Status = WorkOrderStatus.InProgress;
                await db.SaveChangesAsync();
            }

            // ── ⑤ 期末重估 → 在製品快照 ──────────────────────────────────────
            using (var scope = NewScope(out _))
                await Svc<IActualRateReestimationService>(scope).RunAsync(Period, Plant);

            using (var scope = NewScope(out var db))
            {
                // 對帳規格 §1.3 的口徑：淨發料 =（IssuedQty − ReturnedQty）× 成本
                var wom = await db.WorkOrderMaterials.IgnoreQueryFilters()
                    .SingleAsync(m => m.WorkOrderId == Wo && m.OperationSeq == 10 && m.BomSeq == 1);
                Assert.Equal(issuedValue, (wom.IssuedQty - wom.ReturnedQty) * openingPrice);

                var snap = await db.WipSnapshots.IgnoreQueryFilters()
                    .SingleAsync(w => w.PeriodId == Period && w.WorkOrderId == Wo);

                // 料已離開倉庫、成品還沒生出來——這筆錢只能在在製品裡。
                // 快照的材料腿若取「完工時才回寫的 WorkOrder.ActualMaterialCost」，
                // 在製工單就恆為 0：WIP 報表看不到已投入的料，而金額恆等式
                // （§3.10-5，發料腿讀的是庫存異動）會反過來把這筆差額報成漂移——
                // 也就是說，只要月底還有在製工單，這條稽核就不可能跑綠。
                Assert.Equal(issuedValue, snap.WipMaterial);

                // 尚未報工，人工與製費為 0；期末 WIP 即淨發料（§5.8 公式）
                Assert.Equal(
                    WipBalanceCalculator.ComputeEndingWip(
                        beginningWip: 0m, netMaterialIssued: issuedValue,
                        laborAndOverhead: 0m, completionCost: 0m),
                    snap.WipMaterial + snap.WipLabor + snap.WipOverhead);
            }

            // ── ⑥ §3.10 全套稽核（結構性守門＋金額恆等式）必須全綠 ─────────────
            using (var scope = NewScope(out var db))
            {
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditWipSnapshotConsistencyAsync(Period, Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
            }
        }
    }
}
