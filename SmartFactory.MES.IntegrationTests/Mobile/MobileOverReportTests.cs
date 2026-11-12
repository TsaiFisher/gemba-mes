using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using MfgM = SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 超量報工（mfg-domain.md §4.6，2026-10-12）：廠別參數容許率＋超過時拒收／限主管。
    /// 判定走真的 <c>IOverQuantityPolicy</c>（DI）＋真的手機權限表——「主管」＝角色有 OverReportApprove。
    /// 派工量 100、已完成 10，專屬廠別 ZZMRP；參數與權限列在每支測試收尾清掉（共用測試庫）。
    /// </summary>
    [Collection("Integration")]
    public class MobileOverReportTests : IDisposable
    {
        private const string Plant = "ZZMRP";
        private const string ApproveActionId = "ACT-MF-OVERQTY";
        private readonly TestWebApplicationFactory _factory;
        private readonly MobileMfgTestKit.Ids _ids = MobileMfgTestKit.Ids.For("MOR", Guid.NewGuid().ToString("N")[..8]);
        private string SessionId => _ids.DispatchId.Replace("-D-", "-S-");

        public MobileOverReportTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            Cleanup();
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            MobileMfgTestKit.SeedDispatch(db, Plant, _ids, MfgM.DispatchStatus.InProgress, quantity: 100, qtyDone: 10);
            db.ProductionSessions.Add(new MfgM.ProductionSession
            {
                SessionId = SessionId, DispatchId = _ids.DispatchId, WorkOrderId = _ids.WoId,
                Status = MfgM.ProductionSessionStatus.Open, StartAt = DateTime.UtcNow.AddHours(-1),
                OperatorId = "admin", MachineCode = "MOBKIT-M", PlantId = Plant,
            });
            db.SaveChanges();
        }

        public void Dispose() => Cleanup();

        private void Cleanup()
        {
            using var scope = _factory.Services.CreateScope();
            var pp = scope.ServiceProvider.GetRequiredService<IPlantParamService>();
            foreach (var k in new[] { ProjectZSystemParamKeys.MfgOverReportTolerancePct, ProjectZSystemParamKeys.MfgOverReportAction })
                pp.ClearAsync(k, Plant).GetAwaiter().GetResult();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MobileRolePermissions.IgnoreQueryFilters()
                .Where(p => p.RoleId == MobileMfgTestKit.RoleId && p.ModuleActionId == ApproveActionId).ExecuteDelete();
        }

        private async Task SetAsync(string key, string value)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IPlantParamService>().SetAsync(key, Plant, value);
        }

        private async Task GrantSupervisorAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MobileRolePermissions.Add(new MobileRolePermission
                { RoleId = MobileMfgTestKit.RoleId, ModuleActionId = ApproveActionId, IsAllowed = true });
            await db.SaveChangesAsync();
        }

        private async Task<IActionResult> ReportAsync(decimal qtyOk)
        {
            using var scope = _factory.Services.CreateScope();
            return await MobileMfgTestKit.MakeController(scope, Plant).CreateReport(new ReportCreateRequest(
                SessionId, qtyOk, 0, ContainerId: null, ClientId: MobileMfgTestKit.NewClientId(), ClientTimestamp: DateTime.UtcNow));
        }

        private async Task<decimal> QtyDoneAsync()
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId == _ids.DispatchId).Select(d => d.QtyDone).SingleAsync();
        }

        [Fact]
        public async Task 未設定參數_超過派工量一個都擋_與原本相同()
        {
            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await ReportAsync(91), "OVER_REPORT");
            Assert.Equal(10, await QtyDoneAsync());
        }

        [Fact]
        public async Task 容許率5趴_報到105照收_派工結案_超過105擋()
        {
            await SetAsync(ProjectZSystemParamKeys.MfgOverReportTolerancePct, "5");

            MobileMfgTestKit.AssertError<BadRequestObjectResult>(await ReportAsync(96), "OVER_REPORT");   // 累計 106
            Assert.IsType<OkObjectResult>(await ReportAsync(95));                                          // 累計 105
            Assert.Equal(105, await QtyDoneAsync());
        }

        [Fact]
        public async Task 超過上限設成需主管確認_沒有超量核准權限擋下_授權後照收()
        {
            await SetAsync(ProjectZSystemParamKeys.MfgOverReportAction, "Supervisor");

            var denied = MobileMfgTestKit.AssertError<BadRequestObjectResult>(await ReportAsync(100), "OVER_REPORT");
            Assert.False(string.IsNullOrWhiteSpace(denied.Error?.Message));

            await GrantSupervisorAsync();
            Assert.IsType<OkObjectResult>(await ReportAsync(100));
            Assert.Equal(110, await QtyDoneAsync());
        }
    }
}
