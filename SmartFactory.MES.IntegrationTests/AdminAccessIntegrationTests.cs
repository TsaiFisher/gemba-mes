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
    /// 「管理系統」的選單與實際權限一致（2026-11-11 筆電驗收）。
    ///
    /// <para>原本選單照權限矩陣顯示，程式卻寫死 <c>[Authorize(Roles = "Admin")]</c>（看所屬角色）：
    /// 「管理人員」看得到稽核紀錄、代碼維護、系統參數，點進去全是 Access denied；
    /// 反過來屬於 Admin 的人切成別的角色後，選單沒了、網址照樣進得去。</para>
    ///
    /// <para>現在分兩組：帳號／角色／組織／廠別／權限設定／2FA 重置只看作用中角色是否 Admin；
    /// 稽核紀錄／代碼維護／系統參數／簽核範本／簽核設定依權限矩陣。專屬角色與使用者，跑前跑後都清。</para>
    /// </summary>
    /// <summary>
    /// 關掉「Admin 必須啟用兩步驟驗證」：測試帳號屬於 Admin，乾淨部署（CI）預設強制，
    /// 每個頁面都會被導去 /TwoFactor/Setup，測不到權限本身。本機開發設定是關的，所以只在 CI 紅。
    /// 設定只套在這個測試主機上，不影響其他測試。
    /// </summary>
    public class AdminAccessFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Security:TwoFactorEnforcementEnabled", "false");
        }
    }

    [Collection("Integration")]
    public class AdminAccessIntegrationTests : IAsyncLifetime, IClassFixture<AdminAccessFactory>
    {
        private const string UserName = "itest-admin-access";
        private const string Password = "Password123!";
        private const string ManagerRole = "ITEST-AA-MANAGER";   // 權限矩陣勾了 CodeEntry 讀取＋使用者管理讀取

        private readonly AdminAccessFactory _factory;

        public AdminAccessIntegrationTests(AdminAccessFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            await roles.CreateAsync(new ApplicationRole { Id = ManagerRole, Name = ManagerRole });
            if (!await roles.RoleExistsAsync("Admin"))
                await roles.CreateAsync(new ApplicationRole { Name = "Admin" });
            db.MenuPermissions.AddRange(
                new MenuPermission { RoleId = ManagerRole, MenuKey = "CodeEntry", CanRead = true },
                // 矩陣裡勾了也不該生效：使用者管理只限作用中角色為 Admin
                new MenuPermission { RoleId = ManagerRole, MenuKey = "UserManagement", CanRead = true });
            await db.SaveChangesAsync();

            var user = new ApplicationUser { UserName = UserName, Email = $"{UserName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, ManagerRole);
            await users.AddToRoleAsync(user, "Admin");   // 也屬於 Admin，但作用中角色是 Manager
            user.ActiveRoleId = ManagerRole;
            await users.UpdateAsync(user);
        }

        public Task DisposeAsync() => CleanupAsync();

        [Fact]
        public async Task 非Admin作用中角色_矩陣項目進得去_Admin專屬項目選單不顯示也進不去()
        {
            var client = await LoginAsync();

            // 依矩陣：有讀取就進得去，沒有新增就不能新增
            Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(client, "/CodeEntry")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetPageAsync(client, "/CodeEntry/Create")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetPageAsync(client, "/AuditLog")).StatusCode);

            // 只限 Admin：雖然所屬角色有 Admin、矩陣也勾了使用者管理，作用中角色不是 Admin 就不行
            Assert.Equal(HttpStatusCode.Forbidden, (await GetPageAsync(client, "/User")).StatusCode);

            var home = WebUtility.HtmlDecode(await (await GetPageAsync(client, "/UserSettings")).Content.ReadAsStringAsync());
            Assert.Contains("href=\"/CodeEntry\"", home);
            Assert.DoesNotContain("href=\"/User\"", home);
            Assert.DoesNotContain("href=\"/AuditLog\"", home);
            Assert.DoesNotContain("href=\"/License\"", home);   // 授權狀態入口只給 Admin
        }

        [Fact]
        public async Task 切到Admin角色後_Admin專屬項目進得去()
        {
            var client = await LoginAsync();
            string adminId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                adminId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).SingleAsync();
                await db.Users.Where(u => u.UserName == UserName)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.ActiveRoleId, adminId));
            }

            Assert.Equal(HttpStatusCode.OK, (await GetPageAsync(client, "/User")).StatusCode);
            var home = WebUtility.HtmlDecode(await (await GetPageAsync(client, "/UserSettings")).Content.ReadAsStringAsync());
            Assert.Contains("href=\"/User\"", home);
            Assert.Contains("href=\"/License\"", home);   // 授權狀態入口（2026-11-12 rc4 裝機回報：原本找不到）
        }

        private async Task<HttpClient> LoginAsync()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            var login = await client.PostAsJsonAsync("/api/mobile/auth/login", new { Username = UserName, Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return client;
        }

        private static Task<HttpResponseMessage> GetPageAsync(HttpClient client, string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("text/html");
            return client.SendAsync(req);
        }

        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userIds = db.Users.Where(u => u.UserName == UserName).Select(u => u.Id);
            await db.UserRoles.Where(ur => userIds.Contains(ur.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.UserName == UserName).ExecuteDeleteAsync();
            await db.MenuPermissions.Where(p => p.RoleId == ManagerRole).ExecuteDeleteAsync();
            await db.Roles.Where(r => r.Id == ManagerRole).ExecuteDeleteAsync();
        }
    }
}
