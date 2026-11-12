using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 隱私權政策頁：原本是 ASP.NET 範本佔位頁（英文一句 "Use this page to detail your site's
    /// privacy policy."），頁尾連結也寫死英文 "Privacy"。這裡驗的是實際渲染結果——
    /// 依語系 Cookie 出中文或英文全文、不必登入就看得到、未設定組態時退回通用寫法。
    /// </summary>
    [Collection("Integration")]
    public class PrivacyPageTests
    {
        private static readonly Regex Cjk = new(@"\p{IsCJKUnifiedIdeographs}");
        private readonly TestWebApplicationFactory _factory;

        public PrivacyPageTests(TestWebApplicationFactory factory) => _factory = factory;

        private async Task<(HttpStatusCode Status, string Html)> GetAsync(string culture)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/Privacy");
            request.Headers.Add("Cookie", $".AspNetCore.Culture=c={culture}|uic={culture}");
            var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        /// <summary>只取 &lt;main&gt; 內容，排除選單與頁尾（那些另有在地化，不是本頁的責任）。</summary>
        private static string MainOf(string html)
        {
            var m = Regex.Match(html, @"<main[^>]*>(.*?)</main>", RegexOptions.Singleline);
            Assert.True(m.Success, "頁面沒有 <main> 區塊");
            // Razor 的 @ 輸出會把中文編成 &#x...; 字元參照（瀏覽器顯示無差別），比對前先解碼。
            return WebUtility.HtmlDecode(m.Groups[1].Value);
        }

        [Fact]
        public async Task 未登入也看得到_不會被導去登入頁()
        {
            var (status, _) = await GetAsync("zh-TW");
            Assert.Equal(HttpStatusCode.OK, status);
        }

        [Fact]
        public async Task 中文語系_顯示中文全文與個資法權利()
        {
            var (_, html) = await GetAsync("zh-TW");
            var main = MainOf(html);

            Assert.Contains("<h1 class=\"h3 mb-1\">隱私權政策</h1>", main);
            Assert.Contains("個人資料保護法", main);
            Assert.Contains("操作稽核記錄", main);
            Assert.DoesNotContain("Use this page to detail", main);
            Assert.DoesNotContain("Data controller", main);
        }

        [Fact]
        public async Task 英文語系_顯示英文全文且內文不夾中文()
        {
            var (_, html) = await GetAsync("en-US");
            var main = MainOf(html);

            Assert.Contains("<h1 class=\"h3 mb-1\">Privacy Policy</h1>", main);
            Assert.Contains("Data controller", main);
            Assert.Contains("Your rights", main);
            Assert.False(Cjk.IsMatch(main), "英文版內文出現中文字：" + Cjk.Match(main).Value);
        }

        [Fact]
        public async Task 未設定組態也沒有公司名稱時_資料管理者與聯絡窗口退回通用寫法()
        {
            var (_, zh) = await GetAsync("zh-TW");
            var (_, en) = await GetAsync("en-US");

            Assert.Contains("請洽 <strong>貴公司</strong> 的系統管理員", MainOf(zh));
            Assert.Contains("contact the system administrator at <strong>your organization</strong>", MainOf(en));
        }

        [Fact]
        public async Task 系統參數設了公司名稱_資料管理者顯示該名稱而不是貴公司()
        {
            Dictionary<string, string?> saved;
            using (var scope = _factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISystemParamService>();
                var all = await svc.GetAllAsync();
                saved = new()
                {
                    [SystemParamKeys.CompanyNameZh] = all.GetValueOrDefault(SystemParamKeys.CompanyNameZh),
                    [SystemParamKeys.CompanyNameEn] = all.GetValueOrDefault(SystemParamKeys.CompanyNameEn),
                };
                await svc.SetBatchAsync(new()
                {
                    [SystemParamKeys.CompanyNameZh] = "隱私測試股份有限公司",
                    [SystemParamKeys.CompanyNameEn] = "Privacy Test Co., Ltd.",
                });
            }
            try
            {
                var (_, zh) = await GetAsync("zh-TW");
                var (_, en) = await GetAsync("en-US");

                Assert.Contains("請洽 <strong>隱私測試股份有限公司</strong> 的系統管理員", MainOf(zh));
                Assert.DoesNotContain("貴公司", MainOf(zh));
                Assert.Contains("contact the system administrator at <strong>Privacy Test Co., Ltd.</strong>", MainOf(en));
            }
            finally
            {
                using var scope = _factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISystemParamService>().SetBatchAsync(saved);
            }
        }

        [Fact]
        public async Task 頁尾的隱私權連結跟著語系()
        {
            var (_, zh) = await GetAsync("zh-TW");
            var (_, en) = await GetAsync("en-US");

            Assert.Matches(@"<footer.*?href=""/Privacy"">隱私權政策</a>", Regex.Replace(WebUtility.HtmlDecode(zh), @"\s+", " "));
            Assert.Matches(@"<footer.*?href=""/Privacy"">Privacy</a>", Regex.Replace(WebUtility.HtmlDecode(en), @"\s+", " "));
        }
    }
}
