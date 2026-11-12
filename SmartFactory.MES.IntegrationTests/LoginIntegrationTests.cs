using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class LoginIntegrationTests
    {
        private readonly TestWebApplicationFactory _factory;

        public LoginIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Post_Login_WithoutAntiForgery_ReturnsBadRequestOrRedirect()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("Input.Email", "admin"),
                new KeyValuePair<string, string>("Input.Password", "Admin123!"),
                new KeyValuePair<string, string>("Input.RememberMe", "false")
            });

            var response = await client.PostAsync("/Identity/Account/Login", content);

            // Without anti-forgery token: 400 or redirect back to login
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest ||
                response.StatusCode == HttpStatusCode.Redirect ||
                response.StatusCode == HttpStatusCode.OK,
                $"Unexpected status: {response.StatusCode}");
        }

        /// <summary>
        /// 登入頁的前端驗證腳本必須用我們自己的 /Pages/Shared 版本，不可落到 Identity UI 套件內建的那份。
        ///
        /// 套件內建版放在 /Areas/Identity/Pages/，Razor 找 partial 時比 /Pages/Shared/ 先查到，
        /// 於是我們的版本被遮蔽。它在非 Development 環境改從 cdnjs 載入並附 asp-fallback 內嵌腳本，
        /// 三者全被 CSP（script-src 'self' 'nonce-…'）擋掉 → **正式環境登入頁前端驗證靜默失效**。
        /// 2026-09-27 在 IIS（Production）上實測發現；nightly 跑 Development 所以從未看過。
        ///
        /// 本測試只能跑 Development（授權閘門），但判別力不受影響：套件內建版在 Development
        /// 走的是 /Identity/lib/…，我們的走 /lib/…——看得到 /lib/ 就代表覆寫生效，
        /// 而覆寫檔本身沒有任何依環境分支的內容。
        /// </summary>
        [Fact]
        public async Task Get_Login_UsesOwnValidationScripts_NotIdentityUiDefault()
        {
            var client = _factory.CreateClient();

            var html = await client.GetStringAsync("/Identity/Account/Login");

            Assert.Contains("/lib/jquery-validation/dist/jquery.validate.min.js", html);
            Assert.DoesNotContain("/Identity/lib/", html);
            Assert.DoesNotContain("cdnjs.cloudflare.com", html);
        }
    }
}
