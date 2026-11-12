using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.MFG.Services;
using Models = SmartFactory.MES.Models;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// Mobile FPI（首件檢查）手機流程整合測試（mobile-pwa.md §14 / mfg-domain.md §15.3 Phase H）。
    ///
    /// 驗證前端 fpi.html → ipqc-process.html?orderType=FPI 送出所命中的 dispatch-aware 端點
    /// POST /api/mobile/mfg/inspection-orders（SubmitInspection）的派工狀態轉換：
    ///   FPI Pass（FailQty=0） → 派工 PendingFPI 放行為 ReadyToStart、檢驗單 Pass
    ///   FPI Fail（FailQty≥1） → 派工轉 OnHold、檢驗單 Fail
    ///   同 clientId 重送     → 幂等回快照，不重複轉狀態
    ///
    /// 真 PG（PlantId query filter + SyncLog 幂等），故置於整合測試。
    /// </summary>
    [Collection("Integration")]
    public class MobileFpiInspectionTests
    {
        private const string DispatchId = "MBLFPI-D";
        private const string WoId       = "MBLFPI-WO";
        private const string BatchId    = "MBLFPI-B";
        private const string OrderId    = "MBLFPI-IO";
        private const string Product    = "MBLFPI-P";
        private const string Plant      = "P1";
        private const string RoleId     = "1";

        private readonly TestWebApplicationFactory _factory;

        public MobileFpiInspectionTests(TestWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task SubmitFpi_Pass_ReleasesDispatchToReadyToStart()
        {
            Seed(MfgM.DispatchStatus.PendingFPI);

            using var scope = _factory.Services.CreateScope();
            var db = ScopedDb(scope);
            var controller = BuildController(scope, db);

            var req = new InspectionSubmitRequest(
                DispatchId, Models.InspectionOrderType.FPI,
                FailQty: 0, ActualSampleQty: 3,
                ClientId: System.Guid.NewGuid().ToString("N"), ClientTimestamp: System.DateTime.UtcNow,
                Items: new[]
                {
                    new InspectionItemInput(1, "尺寸A", "mm", 9.8m, 10m, 10.2m, 10.05m, true, null)
                });

            var result = await controller.SubmitInspection(req);

            Assert.IsType<OkObjectResult>(result);
            await AssertStatusesAsync(expectedDispatch: MfgM.DispatchStatus.ReadyToStart, expectedOrder: "Pass");
        }

        [Fact]
        public async Task SubmitFpi_Fail_PutsDispatchOnHold()
        {
            Seed(MfgM.DispatchStatus.PendingFPI);

            using var scope = _factory.Services.CreateScope();
            var db = ScopedDb(scope);
            var controller = BuildController(scope, db);

            var req = new InspectionSubmitRequest(
                DispatchId, Models.InspectionOrderType.FPI,
                FailQty: 1, ActualSampleQty: 3,
                ClientId: System.Guid.NewGuid().ToString("N"), ClientTimestamp: System.DateTime.UtcNow,
                Items: null);

            var result = await controller.SubmitInspection(req);

            Assert.IsType<OkObjectResult>(result);
            await AssertStatusesAsync(expectedDispatch: MfgM.DispatchStatus.OnHold, expectedOrder: "Fail");
        }

        [Fact]
        public async Task SubmitFpi_DuplicateClientId_IsIdempotent()
        {
            Seed(MfgM.DispatchStatus.PendingFPI);

            var clientId = System.Guid.NewGuid().ToString("N");
            var ts = System.DateTime.UtcNow;

            // 第一次：Pass 放行
            using (var scope1 = _factory.Services.CreateScope())
            {
                var db1 = ScopedDb(scope1);
                var c1 = BuildController(scope1, db1);
                var r1 = await c1.SubmitInspection(new InspectionSubmitRequest(
                    DispatchId, Models.InspectionOrderType.FPI, 0, 3, clientId, ts, null));
                Assert.IsType<OkObjectResult>(r1);
            }
            await AssertStatusesAsync(MfgM.DispatchStatus.ReadyToStart, "Pass");

            // 手動把派工改回 PendingFPI，模擬「若非幂等會被重複轉狀態」
            using (var scopeReset = _factory.Services.CreateScope())
            {
                var dbR = ScopedDb(scopeReset);
                var d = await dbR.Dispatches.FirstAsync(x => x.DispatchId == DispatchId);
                d.Status = MfgM.DispatchStatus.PendingFPI;
                await dbR.SaveChangesAsync();
            }

            // 第二次：同 clientId → 幂等回快照，不應再轉狀態（派工續留 PendingFPI）
            using (var scope2 = _factory.Services.CreateScope())
            {
                var db2 = ScopedDb(scope2);
                var c2 = BuildController(scope2, db2);
                var r2 = await c2.SubmitInspection(new InspectionSubmitRequest(
                    DispatchId, Models.InspectionOrderType.FPI, 0, 3, clientId, ts, null));
                Assert.IsType<OkObjectResult>(r2);
            }
            await AssertStatusesAsync(MfgM.DispatchStatus.PendingFPI, "Pass");
        }

        // ── 伺服器端判定（2026-09-29 安全盤點）──────────────────────────────────
        // 原本 IsConforming、上下限、TargetStdDev 全照手機送的存，合格只看 FailQty：
        // 超規格的量測值標成「符合」、不良數填 0，首件就放行、派工解鎖。

        [Fact]
        public async Task SubmitFpi_OutOfSpecMeasurement_FailsEvenIfClientSaysConforming()
        {
            Seed(MfgM.DispatchStatus.PendingFPI, withSpecItem: true);

            using (var scope = _factory.Services.CreateScope())
            {
                var controller = BuildController(scope, ScopedDb(scope));
                var result = await controller.SubmitInspection(new InspectionSubmitRequest(
                    DispatchId, Models.InspectionOrderType.FPI,
                    FailQty: 0, ActualSampleQty: 3,
                    ClientId: System.Guid.NewGuid().ToString("N"), ClientTimestamp: System.DateTime.UtcNow,
                    Items: new[]
                    {
                        // 上下限放寬到 0–99、自稱符合；待檢單上的規格快照是 9.8–10.2
                        new InspectionItemInput(1, "尺寸A", "mm", 0m, 10m, 99m, 12m, IsConforming: true, TargetStdDev: null)
                    }));
                Assert.IsType<OkObjectResult>(result);
            }

            await AssertStatusesAsync(expectedDispatch: MfgM.DispatchStatus.OnHold, expectedOrder: "Fail");

            using var check = _factory.Services.CreateScope();
            var db    = ScopedDb(check);
            var item  = await db.InspectionItems.AsNoTracking().SingleAsync(i => i.InspectionOrderId == OrderId);
            var order = await db.InspectionOrders.AsNoTracking().FirstAsync(o => o.InspectionOrderId == OrderId);
            Assert.Equal((9.8m, 10.2m), (item.LowerSpec, item.UpperSpec));
            Assert.Equal(12m, item.MeasuredValue);
            Assert.False(item.IsConforming);
            Assert.Equal(1, order.FailQty);
        }

        [Theory]
        [InlineData(0, 0)]    // 沒抽樣：原本照樣判通過、派工解鎖
        [InlineData(3, -1)]
        [InlineData(3, 4)]
        public async Task SubmitFpi_InvalidQty_RejectedAndDispatchStaysPending(int actualSampleQty, int failQty)
        {
            Seed(MfgM.DispatchStatus.PendingFPI);

            using (var scope = _factory.Services.CreateScope())
            {
                var controller = BuildController(scope, ScopedDb(scope));
                var result = await controller.SubmitInspection(new InspectionSubmitRequest(
                    DispatchId, Models.InspectionOrderType.FPI, failQty, actualSampleQty,
                    System.Guid.NewGuid().ToString("N"), System.DateTime.UtcNow, null));
                var body = Assert.IsType<MobileErrorResult>(Assert.IsType<BadRequestObjectResult>(result).Value);
                Assert.Equal("INVALID_QTY", body.Error?.Code);
            }

            await AssertStatusesAsync(expectedDispatch: MfgM.DispatchStatus.PendingFPI, expectedOrder: "Pending");
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private async Task AssertStatusesAsync(MfgM.DispatchStatus expectedDispatch, string expectedOrder)
        {
            using var scope = _factory.Services.CreateScope();
            var db = ScopedDb(scope);
            var dispatch = await db.Dispatches.AsNoTracking().FirstAsync(d => d.DispatchId == DispatchId);
            var order    = await db.InspectionOrders.AsNoTracking().FirstAsync(o => o.InspectionOrderId == OrderId);
            Assert.Equal(expectedDispatch, dispatch.Status);
            Assert.Equal(expectedOrder, order.Status);
        }

        private static ApplicationDbContext ScopedDb(IServiceScope scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = Plant;
            db.CurrentRoleId  = RoleId;
            return db;
        }

        private MobileMfgController BuildController(IServiceScope scope, ApplicationDbContext db)
            => new MobileMfgController(
                db,
                new FakeUserContext(),
                scope.ServiceProvider.GetRequiredService<IReportEtaNotifier>(),
                scope.ServiceProvider.GetRequiredService<ISamplingPlanService>(),
                new AllowAllMobilePermission(),
                scope.ServiceProvider.GetRequiredService<IProductionCostAccrualService>(),
                scope.ServiceProvider.GetRequiredService<IStagingSourceService>(),
                scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IFpiGateService>(),
                scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IMfgSimplifiedModeService>(),
                scope.ServiceProvider.GetRequiredService<SmartFactory.MES.Modules.MFG.Services.IOperationSequenceGuard>());

        private void Seed(MfgM.DispatchStatus dispatchStatus, bool withSpecItem = false)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = System.DateTime.UtcNow;

            if (!db.ProductionBatches.IgnoreQueryFilters().Any(b => b.BatchId == BatchId))
                db.ProductionBatches.Add(new MfgM.ProductionBatch
                {
                    BatchId = BatchId, MrpRunId = "MBLFPI-MRP", MrpRunLineSeq = 1,
                    ProductCode = Product, Quantity = 10, Priority = 50,
                    PlannedEndDate = now.AddDays(7), PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrders.IgnoreQueryFilters().Any(w => w.WorkOrderId == WoId))
                db.WorkOrders.Add(new MfgM.WorkOrder
                {
                    WorkOrderId = WoId, ProductionBatchId = BatchId, ProductCode = Product,
                    RoutingId = "MBLFPI-R", Quantity = 10, Status = MfgM.WorkOrderStatus.Released,
                    PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                    PlantId = Plant, CreatedByRoleId = RoleId,
                });

            if (!db.WorkOrderOperations.Any(o => o.WorkOrderId == WoId && o.OperationSeq == 10))
                db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
                {
                    WorkOrderId = WoId, OperationSeq = 10, WorkCenterId = "MBLFPI-WC",
                    UnitTime = 1, BatchQty = 1, OperationName = "MBLFPI OP",
                    RequiresInspection = false, RequiresFpi = true,
                });
            db.SaveChanges();

            // 重建派工（PendingFPI）與待檢 FPI 單，確保可重跑
            foreach (var e in db.InspectionItems.IgnoreQueryFilters().Where(i => i.InspectionOrderId == OrderId).ToList())
                db.InspectionItems.Remove(e);
            foreach (var e in db.InspectionOrders.IgnoreQueryFilters().Where(o => o.InspectionOrderId == OrderId).ToList())
                db.InspectionOrders.Remove(e);
            foreach (var e in db.SyncLogs.Where(s => s.Endpoint == "/api/mobile/inspection-orders").ToList())
                db.SyncLogs.Remove(e);
            foreach (var e in db.Dispatches.IgnoreQueryFilters().Where(d => d.DispatchId == DispatchId).ToList())
                db.Dispatches.Remove(e);
            db.SaveChanges();

            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = DispatchId, WorkOrderId = WoId, OperationSeq = 10,
                Quantity = 100, QtyDone = 0, Status = dispatchStatus,
                MachineCode = "MBLFPI-M", ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = Plant, CreatedByRoleId = RoleId,
            });
            db.InspectionOrders.Add(new Models.InspectionOrder
            {
                InspectionOrderId = OrderId, InspectionType = "FPI",
                OrderType = Models.InspectionOrderType.FPI, PlantId = Plant,
                SourceDocId = DispatchId, ProductCode = Product,
                LotSize = 100, SampleSize = 3, VerificationLevel = 4, Status = "Pending",
                CreatedAt = now, UpdatedAt = now,
            });
            if (withSpecItem)
                db.InspectionItems.Add(new Models.InspectionItem
                {
                    InspectionOrderId = OrderId, SeqNo = 1, ItemName = "尺寸A", UomCode = "mm",
                    LowerSpec = 9.8m, TargetValue = 10m, UpperSpec = 10.2m,
                });
            db.SaveChanges();
        }

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

        private sealed class AllowAllMobilePermission : IMobilePermissionService
        {
            public Task<IEnumerable<Models.MobileModule>> GetAllowedModulesAsync(string roleId)
                => Task.FromResult(Enumerable.Empty<Models.MobileModule>());
            public Task<bool> CanExecuteAsync(string roleId, string moduleKey, string actionKey)
                => Task.FromResult(true);
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult(Enumerable.Empty<string>());
        }
    }
}
