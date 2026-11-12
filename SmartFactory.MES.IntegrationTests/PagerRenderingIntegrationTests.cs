using System.Net;
using SmartFactory.MES.IntegrationTests.MFG;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 分頁列真的渲染成人看得懂的字，而不是把 resx 鍵名印出來。
    ///
    /// ⚠️ 為什麼非整合測試不可：`ResourceManagerStringLocalizer` 找不到鍵時
    /// **直接把鍵名當成翻譯回傳**——不拋例外、不讓建置失敗、也不會讓任何既有測試變紅。
    /// 畫面上就出現「PagerSummary」這種英文字樣夾在中文介面裡。
    /// 兩支共用 partial 是全庫第一個從 View 注入 IStringLocalizer&lt;SharedResource&gt; 的地方，
    /// 這條解析路徑在此之前從來沒有被走過，單元測試也只驗得到 localizer 本身、驗不到 View。
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class PagerRenderingIntegrationTests
    {
        private readonly AuthedScheduleFactory _factory;

        public PagerRenderingIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        private HttpClient Client() => _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        /// <summary>分頁 partial 會用到的每一個 resx 鍵；任何一個漏掉都會原樣印在畫面上。</summary>
        private static readonly string[] RawKeys =
        {
            "PagerSummary", "PagerNav", "PagerFirst", "PagerPrevious", "PagerNext", "PagerLast",
        };

        /// <summary>
        /// ⚠️ 測試用 client 預設不是 zh-TW——不帶語系直接打，頁面會用 en-US 渲染，
        /// 於是「畫面上有沒有中文」這種斷言會**全部通過但什麼都沒驗到**。
        /// 語系一律由 cookie 明示。
        /// </summary>
        private static async Task<string> GetHtmlAsync(HttpClient client, string url, string culture)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", ".AspNetCore.Culture=" + Uri.EscapeDataString($"c={culture}|uic={culture}"));
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            // Razor 會把 CJK 編成 &#xXXXX;，直接比中文字串永遠是 false
            return System.Net.WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData("/Product?searched=true")]      // BAS 模組：partial 由模組檢視路徑解析
        [InlineData("/Uom?searched=true")]
        [InlineData("/BankAccount?searched=true")]  // FIN 模組
        public async Task 分頁列不得把資源鍵名印在畫面上(string url)
        {
            using var client = Client();
            var html = await GetHtmlAsync(client, url, "zh-TW");

            foreach (var key in RawKeys)
                Assert.False(html.Contains(key, StringComparison.Ordinal),
                    $"{url}：畫面上出現未翻譯的資源鍵 '{key}'——localizer 沒解析到，會原樣印給使用者看");

            // 每頁筆數下拉在有沒有資料都會渲染，可以無條件斷言
            Assert.Contains("每頁筆數", html);
            Assert.Contains("name=\"pageSize\"", html);
        }

        [Fact]
        public async Task 有多頁資料時摘要以中文顯示頁數與筆數()
        {
            using var client = Client();
            var html = await GetHtmlAsync(client, "/Product?searched=true", "zh-TW");

            // 種子資料的品號足以超過一頁；摘要三個數字缺一個都代表 PagerSummary 的格式被改壞了
            var m = System.Text.RegularExpressions.Regex.Match(html, @"第\s*(\d+)\s*/\s*(\d+)\s*頁，共\s*(\d+)\s*筆");
            Assert.True(m.Success, "找不到中文分頁摘要——PagerSummary 沒解析、或三個佔位符被改掉了");
            Assert.Equal("1", m.Groups[1].Value);
        }

        /// <summary>
        /// 報表走的是 <c>ReportPaging</c>（View 層切片），與清單畫面的 controller 端分頁是兩條路徑。
        /// 這裡確認它也真的把分頁列渲染出來，而不是只在原始碼裡看起來有掛。
        /// </summary>
        [Theory]
        [InlineData("/StockShortage/Index?searched=true")]
        [InlineData("/SalesRanking/Index?searched=true")]
        public async Task 報表畫面也有分頁列與每頁筆數(string url)
        {
            using var client = Client();
            var html = await GetHtmlAsync(client, url, "zh-TW");

            foreach (var key in RawKeys)
                Assert.False(html.Contains(key, StringComparison.Ordinal),
                    $"{url}：畫面上出現未翻譯的資源鍵 '{key}'");

            // 每頁筆數在查詢列上，有沒有資料都會渲染
            Assert.Contains("每頁筆數", html);

            // 分頁摘要只在「真的有結果」時才會出現——沒有資料時報表走的是「無資料」分支，
            // 整張表都不渲染。所以這裡是條件式斷言，不是無條件要求它出現。
            if (html.Contains("<tbody", StringComparison.Ordinal) && html.Contains("</tr>", StringComparison.Ordinal)
                && !html.Contains("找不到符合條件", StringComparison.Ordinal))
            {
                Assert.Matches(@"第\s*\d+\s*/\s*\d+\s*頁，共\s*\d+\s*筆", html);
            }
        }

        [Fact]
        public async Task 英文介面用英文摘要()
        {
            using var client = Client();
            var html = await GetHtmlAsync(client, "/Product?searched=true", "en-US");
            Assert.Matches(@"Page\s+\d+\s+of\s+\d+,\s+\d+\s+records", html);
            Assert.Contains("Per page", html);
        }
    }
}
