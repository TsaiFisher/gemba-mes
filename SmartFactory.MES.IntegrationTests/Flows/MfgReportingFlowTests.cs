using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
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
    /// 人工驗收作業書 §5.1 Wave 2 的 2-4：開工、報工與領料的銜接。
    ///
    /// 報工本身的守門（超量、負數、自動結案）先前只有單元測試以常數驗算式，
    /// 沒有任何測試走過真正的入口，也沒有人問過報工與**領料**之間有沒有關係。
    /// 本測試一律走 <see cref="DispatchController"/>。
    /// </summary>
    [Collection("Integration")]
    public class MfgReportingFlowTests : FlowTestBase
    {
        // 廠別與單號前綴刻意避開 MfgMaterialReturnFlowTests 的 ZZMFGR／FLOW-MFGR-*：
        // 兩個類別共用同一組會互相清場（各自的 Reset 都以工單號為鍵刪派工），
        // 症狀是外鍵衝突而不是斷言失敗，很難聯想到是撞名。
        protected override string Plant => "ZZMFGP";

        private const string Mat   = "FLOW-MFGP-MAT";
        private const string Fg    = "FLOW-MFGP-FG";
        private const string Batch = "FLOW-MFGP-PB";
        private const string Wo    = "FLOW-MFGP-WO";
        private const string Disp  = "FLOW-MFGP-DP";

        private static readonly DateOnly StockDate = new(2098, 2, 3);

        public MfgReportingFlowTests(TestWebApplicationFactory factory) : base(factory)
            => ResetAllAsync().GetAwaiter().GetResult();

        private async Task ResetAllAsync()
        {
            await ResetAsync((Mat, "報工流程原料"), (Fg, "報工流程成品"));
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

        // ── 前置 ────────────────────────────────────────────────────────────

        private async Task SeedReleasedWorkOrderAsync(decimal woQty, decimal requiredQty, decimal matCost)
        {
            using (var scope = NewScope(out var db))
            {
                await Svc<IInventoryPostingService>(scope).PostLineDetailAsync(new PostLineDetailRequest
                {
                    PlantId = Plant, DocNo = "FLOW-MFGP-OPEN", LineNo = 1,
                    ProductCode = Mat, LocationCode = Loc, LotNo = "",
                    Qty = requiredQty * 2m, TypeCode = "PI", TypeCategory = "Purchase",
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
                    BatchId = Batch, MrpRunId = "FLOW-MFGP-MRP", MrpRunLineSeq = 1,
                    ProductCode = Fg, Quantity = woQty, PlantId = Plant, CreatedByRoleId = RoleId,
                });
                var wo = new WorkOrder
                {
                    WorkOrderId = Wo, ProductionBatchId = Batch, ProductCode = Fg,
                    RoutingId = "FLOW-MFGP-RT", Quantity = woQty,
                    Status = WorkOrderStatus.Planned, PlantId = Plant,
                    PlannedStartDate = StockDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    PlannedEndDate   = StockDate.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                };
                var op = new WorkOrderOperation
                {
                    WorkOrderId = Wo, OperationSeq = 10, OperationName = "OP10",
                    WorkCenterId = "FLOW-WCR", UnitTime = 1m,
                };
                op.Materials.Add(new WorkOrderMaterial
                {
                    WorkOrderId = Wo, OperationSeq = 10, BomSeq = 1, ProductCode = Mat,
                    RequiredQty = requiredQty, UomCode = Uom, UnitCost = matCost,
                    IssueMethod = MaterialIssueMethod.Push,
                });
                wo.Operations.Add(op);
                db.WorkOrders.Add(wo);
                await db.SaveChangesAsync();
            }

            // 投產：料況硬閘（§24）在此把關，並自動產生 Push 備料單
            using (var scope = NewScope(out _))
                Assert.Equal(WorkOrderReleaseOutcome.Released,
                    (await Svc<IWorkOrderReleaseService>(scope).ApplyAsync(Wo, confirmed: true)).Outcome);
        }

        private async Task SeedDispatchAsync(decimal dispatchQty)
        {
            using var scope = NewScope(out var db);
            db.Dispatches.Add(new Dispatch
            {
                DispatchId = Disp, WorkOrderId = Wo, OperationSeq = 10,
                Quantity = dispatchQty, QtyDone = 0m,
                Status = DispatchStatus.Scheduled, PlantId = Plant, CreatedByRoleId = RoleId,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 開工：走真正的入口。它同時做三件事——建 ProductionSession、派工轉 InProgress、
        /// 工單 Released→InProgress，而最後一件是後面「報滿自動完工」的前提：
        /// <c>CheckWorkOrderCompletionAsync</c> 只在工單為 InProgress 時才轉 Completed。
        /// SessionId 由伺服器產生，故開工後再讀回來。
        /// </summary>
        private async Task<string> StartSessionAsync()
        {
            using (var scope = NewScope(out var db))
            {
                var ctrl = NewDispatchController(scope, db);
                var json = Assert.IsType<JsonResult>(
                    await ctrl.StartSession(Disp, machineCode: null, toolingCode: null));
                var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
                var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value);
                Assert.True(Equals(ok, true), $"開工應成功：{msg}");
            }

            using (var scope = NewScope(out var db))
                return await db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.DispatchId == Disp && s.Status == ProductionSessionStatus.Open)
                    .Select(s => s.SessionId).SingleAsync();
        }

        // ── 報工入口 ────────────────────────────────────────────────────────

        private sealed record ReportOutcome(bool Ok, string? Message)
        {
            public void AssertOk(string what) => Assert.True(Ok, $"{what} 應成功，實際失敗：{Message}");
            public void AssertRejected(string what) => Assert.False(Ok, $"{what} 應被守門擋下，實際卻放行了");
        }

        private DispatchController NewDispatchController(
            Microsoft.Extensions.DependencyInjection.IServiceScope scope,
            SmartFactory.MES.Data.ApplicationDbContext db)
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

        private async Task<ReportOutcome> ReportAsync(string sessionId, decimal qtyOk, decimal qtyNg = 0m)
        {
            using var scope = NewScope(out var db);
            var ctrl = NewDispatchController(scope, db);

            var json = Assert.IsType<JsonResult>(
                await ctrl.CreateReport(Disp, sessionId, qtyOk, qtyNg, containerCode: null));
            var ok  = json.Value!.GetType().GetProperty("success")?.GetValue(json.Value);
            var msg = json.Value.GetType().GetProperty("message")?.GetValue(json.Value) as string;
            return new ReportOutcome(Equals(ok, true), msg);
        }

        // ── 2-4 ①：報工守門與自動結案 ────────────────────────────────────────

        /// <summary>
        /// 報工的四道守門與兩段自動連動，全部走真正的入口。自動連動是重點：
        /// 報滿之後派工自己 Closed、工單自己 Completed，沒有人按任何按鈕——
        /// 也就是說「報工報滿」等於「這張工單可以入庫了」。
        /// </summary>
        [Fact]
        public async Task Reporting_GatesQuantities_AndAutoClosesDispatchAndWorkOrder()
        {
            const decimal woQty = 100m, requiredQty = 300m, matCost = 20m;

            await SeedReleasedWorkOrderAsync(woQty, requiredQty, matCost);
            await SeedDispatchAsync(woQty);
            var session = await StartSessionAsync();

            (await ReportAsync(session, qtyOk: -5m)).AssertRejected("負數報工");
            (await ReportAsync(session, qtyOk: 0m, qtyNg: 0m)).AssertRejected("零數量報工");
            (await ReportAsync(session, qtyOk: woQty + 1m)).AssertRejected("超出派工量的報工");

            (await ReportAsync(session, qtyOk: 40m)).AssertOk("首次報工 40");
            using (var scope = NewScope(out var db))
            {
                var d = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(x => x.DispatchId == Disp);
                Assert.Equal(40m, d.QtyDone);
                Assert.Equal(DispatchStatus.InProgress, d.Status);
            }

            // 累計後才超量：守門看的是「已報 + 本次」，不是單次數量
            (await ReportAsync(session, qtyOk: 61m)).AssertRejected("累計超出派工量的報工");

            (await ReportAsync(session, qtyOk: 60m)).AssertOk("補足剩餘 60");
            using (var scope = NewScope(out var db))
            {
                var d = await db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(x => x.DispatchId == Disp);
                Assert.Equal(woQty, d.QtyDone);
                Assert.Equal(DispatchStatus.Closed, d.Status);      // 報滿自動結案

                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(WorkOrderStatus.Completed, wo.Status); // 派工全結案 → 工單完工

                Assert.Equal(2, await db.ProductionReports.IgnoreQueryFilters()
                    .CountAsync(r => r.DispatchId == Disp));
            }

            (await ReportAsync(session, qtyOk: 1m)).AssertRejected("派工已結案後再報工");
        }

        // ── 2-4 ②：報工與領料的銜接 ─────────────────────────────────────────

        /// <summary>
        /// 一顆料都沒領，照樣可以把整張派工報滿、工單自動完工、完工入庫。
        ///
        /// 這不是「現場少用了一點料」——是**這張工單帳上沒有耗用任何材料**。
        /// 自動產生的 Push 備料單停在 Draft（它建出來就沒有倉庫／儲位，非人工補不可），
        /// 而報工完全不看領料，於是成品以「只有人工製費」的成本入庫，存貨低估。
        ///
        /// 為什麼所有既有防線都攔不到：
        ///   ‧ 投產料況硬閘（§24）看的是**可用量**（料還在倉庫裡，當然夠），不是已領量
        ///   ‧ 報工守門只比派工量
        ///   ‧ §3.8 後沖稽核只管 Backflush 料
        ///   ‧ WIP 恆等式兩腿都是 0，完全一致
        ///   ‧ 每張傳票都平衡——根本沒有材料那一張
        ///
        /// 判準刻意訂在「完全沒領」而不是「領得比 BOM 少」：超領不設上限（2-8 已定案，
        /// 不良重投與耗損補發屬正常製造場景），少領同理也可能是正常的，
        /// 唯獨一顆都沒領而工單完工，必定是漏了發料這個動作。
        /// </summary>
        [Fact]
        public async Task WorkOrderCompletedWithoutAnyMaterialIssued_IsReportedByAudit()
        {
            const decimal woQty = 100m, requiredQty = 300m, matCost = 20m;

            await SeedReleasedWorkOrderAsync(woQty, requiredQty, matCost);
            await SeedDispatchAsync(woQty);
            var session = await StartSessionAsync();

            // 備料單刻意不過帳——它自動建立時就沒有倉儲，本來就要人補
            (await ReportAsync(session, qtyOk: woQty)).AssertOk("報滿");

            using (var scope = NewScope(out _))
                await Svc<IProductionCompletionService>(scope).CompleteAsync(Wo, Plant, Wh, Loc);

            using (var scope = NewScope(out var db))
            {
                // 現況：原料在手量一個都沒少，成品卻已入庫
                Assert.Equal(requiredQty * 2m, await QtyOnHandAsync(db, Plant, Mat));
                Assert.Equal(woQty,            await QtyOnHandAsync(db, Plant, Fg));

                var wo = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(w => w.WorkOrderId == Wo);
                Assert.Equal(0m, wo.ActualMaterialCost);

                var mat = await db.WorkOrderMaterials.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(m => m.WorkOrderId == Wo);
                Assert.Equal(0m, mat.IssuedQty);

                // 既有防線一致地沉默
                var audit = Svc<IDataAuditService>(scope);
                Assert.Empty(await audit.AuditBackflushConsumptionAsync(Plant));
                Assert.Empty(await audit.AuditJournalBalanceAsync(Plant));
                Assert.Empty(await audit.AuditInventoryRollForwardAsync(Plant));

                // 必須有人叫出來
                var finding = Assert.Single(await audit.AuditWorkOrderMaterialIssueAsync(Plant));
                Assert.Equal("WO_MATERIAL_NOT_ISSUED", finding.AuditType);
                Assert.Equal(Wo, finding.DocId);
                Assert.Equal(Mat, finding.ProductCode);
            }
        }
    }
}
