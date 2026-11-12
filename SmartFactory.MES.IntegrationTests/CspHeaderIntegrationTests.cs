using System.Text.RegularExpressions;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 從真的管線驗 CSP 標頭：script-src 只放行本站與「這次請求的 nonce」，style-src 只放行本站。
    /// 任何一條被放寬（加回 'unsafe-inline'／'unsafe-eval'）這裡會紅。用未登入也拿得到的登入頁。
    /// 「inline script 都帶 nonce」另由單元測試 InlineScriptNonceGuardTests 靜態掃描所有 .cshtml——
    /// 有 inline script 的頁面都要登入，而登入頁本身沒有 inline script，從這裡驗會空轉。
    /// </summary>
    [Collection("Integration")]
    public class CspHeaderIntegrationTests
    {
        private readonly TestWebApplicationFactory _factory;
        public CspHeaderIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

        private async Task<(string Csp, string Html)> GetLoginAsync()
        {
            var resp = await _factory.CreateClient().GetAsync("/Identity/Account/Login");
            resp.EnsureSuccessStatusCode();
            var csp = string.Join(";", resp.Headers.GetValues("Content-Security-Policy"));
            return (csp, await resp.Content.ReadAsStringAsync());
        }

        private static string Directive(string csp, string name) =>
            csp.Split(';').Select(d => d.Trim()).First(d => d.StartsWith(name + " ", StringComparison.Ordinal));

        [Fact]
        public async Task ScriptAndStyleSrc_AreStrict()
        {
            var (csp, _) = await GetLoginAsync();

            var script = Directive(csp, "script-src");
            Assert.DoesNotContain("'unsafe-inline'", script);
            Assert.DoesNotContain("'unsafe-eval'", script);
            Assert.Matches(@"'nonce-[A-Za-z0-9_-]{22}'", script);
            Assert.DoesNotContain("'unsafe-inline'", Directive(csp, "style-src"));
            Assert.Contains("object-src  'none'", csp.Replace(";", ""));
            Assert.Contains("frame-ancestors 'self'", csp);
        }

        [Fact]
        public async Task NonceChangesEveryRequest()
        {
            var a = Directive((await GetLoginAsync()).Csp, "script-src");
            var b = Directive((await GetLoginAsync()).Csp, "script-src");
            Assert.NotEqual(a, b);
        }
    }
}
