using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 帳號救援（2026-11-16）：管理員重設使用者密碼、伺服器指令 admin-recover、Admin 角色不可改名。
    ///
    /// <para>評估時發現的缺口：系統沒有任何替別人重設密碼的功能（忘記密碼只能刪帳號重建）；
    /// 唯一的管理員出事只能直接改資料庫；Admin 角色一改名所有管理員當場失去管理權限。</para>
    ///
    /// <para>走真登入＋真 PG：重點是「臨時密碼真的登得進去、舊密碼真的失效、下次登入真的被要求改密碼」，
    /// 這些只有整條 Identity 管線跑過才算數。專屬使用者，跑前跑後都清。</para>
    /// </summary>
    [Collection("Integration")]
    public class AdminRecoveryIntegrationTests : IAsyncLifetime, IClassFixture<AdminAccessFactory>
    {
        private const string AdminName = "itest-recover-admin";
        private const string TargetName = "itest-recover-target";
        private const string NewAdminName = "itest-recover-new";
        private const string Password = "Password123!";

        private readonly AdminAccessFactory _factory;

        public AdminRecoveryIntegrationTests(AdminAccessFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            using var scope = _factory.Services.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roles.RoleExistsAsync("Admin"))
                await roles.CreateAsync(new ApplicationRole { Name = "Admin" });
            var adminRoleId = (await roles.FindByNameAsync("Admin"))!.Id;

            var admin = new ApplicationUser { UserName = AdminName, Email = $"{AdminName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(admin, Password)).Succeeded);
            await users.AddToRoleAsync(admin, "Admin");
            admin.ActiveRoleId = adminRoleId;
            await users.UpdateAsync(admin);

            var target = new ApplicationUser { UserName = TargetName, Email = $"{TargetName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(target, Password)).Succeeded);
        }

        public Task DisposeAsync() => CleanupAsync();

        [Fact]
        public async Task 管理員重設密碼_臨時密碼登得進去_舊密碼失效_下次登入要改密碼_鎖定解除()
        {
            string targetId;
            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var target = (await users.FindByNameAsync(TargetName))!;
                targetId = target.Id;
                // 被鎖定＋已啟用兩步驟驗證：重設密碼要解鎖，但不該動兩步驟驗證
                await users.SetLockoutEndDateAsync(target, DateTimeOffset.UtcNow.AddHours(1));
                await users.SetTwoFactorEnabledAsync(target, true);
            }

            var client = await LoginAsync(AdminName, Password);
            var panel = await GetAsync(client, $"/User/ResetPassword/{targetId}");
            Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
            var html = await panel.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id=\"temporaryPassword\"", html);   // 還沒確認，不該先產生

            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(token.Success, "重設密碼面板沒有防偽權杖");
            var post = new HttpRequestMessage(HttpMethod.Post, $"/User/ResetPassword/{targetId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
                })
            };
            post.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var done = await client.SendAsync(post);
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            var shown = Regex.Match(await done.Content.ReadAsStringAsync(), "id=\"temporaryPassword\">([^<]+)<");
            Assert.True(shown.Success, "成功後面板要顯示臨時密碼");
            var temporary = WebUtility.HtmlDecode(shown.Groups[1].Value);

            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var target = (await users.FindByNameAsync(TargetName))!;
                Assert.True(await users.CheckPasswordAsync(target, temporary));
                Assert.False(await users.CheckPasswordAsync(target, Password));
                Assert.True(target.MustChangePassword);
                Assert.False(await users.IsLockedOutAsync(target));
                Assert.True(target.TwoFactorEnabled);   // 兩步驟驗證是另一顆按鈕的事
            }
        }

        [Fact]
        public async Task 非管理員不能重設別人的密碼()
        {
            string adminId;
            using (var scope = _factory.Services.CreateScope())
                adminId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(AdminName))!.Id;

            var client = await LoginAsync(TargetName, Password);
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, $"/User/ResetPassword/{adminId}")).StatusCode);
        }

        [Fact]
        public async Task 指令_帳號不存在就建立成管理員_臨時密碼登得進去()
        {
            var output = new StringWriter();
            var exit = await AdminRecovery.RunAsync(_factory.Services, new[] { "admin-recover", NewAdminName }, output);
            Assert.Equal(0, exit);

            var temporary = ExtractPassword(output.ToString());
            using var scope = _factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = (await users.FindByNameAsync(NewAdminName))!;
            Assert.True(await users.CheckPasswordAsync(user, temporary));
            Assert.True(await users.IsInRoleAsync(user, "Admin"));
            Assert.True(user.MustChangePassword);
            Assert.Equal(await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).SingleAsync(), user.ActiveRoleId);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == user.Id && a.UserName == AdminRecovery.CommandName));

            // 整條登入管線也要通：臨時密碼真的登得進去
            await LoginAsync(NewAdminName, temporary);
        }

        [Fact]
        public async Task 指令_救回被取消角色_鎖定_兩步驟驗證卡住的管理員()
        {
            string adminRoleId;
            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var admin = (await users.FindByNameAsync(AdminName))!;
                await users.RemoveFromRoleAsync(admin, "Admin");
                await users.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddHours(1));
                await users.SetTwoFactorEnabledAsync(admin, true);
                adminRoleId = admin.ActiveRoleId!;
                admin.ActiveRoleId = null;
                await users.UpdateAsync(admin);
            }

            var output = new StringWriter();
            Assert.Equal(0, await AdminRecovery.RunAsync(_factory.Services, new[] { "admin-recover", AdminName }, output));
            var temporary = ExtractPassword(output.ToString());

            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var admin = (await users.FindByNameAsync(AdminName))!;
                Assert.True(await users.IsInRoleAsync(admin, "Admin"));
                Assert.Equal(adminRoleId, admin.ActiveRoleId);
                Assert.False(await users.IsLockedOutAsync(admin));
                Assert.False(admin.TwoFactorEnabled);
                Assert.False(await users.CheckPasswordAsync(admin, Password));
            }

            // 救回後真的進得了只限 Admin 的畫面
            var client = await LoginAsync(AdminName, temporary);
            using (var scope = _factory.Services.CreateScope())
            {
                // 改密碼的強制導向不是這支測試的對象：關掉後再確認權限
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Users.Where(u => u.UserName == AdminName)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, false));
            }
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/User")).StatusCode);
        }

        [Theory]
        [InlineData]
        [InlineData("admin-recover")]
        [InlineData("admin-recover", "a", "b")]
        [InlineData("admin-recover", "--help")]
        public async Task 指令_用法錯誤回2_而且不動資料(params string[] args)
        {
            if (args.Length == 0) { Assert.False(AdminRecovery.IsRequested(args)); return; }
            Assert.True(AdminRecovery.IsRequested(args));
            var output = new StringWriter();
            Assert.Equal(2, await AdminRecovery.RunAsync(_factory.Services, args, output));
            Assert.Contains("admin-recover", output.ToString());
        }

        [Fact]
        public async Task Admin角色不可改名也不可刪除()
        {
            string roleId;
            using (var scope = _factory.Services.CreateScope())
                roleId = (await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync("Admin"))!.Id;

            var client = await LoginAsync(AdminName, Password);
            var form = await (await GetAsync(client, $"/Role/Edit/{roleId}")).Content.ReadAsStringAsync();
            var token = WebUtility.HtmlDecode(Regex.Match(form, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            Assert.Matches("name=\"Name\"[^>]*readonly|readonly[^>]*name=\"Name\"", form);

            var rename = new HttpRequestMessage(HttpMethod.Post, $"/Role/Edit/{roleId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Id"] = roleId, ["Name"] = "系統管理員", ["__RequestVerificationToken"] = token
                })
            };
            rename.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var renamed = await (await client.SendAsync(rename)).Content.ReadAsStringAsync();
            Assert.Contains("\"success\":false", renamed);

            var delete = new HttpRequestMessage(HttpMethod.Post, $"/Role/Delete/{roleId}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })
            };
            delete.Headers.Add("X-Requested-With", "XMLHttpRequest");
            Assert.Contains("\"success\":false", await (await client.SendAsync(delete)).Content.ReadAsStringAsync());

            using (var scope = _factory.Services.CreateScope())
                Assert.NotNull(await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync("Admin"));
        }

        [Fact]
        public void 臨時密碼符合密碼規則_而且每次不同()
        {
            var seen = new HashSet<string>();
            for (var i = 0; i < 200; i++)
            {
                var p = AdminRecovery.GenerateTemporaryPassword();
                Assert.Matches("^([A-Za-z2-9]{4}-){3}[A-Za-z2-9]{4}$", p);
                foreach (var group in p.Split('-'))
                {
                    Assert.Contains(group, char.IsUpper);
                    Assert.Contains(group, char.IsLower);
                    Assert.Contains(group, char.IsDigit);
                }
                Assert.True(seen.Add(p));
            }
        }

        private static string ExtractPassword(string output)
        {
            var m = Regex.Match(output, @"([A-Za-z2-9]{4}-){3}[A-Za-z2-9]{4}");
            Assert.True(m.Success, "輸出裡找不到臨時密碼：" + output);
            return m.Value;
        }

        private async Task<HttpClient> LoginAsync(string userName, string password)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            var login = await client.PostAsJsonAsync("/api/mobile/auth/login", new { Username = userName, Password = password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return client;
        }

        private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("text/html");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return client.SendAsync(req);
        }

        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var names = new[] { AdminName, TargetName, NewAdminName };
            var userIds = db.Users.Where(u => names.Contains(u.UserName)).Select(u => u.Id);
            await db.AuditLogs.Where(a => userIds.Contains(a.EntityId!)).ExecuteDeleteAsync();
            await db.UserRoles.Where(ur => userIds.Contains(ur.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => names.Contains(u.UserName)).ExecuteDeleteAsync();
        }
    }
}
