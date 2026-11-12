using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Nav
{
    /// <summary>
    /// 每一張列印頁都打得開、沒有 JS／CSP 錯誤、也沒有把資源鍵原樣印出來——中英兩種語系各走一遍。
    ///
    /// <para><b>為什麼要有這一支</b>：列印頁是另開分頁（<c>target="_blank"</c>）的獨立版面，
    /// 不在側邊選單上，<see cref="MenuWalkSmokeTests"/> 走不到；而它們改得最勤也最少人回頭看——
    /// 2026-10-14 那一批改的就是借出／樣品／報廢單的列印頁（部門與對象顯示名稱、新增三個在地化鍵）。
    /// IViewLocalizer 找不到鍵時不會報錯，而是把<b>鍵名本身</b>印出來（例如 <c>EntityType_Department</c>），
    /// 紙本上才看得到——這支專抓這個。</para>
    ///
    /// <para><b>清單從 repo 列舉</b>：<c>Modules/*/Views/*/Print.cshtml</c>，到各自的列表頁找第一個列印連結。
    /// 列表上沒有資料（或沒有列印鈕）的記為「略過」並印出來；走到的頁數有下限，
    /// 撈不到連結時不會以「每一頁都沒問題」的樣子通過。</para>
    ///
    /// <para><b>資源鍵外洩的判定</b>：讀該 View 的 resx 與兩個共用資源檔的鍵，只看「值與鍵不同」的鍵
    /// （值等於鍵的，例如英文的 Status，印出來本來就長那樣）；鍵名須是無空白的駝峰／底線識別字，
    /// 以整字比對頁面文字。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class PrintPageWalkTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        /// <summary>
        /// 實際打開的列印頁下限，擋「一頁都沒走到」量級的失效（selector 失效、列表查詢參數改名），不是精確計數。
        ///
        /// <para>取 3 的理由：E2E 種子本身保證存在的可印單據只有工單、進貨、期初調整、請購四種；
        /// 其餘單據要等各 smoke 測試建過單才有。乾淨部署上本測試若排在前面，就只走得到這幾張——
        /// 2026-10-15 在跑過整套的庫上是中英各 17 張單據列印頁（另 8 個報表型列印頁列表上沒有單筆列印連結）。</para>
        /// </summary>
        private const int MinimumPrintedPages = 3;

        public PrintPageWalkTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        private static readonly Regex KeyLikeToken = new(@"\b[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*|_[A-Za-z0-9]+)+\b", RegexOptions.Compiled);

        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public async Task 每一張列印頁都打得開_沒有JS錯誤_沒有印出資源鍵(string culture)
        {
            var root = RepoRoot();
            var printViews = Directory.EnumerateFiles(Path.Combine(root, "Modules"), "Print.cshtml", SearchOption.AllDirectories)
                .Select(p => (Module: Directory.GetParent(p)!.Parent!.Parent!.Name, Controller: Directory.GetParent(p)!.Name))
                .OrderBy(v => v.Module).ThenBy(v => v.Controller)
                .ToList();
            Assert.True(printViews.Count >= 20, $"只列舉到 {printViews.Count} 個 Print.cshtml，列舉方式可能失效。");

            var sharedKeys = LoadKeys(root, culture,
                "Resources/ProjectZSharedResource", "Resources/SharedResource");

            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.Context.AddCookiesAsync(new[]
            {
                new Cookie { Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = _fixture.BaseUrl },
            });

            var current = "(尚未導覽)";
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add($"{current} → {e}");
            page.Console += (_, m) =>
            {
                if (m.Type == "error" && m.Text.Contains("Content Security Policy", StringComparison.Ordinal))
                    jsErrors.Add($"{current} → {m.Text}");
            };

            var printed = new List<string>();
            var skipped = new List<string>();
            var failures = new StringBuilder();
            try
            {
                foreach (var (module, controller) in printViews)
                {
                    current = $"/{controller}";
                    var list = await page.GotoAsync($"{_fixture.BaseUrl}/{controller}?searched=true&pageSize=20",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    if (list is null || list.Status >= 400)
                    {
                        skipped.Add($"{controller}（列表 HTTP {list?.Status}）");
                        continue;
                    }

                    var href = await page.EvaluateAsync<string?>(@"c => {
                        const re = new RegExp('/' + c + '/Print', 'i');
                        const el = [...document.querySelectorAll('a[href], [data-url]')]
                            .find(e => re.test(e.getAttribute('href') || e.getAttribute('data-url') || ''));
                        return el ? (el.getAttribute('href') || el.getAttribute('data-url')) : null;
                    }", controller);
                    if (href is null)
                    {
                        skipped.Add(controller);
                        continue;
                    }

                    current = href;
                    var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{_fixture.BaseUrl}{href}";
                    var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    if (response is not null && response.Status >= 400)
                    {
                        failures.AppendLine($"  {href} → HTTP {response.Status}");
                        continue;
                    }
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5_000 }); }
                    catch (TimeoutException) { /* 只是給 JS 一點時間，見 MenuWalkSmokeTests */ }
                    printed.Add(href);

                    var viewKeys = LoadKeys(root, culture, $"Resources/Modules/{module}/Views/{controller}/Print");
                    // 畫面上用到、但 resx 根本沒有的鍵才是最典型的缺翻譯——只讀 resx 會漏掉它們
                    viewKeys.UnionWith(ReferencedKeys(root, module, controller)
                        .Where(k => !TranslatesToItself(root, culture, module, controller, k)));
                    var text = await page.InnerTextAsync("body");
                    var leaked = KeyLikeToken.Matches(text).Select(m => m.Value).Distinct()
                        .Where(t => viewKeys.Contains(t) || sharedKeys.Contains(t))
                        .ToList();
                    if (leaked.Count > 0)
                        failures.AppendLine($"  {href} → 印出資源鍵：{string.Join(", ", leaked)}");
                }
            }
            finally
            {
                await page.Context.AddCookiesAsync(new[]
                {
                    new Cookie { Name = ".AspNetCore.Culture", Value = "c=zh-TW|uic=zh-TW", Url = _fixture.BaseUrl },
                });
            }

            _out.WriteLine($"[{culture}] 走到 {printed.Count} 張列印頁，略過 {skipped.Count} 個（列表沒有可印的單據）：{string.Join("、", skipped)}");
            foreach (var p in printed) _out.WriteLine($"  ✓ {p}");

            Assert.True(printed.Count >= MinimumPrintedPages,
                $"[{culture}] 只打開 {printed.Count} 張列印頁（下限 {MinimumPrintedPages}）。略過：{string.Join("、", skipped)}。"
                + "多半是列表的列印連結 selector 或查詢參數變了——不擋下來的話，走 0 頁也會是綠的。");
            Assert.True(failures.Length == 0, $"[{culture}] 列印頁有問題：\n{failures}");
            Assert.True(jsErrors.Count == 0, $"[{culture}] 列印頁出現 JS／CSP 錯誤：\n{string.Join("\n", jsErrors.Distinct())}");
        }

        /// <summary>
        /// 讀 resx（{basePath}.{culture}.resx），回傳「值與鍵不同」且長得像識別字的鍵。
        /// 找不到檔案時回傳空集合（不是每個 View 都有自己的資源檔）。
        /// </summary>
        private static HashSet<string> LoadKeys(string root, string culture, params string[] basePaths)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var basePath in basePaths)
            {
                var file = Path.Combine(root, $"{basePath}.{culture}.resx");
                if (!File.Exists(file)) continue;
                foreach (var data in XDocument.Load(file).Root!.Elements("data"))
                {
                    var key = (string?)data.Attribute("name");
                    var value = (string?)data.Element("value");
                    if (key is null || value is null || key == value.Trim()) continue;
                    if (KeyLikeToken.IsMatch(key) && KeyLikeToken.Match(key).Value == key) keys.Add(key);
                }
            }
            return keys;
        }

        private static readonly Regex LocalizerCall = new(@"\b(?:Localizer|L|SL)\[""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

        /// <summary>Print.cshtml 裡 <c>Localizer["…"]</c>／<c>L["…"]</c> 用到、長得像識別字的鍵。</summary>
        private static IEnumerable<string> ReferencedKeys(string root, string module, string controller)
        {
            var view = Path.Combine(root, "Modules", module, "Views", controller, "Print.cshtml");
            return LocalizerCall.Matches(File.ReadAllText(view)).Select(m => m.Groups[1].Value)
                .Where(k => KeyLikeToken.Match(k).Value == k).Distinct();
        }

        /// <summary>任一資源檔把這個鍵翻成跟鍵一模一樣的字（例如英文的 Status）——印出來本來就長那樣。</summary>
        private static bool TranslatesToItself(string root, string culture, string module, string controller, string key)
        {
            foreach (var basePath in new[] { $"Resources/Modules/{module}/Views/{controller}/Print",
                                              "Resources/ProjectZSharedResource", "Resources/SharedResource" })
            {
                var file = Path.Combine(root, $"{basePath}.{culture}.resx");
                if (!File.Exists(file)) continue;
                var value = XDocument.Load(file).Root!.Elements("data")
                    .FirstOrDefault(d => (string?)d.Attribute("name") == key)?.Element("value")?.Value;
                if (value is not null && value.Trim() == key) return true;
            }
            return false;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "seed-e2e-fixture.sql")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException(
                $"找不到 repo 根目錄（從 {AppContext.BaseDirectory} 往上找 scripts/seed-e2e-fixture.sql）。");
        }
    }
}
