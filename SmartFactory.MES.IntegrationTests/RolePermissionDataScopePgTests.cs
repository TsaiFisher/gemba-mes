using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 在真 PostgreSQL 上驗證資料可見範圍的端到端解析：跑真 RolePermissionSeeder，
    /// 再以 UserContext.EnsureUserLoaded 相同的 Include 查詢（CurrentPlantId=null）解析
    /// 「看全部」角色的 ActiveRolePath。此路徑 InMemory 抓不到（不對 Include 套過濾器），
    /// 必須在真 PG 才能證實根 OU 綁廠別會被過濾、PlantId=null 才解析成功。
    /// </summary>
    public class RolePermissionDataScopePgTests
    {

        /// <summary>選單設定：報表工具開關開啟，讓 Phase 0-B 的三支畫面納入測試範圍。</summary>
        private static IConfiguration MenuConfig() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] = "true",
                })
                .Build();
        private readonly ITestOutputHelper _o;
        public RolePermissionDataScopePgTests(ITestOutputHelper o) => _o = o;

        private sealed class FakeLocalizer<T> : IStringLocalizer<T>
        {
            public LocalizedString this[string name] => new(name, name, false);
            public LocalizedString this[string name, params object[] arguments] => new(name, name, false);
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
                => System.Array.Empty<LocalizedString>();
        }

        [Fact]
        public async Task ViewAll_roles_resolve_root_path_through_include_on_pg()
        {
            var dbName = $"smf_dscope_{System.Guid.NewGuid():N}".ToLowerInvariant();
            var cs = $"Host=localhost;Database={dbName};Username=dev;Password=Dev12345";

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(cs));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            services.AddSingleton<IMenuContributor>(new CoreMenuContributor(new FakeLocalizer<SharedResource>()));
            services.AddSingleton<IMenuContributor>(new ProjectZMenuContributor(new FakeLocalizer<ProjectZSharedResource>(), MenuConfig()));
            services.AddSingleton<IMenuContributor>(new UserSettingsMenuContributor(new FakeLocalizer<SharedResource>()));
            var sp = services.BuildServiceProvider();

            try
            {
                // Schema + 一筆廠別（ActivePlantId FK）
                using (var scope = sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.EnsureCreatedAsync();
                    db.Plants.Add(new Plant { Id = "P1", Name = "廠一" });
                    await db.SaveChangesAsync();
                }

                // 跑真 seeder（建角色 + 選單權限 + 根 OU + 指派 view-all 角色）
                using (var scope = sp.CreateScope())
                    await RolePermissionSeeder.SeedAsync(scope.ServiceProvider);

                // 根 OU 為全公司
                using (var scope = sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var root = await db.OrganizationUnits.IgnoreQueryFilters()
                        .FirstAsync(o => o.Id == RolePermissionSeeder.RootOrgId);
                    Assert.Null(root.PlantId);
                }

                // 各 view-all 角色建一個使用者，模擬 UserContext 解析
                foreach (var roleId in new[] { "1", "HQMGR", "MGR" })
                {
                    using var scope = sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    db.Users.Add(new ApplicationUser
                    {
                        Id = $"u-{roleId}",
                        UserName = $"u-{roleId}",
                        ActiveRoleId = roleId,
                        ActivePlantId = "P1",
                    });
                    await db.SaveChangesAsync();

                    // UserContext.EnsureUserLoaded 相同查詢；middleware 尚未設廠別 → CurrentPlantId=null
                    db.CurrentPlantId = null;
                    var user = await db.Users
                        .Include(u => u.ActiveRole!).ThenInclude(r => r.OrganizationUnit)
                        .FirstOrDefaultAsync(u => u.Id == $"u-{roleId}");
                    var path = user?.ActiveRole?.OrganizationUnit?.Path;
                    _o.WriteLine($"role={roleId} → ActiveRolePath={path ?? "<null>"}");
                    Assert.Equal(RolePermissionSeeder.RootOrgName, path);
                }
            }
            finally
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureDeletedAsync();
            }
        }
    }
}
