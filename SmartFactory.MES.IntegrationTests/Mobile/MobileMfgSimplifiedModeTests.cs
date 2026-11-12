using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 人工驗收 Wave S 的 S-7／S-1／S-10（`manual-test-plan.md` §10.1）——**手機側**簡化模式。
    ///
    /// <para><b>為什麼非補不可</b>：S-7 的通過標準逐字寫「Web 與手機<b>兩邊都</b>看得到 Pending
    /// 派工且都放行開工」，而桌機側早有 <c>MfgSimplifiedStartAndSequenceTests</c>（14 支，走
    /// <c>DispatchController</c>），手機側**一支都沒有**。兩端共用
    /// <see cref="IMfgSimplifiedModeService"/> 不構成證據——§26 的 FPI 閘口當初也「應該共用」，
    /// 漂移發生在<b>呼叫端有沒有真的把它接上</b>，不在服務本身。這裡釘的正是那個接點。</para>
    ///
    /// <para><b>為什麼放整合測試而非單元測試</b>：S-1／S-10 要驗的是
    /// <see cref="IPlantParamService"/> 真實的行程級快取——「A 廠簡化、B 廠完整並存」與
    /// 「清掉覆寫立刻回到完整模式」都只在真的有快取時才有意義，Mock 掉就等於沒測。</para>
    ///
    /// <para>⚠️ 用專屬廠別代號（<c>ZZWSA</c>／<c>ZZWSB</c>），不碰 P1／P2——那兩廠的覆寫是
    /// §10.1 刻意留下的固定裝置（S-1 的通過標準就是它們），測試改到就等於毀掉驗收環境。
    /// <c>PlantParam</c> 對 <c>Plants</c> 沒有外鍵，故不需要建廠別主檔。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileMfgSimplifiedModeTests : IDisposable
    {
        private const string PlantA = "ZZWSA";   // 測試中會開啟 mfg_simplified_mode
        private const string PlantB = "ZZWSB";   // 全程維持完整模式（對照組）
        private const string RoleId = "1";

        private const string WoA = "WAVES-WO-A";
        private const string WoB = "WAVES-WO-B";
        private const string BatchA = "WAVES-B-A";
        private const string BatchB = "WAVES-B-B";
        private const string PendingA   = "WAVES-D-A-PEND";
        private const string ScheduledA = "WAVES-D-A-SCHD";
        private const string PendingB   = "WAVES-D-B-PEND";
        private const string ScheduledB = "WAVES-D-B-SCHD";

        private readonly TestWebApplicationFactory _factory;

        public MobileMfgSimplifiedModeTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
            ClearOverrides();   // 每支測試都從「兩廠皆完整模式」起跑
        }

        /// <summary>測試結束一律清掉覆寫——共用測試庫，留著會讓別支測試量到另一種模式。</summary>
        public void Dispose() => ClearOverrides();

        // ── S-7：派工清單 ────────────────────────────────────────────────────

        [Fact]
        public async Task DispatchList_FullMode_HidesPendingButStillShowsScheduled()
        {
            var ids = await ListDispatchIdsAsync(PlantB);

            // 兩腿一起斷言：只驗「看不到 Pending」的話，清單整個壞掉（回空）也會過
            Assert.DoesNotContain(PendingB, ids);
            Assert.Contains(ScheduledB, ids);
        }

        [Fact]
        public async Task DispatchList_SimplifiedMode_ShowsPendingDispatch()
        {
            await SetSimplifiedAsync(PlantA, true);

            var ids = await ListDispatchIdsAsync(PlantA);

            Assert.Contains(PendingA, ids);
            Assert.Contains(ScheduledA, ids);   // 放行 Pending 不得排擠既有狀態
        }

        // ── S-7：開工放行 ────────────────────────────────────────────────────

        [Fact]
        public async Task StartSession_FullMode_PendingDispatch_RejectedWithInvalidStatus()
        {
            var result = await StartSessionAsync(PlantB, PendingB);

            var bad  = Assert.IsType<BadRequestObjectResult>(result);
            var body = Assert.IsType<MobileErrorResult>(bad.Value);
            Assert.Equal("INVALID_STATUS", body.Error?.Code);
        }

        [Fact]
        public async Task StartSession_SimplifiedMode_PendingDispatch_Accepted()
        {
            await SetSimplifiedAsync(PlantA, true);

            var result = await StartSessionAsync(PlantA, PendingA);

            Assert.IsType<OkObjectResult>(result);

            // 放行不只是不回錯——派工要真的進 InProgress，否則現場按了等於沒按
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var d = await db.Dispatches.IgnoreQueryFilters().FirstAsync(x => x.DispatchId == PendingA);
            Assert.Equal(MfgM.DispatchStatus.InProgress, d.Status);
        }

        // ── 投產前審核（§18.2）：送審中／被駁回的工單不得開工 ────────────────

        /// <summary>
        /// §18.2 流程圖：Released 時若需簽核 → PendingApproval（**投產前審核**），核准後才是可生產的
        /// Released + Approved。投產服務先寫 Status=Released 再送審，簽核駁回只改 ApprovalStatus、
        /// 不把 Status 退回 Planned；排程引擎與開工只看 Status。完整模式下以已排程派工驗開工閘。
        /// </summary>
        [Theory]
        [InlineData(DocumentApprovalStatus.PendingApproval)]
        [InlineData(DocumentApprovalStatus.Rejected)]
        public async Task StartSession_FullMode_WorkOrderNotApproved_Rejected(DocumentApprovalStatus approval)
        {
            await SetWoApprovalAsync(WoB, approval);
            try
            {
                var result = await StartSessionAsync(PlantB, ScheduledB);
                Assert.IsNotType<OkObjectResult>(result);
            }
            finally
            {
                await SetWoApprovalAsync(WoB, DocumentApprovalStatus.Draft);
            }
        }

        /// <summary>撤回／駁回退回已規劃的工單，其已排程派工不得開工（2026-10-20 實證缺口）。</summary>
        [Fact]
        public async Task StartSession_WorkOrderBackToPlanned_RejectedWithWoNotReleased()
        {
            await SetWoStatusAsync(WoB, MfgM.WorkOrderStatus.Planned);
            try
            {
                var bad  = Assert.IsType<BadRequestObjectResult>(await StartSessionAsync(PlantB, ScheduledB));
                var body = Assert.IsType<MobileErrorResult>(bad.Value);
                Assert.Equal("WO_NOT_RELEASED", body.Error?.Code);
            }
            finally
            {
                await SetWoStatusAsync(WoB, MfgM.WorkOrderStatus.Released);
            }
        }

        private async Task SetWoStatusAsync(string woId, MfgM.WorkOrderStatus status)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == woId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, status));
        }

        private async Task SetWoApprovalAsync(string woId, DocumentApprovalStatus approval)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.WorkOrders.IgnoreQueryFilters().Where(w => w.WorkOrderId == woId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.ApprovalStatus, approval));
        }

        // ── S-1：同一時刻兩廠並存 ────────────────────────────────────────────

        [Fact]
        public async Task TwoPlants_SimplifiedAndFull_CoexistInTheSameMoment()
        {
            await SetSimplifiedAsync(PlantA, true);   // B 廠不設，維持繼承全域（＝完整）

            // 刻意同一輪、不重啟不清快取——這正是 S-1 要證明的「同一時刻並存」
            var a = await ListDispatchIdsAsync(PlantA);
            var b = await ListDispatchIdsAsync(PlantB);

            Assert.Contains(PendingA, a);
            Assert.DoesNotContain(PendingB, b);
        }

        // ── S-10：關掉覆寫回到完整模式 ───────────────────────────────────────

        [Fact]
        public async Task ClearingTheOverride_ImmediatelyReturnsToFullMode()
        {
            await SetSimplifiedAsync(PlantA, true);
            Assert.Contains(PendingA, await ListDispatchIdsAsync(PlantA));

            await ClearSimplifiedAsync(PlantA);

            // 髒快取的症狀就是這一行過不了：值清掉了、行為還停在簡化模式
            Assert.DoesNotContain(PendingA, await ListDispatchIdsAsync(PlantA));
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private async Task SetSimplifiedAsync(string plant, bool on)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlantParamService>()
                .SetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, plant, on ? "true" : "false");
        }

        private async Task ClearSimplifiedAsync(string plant)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlantParamService>()
                .ClearAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, plant);
        }

        private void ClearOverrides()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlantParamService>();
            svc.ClearAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, PlantA).GetAwaiter().GetResult();
            svc.ClearAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, PlantB).GetAwaiter().GetResult();
        }

        /// <summary>走真正的 <c>GET /api/mobile/mfg/dispatches</c>（不帶 status，才會用到模式判定）。</summary>
        private async Task<List<string>> ListDispatchIdsAsync(string plant)
        {
            using var scope = _factory.Services.CreateScope();
            var controller = MakeController(scope, plant);

            var result = await controller.GetDispatches(machineId: null, status: null);
            var ok   = Assert.IsType<OkObjectResult>(result);
            var body = Assert.IsType<MobileOkResult>(ok.Value);

            return (body.Data as System.Collections.IEnumerable)!
                .Cast<object>()
                .Select(o => (string)o.GetType().GetProperty("DispatchId")!.GetValue(o)!)
                .ToList();
        }

        private async Task<IActionResult> StartSessionAsync(string plant, string dispatchId)
        {
            using var scope = _factory.Services.CreateScope();
            var controller = MakeController(scope, plant);

            return await controller.StartSession(new SessionStartRequest(
                DispatchId: dispatchId, MachineId: $"WAVES-M-{plant}", MoldId: null,
                OperatorId: "admin", ClientId: $"WAVES-{Guid.NewGuid():N}",
                ClientTimestamp: DateTime.UtcNow));
        }

        private MobileMfgController MakeController(IServiceScope scope, string plant)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = plant;
            db.CurrentRoleId  = RoleId;

            return new MobileMfgController(
                db,
                new FakeUserContext(plant),
                scope.ServiceProvider.GetRequiredService<IReportEtaNotifier>(),
                scope.ServiceProvider.GetRequiredService<ISamplingPlanService>(),
                new AllowAllMobilePermission(),
                scope.ServiceProvider.GetRequiredService<IProductionCostAccrualService>(),
                scope.ServiceProvider.GetRequiredService<IStagingSourceService>(),
                scope.ServiceProvider.GetRequiredService<IFpiGateService>(),
                scope.ServiceProvider.GetRequiredService<IMfgSimplifiedModeService>(),
                scope.ServiceProvider.GetRequiredService<IOperationSequenceGuard>())
            {
                // 上工的機台狀態閘口從 HttpContext.RequestServices 取廠別參數服務（同 MobileMfgTestKit）
                ControllerContext = new ControllerContext
                {
                    HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider }
                }
            };
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            SeedPlant(db, PlantA, BatchA, WoA, PendingA, ScheduledA, now);
            SeedPlant(db, PlantB, BatchB, WoB, PendingB, ScheduledB, now);
        }

        private static void SeedPlant(ApplicationDbContext db, string plant, string batchId, string woId,
                                      string pendingId, string scheduledId, DateTime now)
        {
            // WorkOrder.ProductionBatchId 不可為 null，故批次也得建
            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == batchId))
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = batchId, MrpRunId = "WAVES-MRP", MrpRunLineSeq = 1,
                    ProductCode = "WAVES-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == woId))
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = woId, ProductionBatchId = batchId, ProductCode = "WAVES-P",
                    RoutingId = $"WAVES-R-{plant}", Quantity = 10, Status = MfgM.WorkOrderStatus.Released,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = plant, CreatedByRoleId = RoleId,
                });

            // 第一道工序：順序守門天生不擋，故失敗只可能來自狀態判定（S-8 另有測試涵蓋）
            if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == woId && o.OperationSeq == 10))
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = woId, OperationSeq = 10, WorkCenterId = "WAVES-WC",
                    UnitTime = 1, BatchQty = 1, OperationName = "WAVES OP",
                    RequiresInspection = false, RequiresFpi = false,
                });
            db.SaveChanges();

            // 派工每次重建：StartSession 會把狀態推進 InProgress，不重建則第二次跑就不是 Pending
            var sessions = db.ProductionSessions.IgnoreQueryFilters()
                .Where(s => s.DispatchId == pendingId || s.DispatchId == scheduledId).ToList();
            if (sessions.Count > 0) { db.ProductionSessions.RemoveRange(sessions); db.SaveChanges(); }

            var stale = db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId == pendingId || d.DispatchId == scheduledId).ToList();
            if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }

            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = pendingId, WorkOrderId = woId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0, Status = MfgM.DispatchStatus.Pending,
                MachineCode = $"WAVES-M-{plant}", ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = plant, CreatedByRoleId = RoleId,
            });
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = scheduledId, WorkOrderId = woId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0, Status = MfgM.DispatchStatus.Scheduled,
                MachineCode = $"WAVES-M-{plant}", ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }

        private sealed class FakeUserContext(string plant) : IUserContext
        {
            public string? PlantId        => plant;
            public string? TimeZoneId     => "Asia/Taipei";
            public string? Language       => "zh-TW";
            public string? UserId         => "admin";
            public string? ActiveRoleId   => RoleId;
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => new List<string>();
        }

        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey)
                => Task.FromResult(true);
            // 不可回空集合——RequireMobileModuleAsync 是用「動作鍵清單非空」當模組閘門，
            // 回空會變成 403，而症狀是清單為空，看起來像模式判定沒生效
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult<IEnumerable<string>>(new[] { "view" });
        }
    }
}
