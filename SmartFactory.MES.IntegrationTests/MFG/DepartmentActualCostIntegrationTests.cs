using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.MFG
{
    /// <summary>
    /// 部門實際費用維護畫面端到端：
    /// (1) 受 [Authorize] 保護，未登入轉址登入頁；
    /// (2) 唯一索引 (PlantId, PeriodId, WorkCenterCode) 在真實 PostgreSQL 生效，防重複輸入。
    /// </summary>
    [Collection("Integration")]
    public class DepartmentActualCostIntegrationTests
    {
        private const string Plant = "ZZDAC";
        private const string Period = "209907";
        private const string Wc = "DAC-WC1";

        private readonly TestWebApplicationFactory _factory;

        public DepartmentActualCostIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Get_Index_Unauthenticated_RedirectsToLogin()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var response = await client.GetAsync("/DepartmentActualCost");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/Identity/Account/Login", response.Headers.Location!.ToString());
        }

        [Fact]
        public async Task UniqueIndex_RejectsDuplicatePeriodWorkCenter()
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Purge(db);

                db.Set<DepartmentActualCost>().Add(new DepartmentActualCost
                {
                    PeriodId = Period, WorkCenterCode = Wc,
                    ActualLaborCost = 1200m, ActualOverheadCost = 600m, PlantId = Plant,
                });
                await db.SaveChangesAsync();
            }

            // 另開 scope，插入同 (廠別,期間,工作中心) → 唯一索引必須擋下
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Set<DepartmentActualCost>().Add(new DepartmentActualCost
                {
                    PeriodId = Period, WorkCenterCode = Wc,
                    ActualLaborCost = 9m, ActualOverheadCost = 9m, PlantId = Plant,
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            using (var scope = _factory.Services.CreateScope())
                Purge(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        }

        private static void Purge(ApplicationDbContext db) =>
            db.Set<DepartmentActualCost>().IgnoreQueryFilters()
                .Where(d => d.PlantId == Plant && d.PeriodId == Period)
                .ExecuteDelete();
    }
}
