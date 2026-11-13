using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 從真的管線驗 cookie 的 Secure 旗標。登入 cookie 自己有設 SecurePolicy，但防偽（antiforgery）、
    /// TempData、語系這些 cookie 各有各的寫入點，預設都不帶 Secure——2026-11-12 對穩定版做被動掃描時
    /// 掃到登入頁的防偽 cookie 沒有 Secure。改由管線的 cookie 政策統一補上，這裡釘住結果：
    /// HTTPS 請求寫出的每一個 cookie 都要有 Secure；HTTP（本機開發）維持不帶，否則瀏覽器不收。
    /// </summary>
    [Collection("Integration")]
    public class CookieSecureFlagIntegrationTests
    {
        private readonly TestWebApplicationFactory _factory;
        public CookieSecureFlagIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

        private async Task<List<string>> LoginPageSetCookiesAsync(string scheme)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri($"{scheme}://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = false,
            });
            var resp = await client.GetAsync("/Identity/Account/Login");
            resp.EnsureSuccessStatusCode();
            return resp.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : new List<string>();
        }

        private static bool HasSecure(string setCookie) =>
            setCookie.Split(';').Any(p => p.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

        [Fact]
        public async Task Https_EveryCookieIsSecure()
        {
            var cookies = await LoginPageSetCookiesAsync("https");

            // 沒掃到防偽 cookie 代表這條測試在空轉（登入頁改版或路徑變了）
            Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
            Assert.All(cookies, c => Assert.True(HasSecure(c), $"HTTPS 回應的 cookie 沒有 Secure：{c.Split('=')[0]}"));
        }

        [Fact]
        public async Task Http_AntiforgeryCookieStaysUsable()
        {
            var cookies = await LoginPageSetCookiesAsync("http");

            var af = Assert.Single(cookies, c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
            Assert.False(HasSecure(af), "HTTP 請求不該寫出 Secure cookie——非 localhost 的 HTTP 測試環境會收不到防偽 cookie、所有表單 400");
        }
    }
}
