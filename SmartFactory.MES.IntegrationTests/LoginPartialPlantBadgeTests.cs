using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 頂端廠別徽章不得在廠別失效時假裝已選好。
    ///
    /// <para>2026-11-11 筆電驗收 B2：管理員把使用者目前的廠別（二廠）從角色移除後，
    /// 頂端仍顯示「一廠」——<c>_LoginPartial</c> 找不到作用中廠別時退為清單第一個顯示。
    /// 實際上所有功能都被擋下、使用者設定裡的一廠也沒有「使用中」，畫面卻像一切正常。</para>
    ///
    /// <para>專屬廠別／角色／使用者，跑前跑後都清（共用測試庫跨進程）。</para>
    /// </summary>
    [Collection("Integration")]
    public class LoginPartialPlantBadgeTests : IAsyncLifetime
    {
        private const string UserName = "itest-plant-badge";
        private const string Password = "Password123!";
        private const string RoleId   = "ITEST-BADGE-ROLE";
        private const string PlantA   = "ITBADGE-A";
        private const string PlantB   = "ITBADGE-B";
        private const string NameA    = "徽章測試甲廠";
        private const string NameB    = "徽章測試乙廠";

        private readonly TestWebApplicationFactory _factory;

        public LoginPartialPlantBadgeTests(TestWebApplicationFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            db.Plants.AddRange(new Plant { Id = PlantA, Name = NameA }, new Plant { Id = PlantB, Name = NameB });
            await db.SaveChangesAsync();
            await roles.CreateAsync(new ApplicationRole { Id = RoleId, Name = RoleId });
            // 角色只剩甲廠——乙廠已被管理員移除
            db.RolePlantAssignments.Add(new RolePlantAssignment { RoleId = RoleId, PlantId = PlantA, IsDefault = true });
            await db.SaveChangesAsync();

            var user = new ApplicationUser { UserName = UserName, Email = $"{UserName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, RoleId);
            user.ActiveRoleId = RoleId;
            user.ActivePlantId = PlantB;
            await users.UpdateAsync(user);
        }

        public Task DisposeAsync() => CleanupAsync();

        [Fact]
        public async Task 廠別失效時頂端顯示警示而不是第一個廠_重選後恢復顯示廠名()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            var login = await client.PostAsJsonAsync("/api/mobile/auth/login", new { Username = UserName, Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // /UserSettings 是復原入口，廠別失效時仍會渲染版面（頂端徽章在 _LoginPartial）
            var stale = await GetHeaderAsync(client);
            Assert.Contains("廠別已失效", stale);
            Assert.DoesNotContain(NameA, stale);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Users.Where(u => u.UserName == UserName)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.ActivePlantId, PlantA));
            }

            var valid = await GetHeaderAsync(client);
            Assert.Contains(NameA, valid);
            Assert.DoesNotContain("廠別已失效", valid);
        }

        /// <summary>只取頂端導覽列：使用者設定頁本文也會列出廠別名稱，不能拿整頁比對。</summary>
        private static async Task<string> GetHeaderAsync(HttpClient client)
        {
            var res = await client.GetAsync("/UserSettings");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            // Razor 會把中文編成 &#x...; 實體，比對前先解碼
            var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
            var end = html.IndexOf("</header>", StringComparison.Ordinal);
            Assert.True(end > 0, "找不到頂端導覽列");
            return html[..end];
        }

        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userIds = db.Users.Where(u => u.UserName == UserName).Select(u => u.Id);
            await db.UserRoles.Where(ur => userIds.Contains(ur.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.UserName == UserName).ExecuteDeleteAsync();
            await db.RolePlantAssignments.Where(x => x.RoleId == RoleId).ExecuteDeleteAsync();
            await db.Roles.Where(r => r.Id == RoleId).ExecuteDeleteAsync();
            await db.Plants.IgnoreQueryFilters().Where(p => p.Id == PlantA || p.Id == PlantB).ExecuteDeleteAsync();
        }
    }
}
