using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// ProjectZ 對 Core 隱私權政策的補充段落（Pages/_PrivacyExtra.*）真的接得上。
    /// Core 的通用內容由 PrivacyPageTests 驗；這裡只驗本專案獨有的推播、登出清除、
    /// 查詢助理、自動清除排程四段。整合測試一律開啟 Q&amp;A（見 TestWebApplicationFactory），
    /// 所以查詢助理那段在這裡應該出現。
    /// </summary>
    [Collection("Integration")]
    public class PrivacyExtraTests
    {
        private readonly TestWebApplicationFactory _factory;

        public PrivacyExtraTests(TestWebApplicationFactory factory) => _factory = factory;

        private async Task<string> MainAsync(string culture)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/Privacy");
            request.Headers.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
            var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
            var m = Regex.Match(html, @"<main[^>]*>(.*?)</main>", RegexOptions.Singleline);
            Assert.True(m.Success, "頁面沒有 <main> 區塊");
            return WebUtility.HtmlDecode(m.Groups[1].Value);
        }

        [Fact]
        public async Task 中文版_補充段落接在通用內容之後_聯絡窗口之前()
        {
            var main = await MainAsync("zh-TW");

            var security = main.IndexOf("資料安全", StringComparison.Ordinal);
            var push = main.IndexOf("手機版推播通知", StringComparison.Ordinal);
            var rights = main.IndexOf("您的權利", StringComparison.Ordinal);
            Assert.True(security >= 0 && push > security && rights > push,
                $"段落順序不對：資料安全={security}、推播={push}、您的權利={rights}");

            Assert.Contains("手機版登出時的資料清除", main);
            Assert.Contains("資料查詢助理", main);
            Assert.Contains("操作稽核記錄、簽核歷程：730 天", main);

            // 2026-10-21 現場遙測：會帶帳號、裝置代號與 User-Agent，必須寫明並列出保存期限
            Assert.Contains("手機版運作狀況回報", main);
            Assert.Contains("手機版運作狀況回報：30 天", main);
            // 資安警示 webhook 只有部署單位設了網址才會對外送，沒設就不出現
            Assert.DoesNotContain("資安警示通知", main);
        }

        [Fact]
        public async Task 英文版_使用英文補充段落()
        {
            var main = await MainAsync("en-US");

            Assert.Contains("Mobile push notifications", main);
            Assert.Contains("Data query assistant", main);
            Assert.Contains("Audit logs and approval history: 730 days", main);
            Assert.Contains("Mobile app health reports: 30 days", main);
            Assert.DoesNotContain("手機版推播通知", main);
        }
    }
}
