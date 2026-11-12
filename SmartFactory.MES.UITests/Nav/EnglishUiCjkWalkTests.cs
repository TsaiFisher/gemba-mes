using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Nav
{
    /// <summary>
    /// 英文介面下，選單上每一頁的「介面文字」都不得出現中文。
    ///
    /// <para><b>為什麼要有這一支</b>：寫死中文的靜態守門（HardcodedCjkLiteralGuardTests）掃的是 C#／Razor／JS 原始碼，
    /// 看不到三種執行期才出現的漏網：en-US.resx 的值其實是中文（複製 zh-TW 檔忘了翻）、
    /// 資源檔缺鍵而退回中性語系、伺服端組字串時帶進中文。這些只有真的用英文打開頁面才看得到。</para>
    ///
    /// <para><b>只看介面，不看資料</b>：品名、倉庫名、往來對象名本來就可能是中文（E2E 種子就是），
    /// 所以只檢查標題、標籤、表頭、按鈕、提示框、說明文字與 placeholder／title／aria-label，
    /// 並排除下拉選項、表格內容、select2 與語言切換（「繁體中文」是刻意的）。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class EnglishUiCjkWalkTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;
        private const int MinimumMenuLinks = 100;

        /// <summary>
        /// 刻意是中文的介面文字（頁面 → 文字片段），每一條都要寫理由。
        /// </summary>
        private static readonly Dictionary<string, (string Text, string Why)[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/SystemParam"] = new[]
            {
                ("台灣智慧工廠股份有限公司", "「公司名稱（中文）」欄位的 placeholder，範例本來就該是中文"),
                ("智慧工廠", "「公司簡稱（中文）」欄位的 placeholder，同上"),
            },
        };

        /// <summary>版面上永遠在的、刻意中文的字：語言切換鈕把中文選項寫成「繁體中文」。</summary>
        private static readonly string[] AlwaysAllowed = { "繁體中文" };

        public EnglishUiCjkWalkTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        private const string CollectCjkChrome = @"() => {
            const re = /[㐀-鿿豈-﫿]/;
            const skip = el => el.closest('select, option, table tbody, .select2-container, [data-lang-switch], .lang-switch, .dropdown-menu-lang');
            const out = new Set();
            document.querySelectorAll('h1,h2,h3,h4,h5,h6,label,th,button,.btn,.nav-link,.alert,.form-text,.card-header,legend,.badge,.page-title')
                .forEach(el => {
                    if (skip(el)) return;
                    const t = (el.innerText || '').replace(/\s+/g, ' ').trim();
                    if (t && re.test(t)) out.add(el.tagName.toLowerCase() + '｜' + t.slice(0, 60));
                });
            document.querySelectorAll('[placeholder],[title],[aria-label],[data-placeholder]').forEach(el => {
                if (skip(el)) return;
                for (const a of ['placeholder', 'title', 'aria-label', 'data-placeholder']) {
                    const v = el.getAttribute(a);
                    if (v && re.test(v)) out.add('@' + a + '｜' + v.slice(0, 60));
                }
            });
            return [...out];
        }";

        [Fact]
        public async Task 英文介面_選單每一頁的介面文字都沒有中文()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.Context.AddCookiesAsync(new[]
            {
                new Cookie { Name = ".AspNetCore.Culture", Value = "c=en-US|uic=en-US", Url = _fixture.BaseUrl },
            });

            // 廠別名稱：英文介面有英文名就該用英文名（PlantDisplay／_LoginPartial 的規則），沒有英文名才退回中文——
            // 只把「沒有英文名的廠」的中文名當資料剔除；有英文名卻印出中文名，照樣算缺陷（2026-10-15 抓到四個畫面）。
            var plantNames = new List<string>();
            foreach (var id in new[] { "P1", "E2E-P2" })
            {
                var n = await SeedFixture.ScalarAsync<string>(
                    $"SELECT \"Name\" FROM \"Plants\" WHERE \"Id\" = '{id}' AND COALESCE(TRIM(\"NameEn\"), '') = ''");
                if (!string.IsNullOrEmpty(n)) plantNames.Add(n);
            }
            var cjk = new System.Text.RegularExpressions.Regex(@"[\u3400-\u9fff\uf900-\ufaff]");

            var findings = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            try
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                var links = (await page.EvalOnSelectorAllAsync<string[]>(".nav .nav-link[href]", "els => els.map(e => e.getAttribute('href'))"))
                    .Where(h => h != null && h.StartsWith('/') && !h.StartsWith("//") && !h.Contains("Logout", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Assert.True(links.Count >= MinimumMenuLinks,
                    $"只從側邊選單撈到 {links.Count} 條連結（下限 {MinimumMenuLinks}），selector 可能失效——走 0 頁也會是綠的。");

                foreach (var link in links)
                {
                    try
                    {
                        await page.GotoAsync($"{_fixture.BaseUrl}{link}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3_000 }); }
                        catch (TimeoutException) { /* 輪詢頁面等不到 NetworkIdle，見 MenuWalkSmokeTests */ }
                    }
                    catch (TimeoutException) { continue; }   // 打不打得開由 MenuWalkSmokeTests 負責

                    var hits = (await page.EvaluateAsync<string[]>(CollectCjkChrome))
                        .Where(h =>
                        {
                            var t = h;
                            foreach (var a in AlwaysAllowed.Concat(plantNames)) t = t.Replace(a, "");
                            if (Allowed.TryGetValue(link, out var allow))
                                foreach (var (text, _) in allow) t = t.Replace(text, "");
                            return cjk.IsMatch(t[(t.IndexOf('｜') + 1)..]);
                        })
                        .ToList();
                    if (hits.Count > 0) findings[link] = hits;
                }
            }
            finally
            {
                await page.Context.AddCookiesAsync(new[]
                {
                    new Cookie { Name = ".AspNetCore.Culture", Value = "c=zh-TW|uic=zh-TW", Url = _fixture.BaseUrl },
                });
            }

            foreach (var (link, hits) in findings)
                _out.WriteLine($"{link}\n  " + string.Join("\n  ", hits));

            Assert.True(findings.Count == 0,
                $"英文介面下有 {findings.Count} 頁的介面文字仍是中文：\n"
                + string.Join("\n", findings.Select(f => $"  {f.Key}：{string.Join(" ／ ", f.Value.Take(5))}")));
        }
    }
}
