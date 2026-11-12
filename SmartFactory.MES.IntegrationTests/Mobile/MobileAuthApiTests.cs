using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    [Collection("Integration")]
    public class MobileAuthApiTests
    {
        private readonly TestWebApplicationFactory _factory;

        public MobileAuthApiTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// 測 2FA 挑戰的案例必須用這個 client：開發機的 appsettings.Development.json（不進版控）設了
        /// <c>Security:TwoFactorEnforcementEnabled=false</c>，而測試跑在 Development——
        /// <see cref="DevTwoFactorBypassSignInManager"/> 因此生效、登入直接成功，
        /// 這組測試在開發機上就會紅（CI 沒有那個檔，所以是綠的）。以最後加入的組態蓋回 true，
        /// 讓結果不再取決於哪台機器跑。
        /// </summary>
        private HttpClient CreateEnforcedClient() => _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DevTwoFactorBypassSignInManager.EnforcementConfigKey] = "true",
            }))).CreateClient();

        private async Task<ApplicationUser> EnsureTwoFactorUserAsync(string userName)
        {
            using var scope = _factory.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var user = await userManager.FindByNameAsync(userName);
            if (user == null)
            {
                user = new ApplicationUser { UserName = userName, Email = $"{userName}@test.com" };
                await userManager.CreateAsync(user, "Password123!");
            }
            await userManager.ResetAuthenticatorKeyAsync(user);
            await userManager.SetTwoFactorEnabledAsync(user, true);
            // 錯誤驗證碼測試會累積 AccessFailedCount（共用 PG 庫跨輪保留），
            // 多跑幾輪後觸發 Identity 鎖戶、第一步登入即 401 → 測試假紅。種子時重置。
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);
            return user;
        }

        [Fact]
        public async Task Login_WithInvalidCredentials_Returns401()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "nonexistent", Password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Login_WithEmptyBody_Returns400()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "", Password = "" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Login_WithTwoFactorEnabled_ReturnsOkWithRequiresTwoFactor()
        {
            // 回歸防護：修正前，密碼正確但帳號啟用 2FA 時會被當成一般登入失敗回 401，
            // 導致手機版 apiCall 的全域 401 攔截器誤判為 session 過期，強制清空並導回登入頁
            // ——使用者輸入完帳密後又看到帳密輸入畫面，永遠無法進入驗證碼輸入步驟。
            await EnsureTwoFactorUserAsync("mobile2faUserA");

            var client = CreateEnforcedClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "mobile2faUserA", Password = "Password123!" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("success").GetBoolean());
            Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
        }

        [Fact]
        public async Task LoginTwoFactor_WithoutPendingChallenge_Returns401()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/mobile/auth/login-2fa", new { Code = "123456" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task LoginTwoFactor_WithWrongCodeAfterPendingChallenge_Returns401()
        {
            await EnsureTwoFactorUserAsync("mobile2faUserB");

            // HandleCookies 預設為 true：同一 client 的後續請求會帶上 Login 產生的
            // IdentityConstants.TwoFactorUserIdScheme 暫存 Cookie。
            var client = CreateEnforcedClient();
            var loginResponse = await client.PostAsJsonAsync("/api/mobile/auth/login",
                new { Username = "mobile2faUserB", Password = "Password123!" });
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

            var twoFaResponse = await client.PostAsJsonAsync("/api/mobile/auth/login-2fa", new { Code = "000000" });
            Assert.Equal(HttpStatusCode.Unauthorized, twoFaResponse.StatusCode);
        }

        [Fact]
        public async Task Me_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var response = await client.GetAsync("/api/mobile/auth/me");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Modules_WithoutAuth_Returns401()
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var response = await client.GetAsync("/api/mobile/modules");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
