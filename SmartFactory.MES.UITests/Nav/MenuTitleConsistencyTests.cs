using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Nav
{
    /// <summary>
    /// 側邊選單上的名稱＝點進去那一頁的標題（中、英文各一輪）。
    ///
    /// <para>2026-10-05 人工驗收：選單「庫存價值分析」點進去標題是「存貨價值分析」。逐頁比對後中文 50 處、
    /// 英文 51 處不一致——同一個畫面兩個名字，使用者照著手冊或同事口頭說的名字找不到選單。
    /// 使用者裁決：<b>畫面跟選單</b>（選單是找功能的入口）。</para>
    ///
    /// <para>清單從實際渲染的選單列舉（同 <see cref="MenuWalkSmokeTests"/>），新畫面自動納入。
    /// 只比標題（<c>&lt;title&gt;</c>，即 ViewData["Title"]）：頁首與標題在各畫面多半取同一個資源鍵。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MenuTitleConsistencyTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public MenuTitleConsistencyTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        /// <summary>
        /// 暫不比對的頁面，每一筆都要有理由；理由消失就刪掉那一筆。
        /// </summary>
        private static readonly Dictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase)
        {
            // 2026-10-08：原本 6 個 Core 畫面（廠別、功能權限、手機模組權限、簽核範本、全部簽核紀錄、首頁）
            // 已由 Core #61 修正並同步（ProjectZ #227），從暫不比對清單移除。
            // 導覽列上的使用者名稱連結，不是選單項目
            ["/UserSettings"]  = "導覽列的使用者名稱連結",
        };

        private const int MinimumMenuLinks = 100;

        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public async Task 選單名稱與畫面標題一致(string culture)
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.Context.AddCookiesAsync(new[]
            {
                new Microsoft.Playwright.Cookie { Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = _fixture.BaseUrl },
            });
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                var links = await page.EvalOnSelectorAllAsync<string[][]>(".nav .nav-link[href]",
                    "els => els.map(e => [e.getAttribute('href'), e.textContent.replace(/\\s+/g, ' ').trim()])");
                var menu = links
                    .Where(l => l[0] != null && l[0].StartsWith('/') && !l[0].StartsWith("//") && !string.IsNullOrEmpty(l[1])
                             && !l[0].Contains("Logout", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(l => l[0], StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Href: g.Key, Label: g.First()[1]))
                    .ToList();
                Assert.True(menu.Count >= MinimumMenuLinks,
                    $"只從側邊選單撈到 {menu.Count} 條連結（下限 {MinimumMenuLinks}），selector 可能失效。");

                var bad = new StringBuilder();
                var compared = 0;
                foreach (var (href, label) in menu)
                {
                    if (Pending.ContainsKey(href)) continue;
                    var resp = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}{href}");
                    if (resp.Status != 200) continue;   // 打不開的頁由 MenuWalkSmokeTests 管
                    var html = await resp.TextAsync();
                    var m = Regex.Match(html, "<title>([^<]*)</title>");
                    if (!m.Success) continue;
                    var title = WebUtility.HtmlDecode(m.Groups[1].Value);
                    title = Regex.Replace(title, @"\s*-\s*SmartFactory\.MES\s*$", "").Trim();
                    compared++;
                    if (!string.Equals(title, label, StringComparison.Ordinal))
                        bad.AppendLine($"  {href}：選單「{label}」≠ 標題「{title}」");
                }
                _out.WriteLine($"{culture}：比對 {compared} 頁");
                Assert.True(compared >= MinimumMenuLinks - Pending.Count,
                    $"只比對到 {compared} 頁，比對方式可能失效。");
                Assert.True(bad.Length == 0, $"{culture} 選單名稱與畫面標題不一致：\n{bad}");
            }
            finally
            {
                // 共用 Context：切回中文，免得後面的測試吃到英文介面
                await page.Context.AddCookiesAsync(new[]
                {
                    new Microsoft.Playwright.Cookie { Name = ".AspNetCore.Culture", Value = "c=zh-TW|uic=zh-TW", Url = _fixture.BaseUrl },
                });
            }
        }
    }
}
