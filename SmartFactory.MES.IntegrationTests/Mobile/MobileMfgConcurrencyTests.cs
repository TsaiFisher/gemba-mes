using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.MFG.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// Mobile MFG 寫入端並發例外處理驗證（xmin 鋪開後補的 catch→409 一致性）。
    ///
    /// 背景：Dispatch（顯式 uint xmin）/WorkOrder（shadow xmin）成為有效並發權杖後，
    /// Mobile 端動到這些追蹤實體的端點，並發競態會拋 DbUpdateConcurrencyException。
    /// Mobile 無全域 ExceptionFilter，原僅 CreateReport 有 inline catch；其餘端點裸奔 → 裸 500。
    /// 已抽 SaveSyncOrConflictAsync helper 套到 SetupStart/SetupDone/StartSession/StopSession/
    /// SubmitInspection/MachineStatusEvent，並重構 CreateReport。
    ///
    /// 本測試以「EF identity-map 持舊 xmin 快照」確定性製造衝突（避免 HTTP 平行競態的 flaky）：
    /// controller 的 context 先載入派工（持 xmin v1）→ 另一 context 改該列（DB 進 v2）→
    /// 呼叫 StartSession：其 FirstOrDefaultAsync 經 identity-map 回傳仍持 v1 的追蹤實體 →
    /// 改 Status 後 SaveChanges 以 WHERE xmin=v1 命中 0 列 → 拋並發例外 → helper 攔截回 409 Conflict。
    /// 真 PG 才有 xmin（InMemory 無此系統欄位），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class MobileMfgConcurrencyTests
    {
        private const string DispatchId = "MBLCONCUR-D";
        private const string WoId       = "MBLCONCUR-WO";
        private const string BatchId    = "MBLCONCUR-B";
        private const string Plant      = "P1";
        private const string RoleId     = "1";

        private readonly TestWebApplicationFactory _factory;

        public MobileMfgConcurrencyTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Seed();
        }

        /// <summary>
        /// 一次性的衝突（controller 的 context 持舊 xmin）：伺服器在同一個請求裡清掉追蹤狀態、以最新資料重做，
        /// 上工成功——不再把 409 丟回手機（2026-10-21 起，見 MobileMfgController.WithConcurrencyRetryAsync）。
        /// </summary>
        [Fact]
        public async Task StartSession_ConcurrentDispatchModified_ServerRetriesAndSucceeds()
        {
            using var scopeCtl = _factory.Services.CreateScope();
            var dbCtl = scopeCtl.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbCtl.CurrentPlantId = Plant;
            dbCtl.CurrentRoleId  = RoleId;
            await dbCtl.Dispatches.FirstAsync(d => d.DispatchId == DispatchId);   // 持 xmin v1
            await TouchDispatchAsync();                                            // DB 進 v2

            var logs = new CapturingLoggerFactory();
            var result = await MakeController(scopeCtl, dbCtl, logs).StartSession(NewRequest());

            Assert.IsType<OkObjectResult>(result);
            // 重試後成功要留紀錄：上線後衝突多常發生，只能從這裡看（見 ConcurrencyConflictLog）
            Assert.Contains(logs.Entries, e => e.EventId == ConcurrencyConflictLog.RetrySucceeded.Id);
            using var verify = _factory.Services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await db.ProductionSessions.IgnoreQueryFilters().CountAsync(s => s.DispatchId == DispatchId));
        }

        /// <summary>
        /// 持續衝突（每一次存檔前都有人搶先改了這張派工）：重試次數有上限，最後仍回 409 CONCURRENCY_CONFLICT 交給手機佇列，
        /// 而不是無限重試、也不是裸 500。
        /// </summary>
        [Fact]
        public async Task StartSession_PersistentConflict_GivesUpAfterBoundedRetries_Returns409()
        {
            using var scopeCtl = _factory.Services.CreateScope();
            var bumper = new BumpDispatchOnSave(DispatchId,
                scopeCtl.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>(
                    scopeCtl.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>())
                .AddInterceptors(bumper).Options;
            await using var dbCtl = new ApplicationDbContext(options) { CurrentPlantId = Plant, CurrentRoleId = RoleId };

            var logs = new CapturingLoggerFactory();
            var result = await MakeController(scopeCtl, dbCtl, logs).StartSession(NewRequest());

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Single(logs.Entries, e => e.EventId == ConcurrencyConflictLog.RetryExhausted.Id);
            Assert.DoesNotContain(logs.Entries, e => e.EventId == ConcurrencyConflictLog.RetrySucceeded.Id);
            Assert.Equal("CONCURRENCY_CONFLICT", Assert.IsType<MobileErrorResult>(conflict.Value).Error?.Code);
            Assert.Equal(MobileMfgController.ServerConflictRetries + 1, bumper.Saves);
        }

        private SessionStartRequest NewRequest() => new(
            DispatchId: DispatchId, MachineId: "MBLCONCUR-M", MoldId: null,
            OperatorId: "admin", ClientId: $"MBLCONCUR-{Guid.NewGuid():N}", ClientTimestamp: DateTime.UtcNow);

        private MobileMfgController MakeController(IServiceScope scopeCtl, ApplicationDbContext dbCtl, CapturingLoggerFactory? logs = null) => new(
            dbCtl,
            new FakeUserContext(),
            scopeCtl.ServiceProvider.GetRequiredService<IReportEtaNotifier>(),
            scopeCtl.ServiceProvider.GetRequiredService<ISamplingPlanService>(),
            new AllowAllMobilePermission(),
            scopeCtl.ServiceProvider.GetRequiredService<IProductionCostAccrualService>(),
            scopeCtl.ServiceProvider.GetRequiredService<IStagingSourceService>(),
            scopeCtl.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IFpiGateService>(),
            scopeCtl.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IMfgSimplifiedModeService>(),
            scopeCtl.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IOperationSequenceGuard>())
        {
            // 上工的機台狀態閘口從 HttpContext.RequestServices 取廠別參數服務（同 MobileMfgTestKit）
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    RequestServices = logs == null ? scopeCtl.ServiceProvider : new LoggerOverride(scopeCtl.ServiceProvider, logs)
                }
            }
        };

        /// <summary>只換掉 ILoggerFactory，其餘照舊交給原本的 scope。</summary>
        private sealed class LoggerOverride(IServiceProvider inner, Microsoft.Extensions.Logging.ILoggerFactory logs) : IServiceProvider
        {
            public object? GetService(Type t) => t == typeof(Microsoft.Extensions.Logging.ILoggerFactory) ? logs : inner.GetService(t);
        }

        private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
        {
            public readonly System.Collections.Concurrent.ConcurrentBag<(int EventId, string Message)> Entries = new();
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
            public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
            public void Dispose() { }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, formatter(state, exception)));
        }

        private async Task TouchDispatchAsync()
        {
            using var scopeX = _factory.Services.CreateScope();
            var dbX = scopeX.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var dx = await dbX.Dispatches.IgnoreQueryFilters().FirstAsync(d => d.DispatchId == DispatchId);
            dx.UpdatedAt = DateTime.UtcNow;
            await dbX.SaveChangesAsync();
        }

        /// <summary>每次存檔前用另一條連線改一下這張派工（xmin 前進），讓這一次存檔必定衝突。</summary>
        private sealed class BumpDispatchOnSave(string dispatchId, string connectionString) : SaveChangesInterceptor
        {
            public int Saves;
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Saves++;
                await using var conn = new Npgsql.NpgsqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await using var cmd = new Npgsql.NpgsqlCommand(
                    "UPDATE \"Dispatches\" SET \"UpdatedAt\" = now() WHERE \"DispatchId\" = @id", conn);
                cmd.Parameters.AddWithValue("id", dispatchId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                return result;
            }
        }

        private void Seed()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;

            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == BatchId))
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = BatchId, MrpRunId = "MBLCONCUR-MRP", MrpRunLineSeq = 1,
                    ProductCode = "MBLCONCUR-P", Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = BatchId, ProductCode = "MBLCONCUR-P",
                    RoutingId = "MBLCONCUR-R", Quantity = 10, Status = MfgM.WorkOrderStatus.Released,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == WoId && o.OperationSeq == 10))
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = WoId, OperationSeq = 10, WorkCenterId = "MBLCONCUR-WC",
                    UnitTime = 1, BatchQty = 1, OperationName = "MBLCONCUR OP",
                    RequiresInspection = false, RequiresFpi = false,
                });
            db.SaveChanges();

            // 每次重建派工，確保 Status=Scheduled 且無進行中 session（可重跑）
            var staleSessions = db.ProductionSessions.IgnoreQueryFilters()
                .Where(s => s.DispatchId == DispatchId).ToList();
            if (staleSessions.Count > 0) { db.ProductionSessions.RemoveRange(staleSessions); db.SaveChanges(); }

            var stale = db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId == DispatchId).ToList();
            if (stale.Count > 0) { db.Dispatches.RemoveRange(stale); db.SaveChanges(); }

            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = DispatchId, WorkOrderId = WoId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0, Status = MfgM.DispatchStatus.Scheduled,
                MachineCode = "MBLCONCUR-M", ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }

        /// <summary>固定回 P1 / Role 1 / admin 的測試用 IUserContext（不依賴 HTTP）。</summary>
        private sealed class FakeUserContext : IUserContext
        {
            public string? PlantId        => Plant;
            public string? TimeZoneId     => "Asia/Taipei";
            public string? Language       => "zh-TW";
            public string? UserId         => "admin";
            public string? ActiveRoleId   => RoleId;
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => new List<string>();
        }

        /// <summary>測試用：行動權限一律放行，聚焦驗證並發處理路徑。</summary>
        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<SmartFactory.MES.Models.MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<SmartFactory.MES.Models.MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey)
                => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult(Enumerable.Empty<string>());
        }
    }
}
