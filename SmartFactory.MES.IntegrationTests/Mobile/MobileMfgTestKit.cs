using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機 MFG 寫入端測試共用：以真服務組出 <see cref="MobileMfgController"/>，並建一組獨立的
    /// 批次／工單／工序／派工。代號帶呼叫端給的尾碼——測試庫跨 worktree 共用，固定代號會被別人中途重建。
    /// </summary>
    internal static class MobileMfgTestKit
    {
        public const string RoleId = "1";

        public sealed record Ids(string BatchId, string WoId, string DispatchId)
        {
            public static Ids For(string prefix, string suffix) =>
                new($"{prefix}-B-{suffix}", $"{prefix}-WO-{suffix}", $"{prefix}-D-{suffix}");
        }

        /// <summary>分廠主檔（途程、機台）代碼帶廠別：各測試類別用不同廠，共用一個代碼會被參照檢查判為他廠。</summary>
        public static string Routing(string plant) => $"MOBKIT-R-{plant}";
        public static string Machine(string plant) => $"MOBKIT-M-{plant}";

        public static MobileMfgController MakeController(IServiceScope scope, string plant)
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CurrentPlantId = plant;
            db.CurrentRoleId  = RoleId;

            var controller = new MobileMfgController(
                db,
                new FakeUserContext(plant),
                scope.ServiceProvider.GetRequiredService<IReportEtaNotifier>(),
                scope.ServiceProvider.GetRequiredService<ISamplingPlanService>(),
                new AllowAllMobilePermission(),
                scope.ServiceProvider.GetRequiredService<IProductionCostAccrualService>(),
                scope.ServiceProvider.GetRequiredService<IStagingSourceService>(),
                scope.ServiceProvider.GetRequiredService<IFpiGateService>(),
                scope.ServiceProvider.GetRequiredService<IMfgSimplifiedModeService>(),
                scope.ServiceProvider.GetRequiredService<IOperationSequenceGuard>());
            // 報工核心從 HttpContext.RequestServices 取模具打次服務
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider }
            };
            return controller;
        }

        /// <summary>建一張第一道工序的派工（順序守門天生不擋，失敗只會來自受測的判定）。</summary>
        public static void SeedDispatch(ApplicationDbContext db, string plant, Ids ids,
            MfgM.DispatchStatus status, decimal quantity = 100, decimal qtyDone = 0, bool requiresFpi = false)
        {
            var now = DateTime.UtcNow;
            db.ProductionBatches.Add(new MfgM.ProductionBatch
            {
                BatchId = ids.BatchId, MrpRunId = "MOBKIT-RUN", MrpRunLineSeq = 1,
                ProductCode = "MOBKIT-P", Quantity = quantity, Priority = 50,
                PlannedEndDate = now.AddDays(7), PlantId = plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrders.Add(new MfgM.WorkOrder
            {
                WorkOrderId = ids.WoId, ProductionBatchId = ids.BatchId, ProductCode = "MOBKIT-P",
                RoutingId = Routing(plant), Quantity = quantity, Status = MfgM.WorkOrderStatus.InProgress,
                PlannedStartDate = now, PlannedEndDate = now.AddDays(7),
                PlantId = plant, CreatedByRoleId = RoleId,
            });
            db.WorkOrderOperations.Add(new MfgM.WorkOrderOperation
            {
                WorkOrderId = ids.WoId, OperationSeq = 10, WorkCenterId = "MOBKIT-WC",
                UnitTime = 1, BatchQty = 1, OperationName = "MOBKIT OP",
                RequiresInspection = false, RequiresFpi = requiresFpi,
            });
            db.Dispatches.Add(new MfgM.Dispatch
            {
                DispatchId = ids.DispatchId, WorkOrderId = ids.WoId, OperationSeq = 10,
                Quantity = quantity, QtyDone = qtyDone, Status = status,
                MachineCode = Machine(plant), ScheduledStart = now, ScheduledEnd = now.AddHours(2),
                PlantId = plant, CreatedByRoleId = RoleId,
            });
            db.SaveChanges();
        }

        public static string NewClientId() => $"MOBKIT-C-{Guid.NewGuid():N}";

        public static MobileErrorResult AssertError<TResult>(IActionResult result, string code)
            where TResult : ObjectResult
        {
            var obj  = Xunit.Assert.IsType<TResult>(result);
            var body = Xunit.Assert.IsType<MobileErrorResult>(obj.Value);
            Xunit.Assert.Equal(code, body.Error?.Code);
            return body;
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
            // 不可回空集合——RequireMobileModuleAsync 以「動作鍵清單非空」當模組閘門
            public Task<IEnumerable<string>> GetAllowedActionKeysAsync(string roleId, string moduleKey)
                => Task.FromResult<IEnumerable<string>>(new[] { "view" });
        }
    }
}
