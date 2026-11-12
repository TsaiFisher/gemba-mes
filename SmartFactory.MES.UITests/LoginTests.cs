using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests
{
    [Collection("UI Tests")]
    public class LoginTests
    {
        private readonly UiTestFixture _fixture;

        public LoginTests(UiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Login_ValidCredentials_RedirectsToHome()
        {
            // 乾淨 cookie：共用 Context 早已由 fixture 登入，用它驗不到「登入」這件事本身。
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            await using (context)
            {
                var loginPage = new LoginPage(page, _fixture.BaseUrl);

                // 密碼取自 fixture：乾淨 DB 的 admin 帶 MustChangePassword，
                // 首登會被強制改密，之後的有效密碼不再是種子預設值。
                await loginPage.GotoAsync();
                await loginPage.LoginAsync(_fixture.AdminUserName, _fixture.AdminPassword);

                await page.WaitForFunctionAsync(UiTestFixture.HomeTitleCheck);
                Assert.True(await page.EvaluateAsync<bool>(UiTestFixture.HomeTitleCheck), await page.TitleAsync());
            }
        }

        [Fact]
        public async Task Login_InvalidCredentials_ShowsError()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            await using (context)
            {
                var loginPage = new LoginPage(page, _fixture.BaseUrl);

                await loginPage.GotoAsync();
                await loginPage.LoginAsync(_fixture.AdminUserName, "WrongPassword");

                bool isErrorVisible = await loginPage.IsErrorMessageVisibleAsync();
                Assert.True(isErrorVisible, "Error message should be visible for invalid login.");
            }
        }
    }
}
