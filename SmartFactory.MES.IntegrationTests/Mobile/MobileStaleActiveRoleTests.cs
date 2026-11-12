using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 使用者被移出作用中角色後，手機選單不得再照那個角色給。
    ///
    /// <para>2026-10-07 人工驗收 P-8：dev 只屬於 Operator（手機權限全關），手機選單卻全部出現、也進得去。
    /// 根因是 dev 2 月時被改掉角色，ActiveRoleId 卻一直停在 Admin；MobileModuleController 只看 ActiveRoleId。
    /// 單元測試的 UserContext 用 InMemory，這裡走真的登入＋真的 PG，守的是整條鏈。</para>
    ///
    /// <para>專屬角色／模組／使用者，跑前跑後都清——共用測試庫跨進程，不碰 Admin（見
    /// project-shared-test-db-cross-worktree-permission-clobber）。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileStaleActiveRoleTests : IAsyncLifetime
    {
        private const string UserName       = "itest-stale-role";
        private const string Password       = "Password123!";
        private const string GrantedRoleId  = "ITEST-STALE-GRANTED";   // 有手機權限、使用者已被移出
        private const string MemberRoleId   = "ITEST-STALE-MEMBER";    // 使用者實際所屬、沒有手機權限
        private const string ModuleId       = "ITEST-STALE-MOD";
        private const string ActionId       = "ITEST-STALE-ACT";

        private readonly TestWebApplicationFactory _factory;

        public MobileStaleActiveRoleTests(TestWebApplicationFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            await roles.CreateAsync(new ApplicationRole { Id = GrantedRoleId, Name = GrantedRoleId });
            await roles.CreateAsync(new ApplicationRole { Id = MemberRoleId,  Name = MemberRoleId });

            db.MobileModules.Add(new MobileModule { Id = ModuleId, ModuleKey = ModuleId, DisplayName = ModuleId, IsActive = true });
            db.MobileModuleActions.Add(new MobileModuleAction { Id = ActionId, ModuleId = ModuleId, ActionKey = "View", DisplayName = "View" });
            db.MobileRolePermissions.Add(new MobileRolePermission { RoleId = GrantedRoleId, ModuleActionId = ActionId, IsAllowed = true });
            await db.SaveChangesAsync();

            var user = new ApplicationUser { UserName = UserName, Email = $"{UserName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, MemberRoleId);

            // 重現現場資料：作用中角色指向一個已經不屬於的角色。
            user.ActiveRoleId = GrantedRoleId;
            await users.UpdateAsync(user);
        }

        public Task DisposeAsync() => CleanupAsync();

        [Theory]
        [InlineData("/switch-role")]
        [InlineData("/UserSettings/SwitchRole")]
        public async Task Modules_角色撤銷時拒絕存取_切回所屬角色後恢復(string switchRoute)
        {
            // ProjectZ 的登入 cookie 是 SecurePolicy.Always（Core 是 SameAsRequest）：走 http 時
            // cookie 不會被送回，下一個請求就是 401。用 https 讓 cookie 在兩邊都帶得回去。
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            var login = await client.PostAsJsonAsync("/api/mobile/auth/login", new { Username = UserName, Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var response = await client.GetAsync("/api/mobile/modules");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // Recovery must remain available without exposing business data. Exercise both
            // role-switch routes with a real antiforgery token and persisted role membership.
            var settings = await client.GetAsync("/UserSettings");
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            var html = await settings.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(token.Success, "Recovery page did not render an antiforgery token.");
            var switched = await client.PostAsync(switchRoute, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["roleId"] = MemberRoleId,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
            }));
            Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(MemberRoleId, await db.Users.Where(u => u.UserName == UserName)
                    .Select(u => u.ActiveRoleId).SingleAsync());
            }
            var recovered = await client.GetAsync("/api/mobile/modules");
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var body = await recovered.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(body.GetProperty("modules").EnumerateArray());

            // 沒有權限的桌機頁面：原本導向 Identity DefaultUI 的 /Identity/Account/AccessDenied，
            // 那頁只有英文（「Access denied」），中文介面也照樣顯示（2026-11-11 筆電驗收 A5）。
            // 現在直接 403、由 /Error/403 以目前語系顯示，網址不變。
            using var page = new HttpRequestMessage(HttpMethod.Get, "/Organization");
            page.Headers.Accept.ParseAdd("text/html");
            var denied = await client.SendAsync(page);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Null(denied.Headers.Location);
            // Razor 會把中文編成 &#x...; 實體，比對前先解碼
            var deniedHtml = WebUtility.HtmlDecode(await denied.Content.ReadAsStringAsync());
            Assert.Contains("存取遭拒", deniedHtml);
            Assert.DoesNotContain("You do not have access to this resource", deniedHtml);
        }

        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await db.MobileRolePermissions.Where(p => p.ModuleActionId == ActionId).ExecuteDeleteAsync();
            await db.MobileModuleActions.Where(a => a.Id == ActionId).ExecuteDeleteAsync();
            await db.MobileModules.Where(m => m.Id == ModuleId).ExecuteDeleteAsync();

            var userIds = db.Users.Where(u => u.UserName == UserName).Select(u => u.Id);
            await db.UserRoles.Where(ur => userIds.Contains(ur.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.UserName == UserName).ExecuteDeleteAsync();
            await db.Roles.Where(r => r.Id == GrantedRoleId || r.Id == MemberRoleId).ExecuteDeleteAsync();
        }
    }
}
