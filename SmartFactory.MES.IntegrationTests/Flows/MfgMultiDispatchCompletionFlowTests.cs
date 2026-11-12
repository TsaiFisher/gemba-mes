using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Flows
{
    /// <summary>
    /// 多工序＋拆張的工單：只有「最後一張」派工報滿時工單才自動完工（人工驗收 N-7 的補測）。
    ///
    /// N-7 實機只驗過「一張工單只有一張派工」——那個情境下第一張報滿就是最後一張，
    /// 「太早完工」與「永遠不完工」兩種缺陷都看不出來。這裡兩道工序、第二道拆成兩張，
    /// 每張報滿後都檢查工單狀態，全部走 <see cref="DispatchController"/> 真正的報工入口。
    ///
    /// 曾發生過的同類缺陷：完工判斷在存檔前以 AsNoTracking 投影讀派工狀態，讀到剛報滿那張的舊值，
    /// 最後一張報滿時工單永遠不轉完工（CreateReportControllerTests ⑨ 只在 InMemory、單一派工下守住）。
    /// </summary>
    [Collection("Integration")]
    public class MfgMultiDispatchCompletionFlowTests : FlowTestBase
    {
        // 廠別與單號前綴避開其他 MFG 流程測試（共用庫，各自以工單號清場）
        protected override string Plant => "ZZMFGQ";

        private const string Mat   = "FLOW-MFGQ-MAT";
        private const string Fg    = "FLOW-MFGQ-FG";
        private const string Batch = "FLOW-MFGQ-PB";
        private const string Wo    = "FLOW-MFGQ-WO";
        private const string D10   = "FLOW-MFGQ-DP10";
        private const string D20   = "FLOW-MFGQ-DP20";
        private const string D20S1 = "FLOW-MFGQ-DP20-s1";   // 拆分沿用「原編號-sN」

        private static readonly DateOnly StockDate = new(2098, 3, 4);

        public MfgMultiDispatchCompletionFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "多派工完工原料"), (Fg, "多派工完工成品"));
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

            await db.ProductionReports.IgnoreQueryFilters().Where(r => r.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionSessions.IgnoreQueryFilters().Where(s => s.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderActualCosts.IgnoreQueryFilters().Where(c => c.PlantId == Plant).ExecuteDeleteAsync();
            await db.WorkOrderMaterials.IgnoreQueryFilters().Where(m => m.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrderOperations.IgnoreQueryFilters().Where(o => o.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == Wo).ExecuteDeleteAsync();
            await db.ProductionBatches.IgnoreQueryFilters().Where(b => b.BatchId == Batch).ExecuteDeleteAsync();
        }

        // ── 前置：兩道工序的工單投產，第二道拆成兩張派工 ──────────────────────

        private async Task SeedAsync(decimal woQty)
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGQ-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = woQty * 4m, TypeCode = "PI", TypeCategory = "Purchase",
                    MovementDate = StockDate, UomCode = Uom, StockUomCode = Uom, UnitPrice = 10m,
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
                    BatchId = Batch, MrpRunId = "FLOW-MFGQ-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                var wo = new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGQ-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Planned, PlantId = Plant,
                    PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                };
                var op10 = new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10",
                    WorkCenterId = "FLOW-WCQ", UnitTime = 1m,
                };
                op10.Materials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = woQty * 2m, UomCode = Uom, UnitCost = 10m,
                    IssueMethod = MaterialIssueMethod.Push,
                });
                wo.Operations.Add(op10);
                wo.Operations.Add(new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 20, OperationName = "OP20",
                    WorkCenterId = "FLOW-WCQ", UnitTime = 1m,
                });
                db.WorkOrders.Add(wo);
                await db.SaveChangesAsync();
            }

            using (var scope = NewScope(out _))
                Assert.Equal(WorkOrderReleaseOutcome.Released,
                    (await Svc<IWorkOrderReleaseService>(scope).ApplyAsync(Wo, confirmed: true)).Outcome);

            using (var scope = NewScope(out var db))
            {
                db.Dispatches.Add(NewDispatch(D10, 10, woQty));
                db.Dispatches.Add(NewDispatch(D20, 20, woQty * 0.6m));     // 拆張：60
                db.Dispatches.Add(NewDispatch(D20S1, 20, woQty * 0.4m));   //       40
                await db.SaveChangesAsync();
            }
        }

        private Dispatch NewDispatch(string id, int seq, decimal qty) => new()
        {
            DispatchId = id, WorkOrderId = Wo, OperationSeq = seq,
            Quantity = qty, QtyDone = 0m,
            Status = DispatchStatus.Scheduled, PlantId = Plant, CreatedByRoleId = RoleId,
        };

        // ── 真正的入口：開工、報工 ──────────────────────────────────────────

        private DispatchController NewDispatchController(IServiceScope scope, SmartFactory.MES.Data.ApplicationDbContext db)
        {
            var sp = new TestControllerServices(scope.ServiceProvider, db, Plant, RoleId);
            return new DispatchController(
                sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>(),
                sp.GetRequiredService<IInventoryReservationService>(),
                sp.GetRequiredService<IPlantParamService>(),
                sp.GetRequiredService<IWorkOrderMaterialReadinessService>(),
                sp.GetRequiredService<IProductionCostAccrualService>(),
                sp.GetRequiredService<IStagingSourceService>(),
                sp.GetRequiredService<IUomService>(),
                sp.GetRequiredService<IFpiGateService>(),
                sp.GetRequiredService<IMfgSimplifiedModeService>(),
                sp.GetRequiredService<IOperationSequenceGuard>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = sp },
                },
            };
        }

        private static (bool Ok, string? Message) Read(IActionResult r)
        {
            var json = Assert.IsType<JsonResult>(r);
            var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
            var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value) as string;
            return (Equals(ok, true), msg);
        }

        /// <summary>開工後一次報滿該派工。</summary>
        private async Task StartAndReportFullAsync(string dispatchId, decimal qty)
        {
            using (var scope = NewScope(out var db))
            {
                var (ok, msg) = Read(await NewDispatchController(scope, db)
                    .StartSession(dispatchId, machineCode: null, toolingCode: null));
                Assert.True(ok, $"{dispatchId} 開工應成功：{msg}");
            }

            string sessionId;
            using (var scope = NewScope(out var db))
                sessionId = await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.DispatchId == dispatchId && s.Status == ProductionSessionStatus.Open)
                    .Select(s => s.SessionId).SingleAsync();

            using (var scope = NewScope(out var db))
            {
                var (ok, msg) = Read(await NewDispatchController(scope, db)
                    .CreateReport(dispatchId, sessionId, qty, 0m, containerCode: null));
                Assert.True(ok, $"{dispatchId} 報工 {qty} 應成功：{msg}");
            }
        }

        private async Task<(WorkOrderStatus Wo, DispatchStatus Dispatch)> StatusAsync(string dispatchId)
        {
            using var scope = NewScope(out var db);
            var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkOrderId == Wo);
            var d  = await db.Dispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.DispatchId == dispatchId);
            return (wo.Status, d.Status);
        }

        [Fact]
        public async Task 多工序拆張_只有最後一張派工報滿時工單才自動完工()
        {
            const decimal woQty = 100m;
            await SeedAsync(woQty);

            // 第一道工序報滿：派工結案，但第二道還沒做 → 工單仍在生產中（不可太早完工）
            await StartAndReportFullAsync(D10, woQty);
            var s = await StatusAsync(D10);
            Assert.Equal(DispatchStatus.Closed, s.Dispatch);
            Assert.Equal(WorkOrderStatus.InProgress, s.Wo);

            // 第二道的第一張（60）報滿：還有拆出來的 40 沒做 → 仍在生產中
            await StartAndReportFullAsync(D20, woQty * 0.6m);
            s = await StatusAsync(D20);
            Assert.Equal(DispatchStatus.Closed, s.Dispatch);
            Assert.Equal(WorkOrderStatus.InProgress, s.Wo);

            // 最後一張（40）報滿：全部派工結案 → 工單在同一次報工自動完工（不可永遠不完工）
            await StartAndReportFullAsync(D20S1, woQty * 0.4m);
            s = await StatusAsync(D20S1);
            Assert.Equal(DispatchStatus.Closed, s.Dispatch);
            Assert.Equal(WorkOrderStatus.Completed, s.Wo);
        }
    }
}
