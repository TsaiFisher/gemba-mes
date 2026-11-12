using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// RolePermissionSeeder 的資料可見範圍（row-level）測試：驗證根 OrganizationUnit 建立、
    /// 「看全部」角色（高階/管理）指派至根 OU、冪等、以及經 Core 全域查詢過濾器確實看得到全部列。
    /// </summary>
    public class RolePermissionSeederDataScopeTests
    {

        /// <summary>選單設定：報表工具開關開啟，讓 Phase 0-B 的三支畫面納入測試範圍。</summary>
        private static IConfiguration MenuConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] = "true",
                })
                .Build();
        private static ServiceProvider BuildProvider(string dbName)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            var core = new Mock<IStringLocalizer<SharedResource>>();
            core.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
            var pz = new Mock<IStringLocalizer<ProjectZSharedResource>>();
            pz.Setup(l => l[It.IsAny<string>()]).Returns((string k) => new LocalizedString(k, k));
            services.AddSingleton<IMenuContributor>(new CoreMenuContributor(core.Object));
            services.AddSingleton<IMenuContributor>(new ProjectZMenuContributor(pz.Object, MenuConfig()));
            services.AddSingleton<IMenuContributor>(new UserSettingsMenuContributor(core.Object));

            return services.BuildServiceProvider();
        }

        [Fact]
        public async Task Seeds_root_OU_and_assigns_only_view_all_roles()
        {
            var sp = BuildProvider(nameof(Seeds_root_OU_and_assigns_only_view_all_roles));
            using (var scope = sp.CreateScope())
                await RolePermissionSeeder.SeedAsync(scope.ServiceProvider);

            using var verify = sp.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var root = await db.OrganizationUnits.IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == RolePermissionSeeder.RootOrgId);
            Assert.NotNull(root);
            Assert.False(string.IsNullOrEmpty(root!.Path));
            // 根 OU 必為全公司（PlantId=null）：綁廠別會在真 PG 的 OU Include 被過濾掉 → view-all 失效
            Assert.Null(root.PlantId);

            // 看全部資料角色：系統管理者(1)/高階(HQMGR)/管理(MGR) 皆指派至根 OU
            foreach (var roleId in new[] { "1", "HQMGR", "MGR" })
            {
                var role = await db.Roles.IgnoreQueryFilters().FirstAsync(r => r.Id == roleId);
                Assert.Equal(RolePermissionSeeder.RootOrgId, role.OrganizationUnitId);
            }
            // 部門/業務角色不指派 OU（維持 null-path 部門資料語意）
            Assert.Null((await db.Roles.FirstAsync(r => r.Id == "SALES")).OrganizationUnitId);
            Assert.Null((await db.Roles.FirstAsync(r => r.Id == "WH")).OrganizationUnitId);
        }

        [Fact]
        public async Task Idempotent_rerun_no_duplicate_OU()
        {
            var sp = BuildProvider(nameof(Idempotent_rerun_no_duplicate_OU));
            using (var s1 = sp.CreateScope()) await RolePermissionSeeder.SeedAsync(s1.ServiceProvider);
            using (var s2 = sp.CreateScope()) await RolePermissionSeeder.SeedAsync(s2.ServiceProvider); // 重跑

            using var verify = sp.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await db.OrganizationUnits.IgnoreQueryFilters()
                .CountAsync(o => o.Id == RolePermissionSeeder.RootOrgId));
            Assert.True(await db.MenuPermissions.CountAsync(p => p.RoleId == "HQMGR") > 0);
        }

        [Fact]
        public async Task ViewAll_role_path_sees_all_rows_via_global_filter()
        {
            var sp = BuildProvider(nameof(ViewAll_role_path_sees_all_rows_via_global_filter));
            using (var scope = sp.CreateScope())
                await RolePermissionSeeder.SeedAsync(scope.ServiceProvider);

            using var verify = sp.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "DEPT", Name = "dept", OrganizationUnitPath = null },           // 部門/業務（公開）
                new ApprovalTemplate { Id = "HQ",   Name = "hq",   OrganizationUnitPath = "總公司" },        // 總公司
                new ApprovalTemplate { Id = "SUB",  Name = "sub",  OrganizationUnitPath = "總公司/業務" }    // 總公司後代
            );
            await db.SaveChangesAsync();

            // 模擬「高階/管理」登入：CurrentRolePath = 根 OU path → 應看得到全部
            db.CurrentRolePath = RolePermissionSeeder.RootOrgName;
            var visible = await db.ApprovalTemplates.Select(t => t.Id).ToListAsync();
            Assert.Contains("DEPT", visible);
            Assert.Contains("HQ", visible);
            Assert.Contains("SUB", visible);
        }

        [Fact]
        public async Task Department_role_without_path_sees_only_own_role_rows()
        {
            var sp = BuildProvider(nameof(Department_role_without_path_sees_only_own_role_rows));
            using (var scope = sp.CreateScope())
                await RolePermissionSeeder.SeedAsync(scope.ServiceProvider);

            using var verify = sp.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ApprovalTemplates.AddRange(
                new ApprovalTemplate { Id = "MINE",   Name = "mine",   CreatedByRoleId = "SALES", OrganizationUnitPath = null },
                new ApprovalTemplate { Id = "OTHERS", Name = "others", CreatedByRoleId = "WH",    OrganizationUnitPath = null }
            );
            await db.SaveChangesAsync();

            // 業務（OU=null）：空 RolePath → 僅看自己 RoleId 的列
            db.CurrentRoleId = "SALES";
            db.CurrentRolePath = null;
            var visible = await db.ApprovalTemplates.Select(t => t.Id).ToListAsync();
            Assert.Contains("MINE", visible);
            Assert.DoesNotContain("OTHERS", visible);
        }
    }
}
