using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 廠別預設語言（2026-10-27）：個人語言留空的帳號，桌機登入後以所在廠別的預設語言顯示；
    /// 有個人語言的以個人為準。原本 Plant.DefaultLanguage 沒有任何作用，個人留空時登入不寫語系 cookie。
    ///
    /// <para>⚠️ 共用的 admin 帳號與它所在廠別：只改語言兩個欄位，finally 一律還原。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class PlantDefaultLanguageUiTests
    {
        private readonly UiTestFixture _fixture;

        public PlantDefaultLanguageUiTests(UiTestFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task 個人語言留空_登入後用廠別預設語言_有個人語言時以個人為準()
        {
            var plant = await SeedFixture.ScalarAsync<string>("""SELECT "ActivePlantId" FROM "AspNetUsers" WHERE "UserName"='admin'""");
            Assert.False(string.IsNullOrEmpty(plant), "admin 沒有 ActivePlantId。");
            var origPersonal = await SeedFixture.ScalarAsync<string>("""SELECT "Language" FROM "AspNetUsers" WHERE "UserName"='admin'""");
            var origPlant    = await SeedFixture.ScalarAsync<string>($"""SELECT "DefaultLanguage" FROM "Plants" WHERE "Id"='{plant}'""");
            try
            {
                await SetAsync(plant!, personal: null, plantDefault: "en-US");
                var (cookie, logoutText) = await SignInFreshAsync();
                Assert.Equal("en-US", cookie);
                Assert.Equal("Logout", logoutText);

                await SetAsync(plant!, personal: "zh-TW", plantDefault: "en-US");
                (cookie, logoutText) = await SignInFreshAsync();
                Assert.Equal("zh-TW", cookie);
                Assert.Equal("登出", logoutText);
            }
            finally
            {
                await SetAsync(plant!, origPersonal, origPlant);
            }
        }

        private static Task SetAsync(string plant, string? personal, string? plantDefault) => SeedFixture.ExecAsync(
            $"""
            UPDATE "AspNetUsers" SET "Language" = {Sql(personal)} WHERE "UserName"='admin';
            UPDATE "Plants" SET "DefaultLanguage" = {Sql(plantDefault)} WHERE "Id"='{plant}';
            """);

        private static string Sql(string? v) => v == null ? "NULL" : $"'{v}'";

        /// <summary>全新瀏覽器環境登入，回傳語系 cookie 的 UI culture 與導覽列登出連結文字。</summary>
        private async Task<(string? Culture, string LogoutText)> SignInFreshAsync()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                var login = new LoginPage(page, _fixture.BaseUrl);
                await login.GotoAsync();
                await login.LoginAsync(_fixture.AdminUserName, _fixture.AdminPassword);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.DoesNotContain("/Identity/Account/Login", page.Url, StringComparison.OrdinalIgnoreCase);

                var raw = (await context.CookiesAsync()).FirstOrDefault(c => c.Name == ".AspNetCore.Culture")?.Value;
                var culture = raw == null ? null
                    : Uri.UnescapeDataString(raw).Split('|').Select(p => p.Split('=')).FirstOrDefault(p => p[0] == "uic")?[1];
                var logout = (await page.Locator("form[action*='Logout'] button, a[href*='Logout']").First.InnerTextAsync()).Trim();
                return (culture, logout);
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
    }
}
