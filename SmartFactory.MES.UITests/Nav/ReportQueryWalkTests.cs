using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Nav
{
    /// <summary>
    /// 每一支報表／查詢頁，用畫面預設條件**按一次「查詢」**：沒有 5xx、沒有未攔截的 JS 錯誤、沒有錯誤頁。
    ///
    /// <para><b>為什麼要有這一支</b>：<see cref="MenuWalkSmokeTests"/> 只「打開」頁面——而查詢列頁面
    /// 在沒按查詢前（沒有 <c>searched=true</c>）多半不碰資料庫，真正的查詢、彙總、日期界線換算全在按下去之後。
    /// 2026-10-20 盤點時，52 支報表裡只有十來支有專屬的查詢測試，其餘按下查詢之後的路徑沒有人走過；
    /// 已經發生過的壞法（<c>DateTime.Kind</c> 對不齊欄位型別的 500、空集合 <c>Max()</c> 例外）
    /// 都是「打得開、一按就炸」。</para>
    ///
    /// <para><b>母體</b>：側邊選單 DOM 撈出的連結（同 MenuWalk，新畫面自動進來），
    /// 交集 <c>ProjectZMenuContributor.ReportActions</c> 表的選單鍵（只收報表／查詢型畫面，
    /// 該表另由 <c>ReportSupportedActionsTests</c> 守著不漏登錄）。</para>
    ///
    /// <para><b>它不驗數字</b>，只回答「按得下去、沒有炸」。各頁查到幾筆會印在輸出裡供人工對照，
    /// 不做斷言：E2E 種子不保證每支報表都有資料，「0 筆」本身不是錯。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ReportQueryWalkTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        /// <summary>
        /// 實際按到查詢的頁數下限。擋的是「一頁都沒按到」量級的失效（查詢列 selector 改了、
        /// ReportActions 表解析不到），不是精確計數。
        /// </summary>
        private const int MinimumQueried = 40;
        private const int PerPageTimeoutMs = 30_000;

        private static readonly Regex ReportKey = new(@"\[""(\w+)""\]\s*=\s*\[", RegexOptions.Compiled);

        public ReportQueryWalkTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        [Fact]
        public async Task 每支報表按下查詢都沒有錯誤()
        {
            var keys = LoadReportKeys();
            Assert.True(keys.Count >= 40, $"只從 ReportActions 表解析到 {keys.Count} 個選單鍵，解析方式可能失效。");

            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = PerPageTimeoutMs });

            var hrefs = await page.EvalOnSelectorAllAsync<string[]>(
                ".nav .nav-link[href]", "els => els.map(e => e.getAttribute('href'))");
            var links = hrefs
                .Where(h => !string.IsNullOrWhiteSpace(h) && h.StartsWith('/') && !h.StartsWith("//"))
                .Select(h => h.Trim())
                .Where(h => keys.Contains(h.Trim('/').Split('/', '?')[0]))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _out.WriteLine($"選單上的報表／查詢頁：{links.Count} 支");

            var current = "(尚未導覽)";
            var failures = new List<string>();
            page.PageError += (_, e) => failures.Add($"{current} → JS 錯誤：{FirstLine(e)}");
            page.Response += (_, r) =>
            {
                if (r.Status >= 500 && r.Url.StartsWith(_fixture.BaseUrl, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{current} → HTTP {r.Status} {r.Url[_fixture.BaseUrl.Length..]}");
            };

            var queried = 0;
            var noQueryBar = new List<string>();
            var summary = new StringBuilder();

            foreach (var link in links)
            {
                current = link;
                try
                {
                    await page.GotoAsync($"{_fixture.BaseUrl}{link}", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = PerPageTimeoutMs });

                    // 查詢列有兩種寫法：hidden searched=true（new-screen 規格），或送出鈕本身 name="searched"。
                    var form = page.Locator("form:has(input[name='searched']), form:has(button[name='searched'])").First;
                    var submit = form.Locator("button[name='searched'], button[type='submit'], input[type='submit']").First;
                    if (await form.CountAsync() == 0 || await submit.CountAsync() == 0)
                    {
                        noQueryBar.Add(link);
                        continue;
                    }

                    // 必填欄位沒有預設值時瀏覽器會擋下送出（例如日期區間、明細帳的品號）——
                    // 那是畫面的正常行為，不是錯；替它補上最平凡的值：日期取今天、下拉取第一個有值的選項。
                    var unfillable = await form.EvaluateAsync<string>(FillRequiredJs);
                    if (!string.IsNullOrEmpty(unfillable))
                    {
                        failures.Add($"{link} → 必填欄位無法自動填入：{unfillable}");
                        continue;
                    }

                    await page.RunAndWaitForNavigationAsync(() => submit.ClickAsync(),
                        new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = PerPageTimeoutMs });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); }
                    catch (TimeoutException) { /* 有輪詢的頁面等不到 NetworkIdle，不算失敗 */ }
                    queried++;

                    if (await page.Locator("form input[name='Input.Password'], #account").CountAsync() > 0)
                    {
                        failures.Add($"{link} → 查詢後被導回登入頁");
                        continue;
                    }
                    var body = await page.Locator("body").InnerTextAsync();
                    if (body.Contains("An unhandled exception", StringComparison.Ordinal)
                        || body.Contains("處理您的要求時發生錯誤", StringComparison.Ordinal)
                        || body.Contains("An error occurred while processing your request", StringComparison.Ordinal))
                    {
                        failures.Add($"{link} → 查詢後出現錯誤頁");
                        continue;
                    }

                    var rows = await page.Locator("table tbody tr").CountAsync();
                    var danger = await page.Locator(".alert-danger:visible").AllInnerTextsAsync();
                    summary.AppendLine($"  {link}  表格列 {rows}"
                        + (danger.Count > 0 ? $"  ⚠ {FirstLine(string.Join(" / ", danger))}" : ""));
                }
                catch (TimeoutException)
                {
                    failures.Add($"{link} → 查詢逾時或按下後沒有送出（>{PerPageTimeoutMs} ms）");
                }
            }

            _out.WriteLine($"按到查詢：{queried} 支");
            _out.WriteLine(summary.ToString());
            if (noQueryBar.Count > 0)
                _out.WriteLine("沒有標準查詢列（看板／即時頁，略過）：" + string.Join("、", noQueryBar));

            Assert.True(queried >= MinimumQueried,
                $"只按到 {queried} 支報表的查詢（下限 {MinimumQueried}）。查詢列 selector 可能失效。");
            Assert.True(failures.Count == 0, "報表查詢出錯：\n" + string.Join("\n", failures.Distinct()));
        }

        /// <summary>補必填欄位；回傳仍無法通過驗證的欄位名（空字串＝可以送出）。</summary>
        private const string FillRequiredJs = """
            f => {
                const today = new Date().toISOString().slice(0, 10);
                for (const el of f.querySelectorAll('[required]')) {
                    if (el.checkValidity()) continue;
                    if (el.tagName === 'SELECT') {
                        const opt = [...el.options].find(o => o.value);
                        if (opt) { el.value = opt.value; el.dispatchEvent(new Event('change', { bubbles: true })); }
                    } else if (el.type === 'date') {
                        el.value = today;
                    } else if (el.type === 'month') {
                        el.value = today.slice(0, 7);
                    }
                }
                return [...f.querySelectorAll('[required]')].filter(el => !el.checkValidity()).map(el => el.name).join(',');
            }
            """;

        private static HashSet<string> LoadReportKeys()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "seed-e2e-fixture.sql")))
                dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException($"找不到 repo 根目錄（從 {AppContext.BaseDirectory} 往上找）。");

            var src = File.ReadAllText(Path.Combine(dir.FullName, "Services", "ProjectZMenuContributor.cs"));
            var start = src.IndexOf("ReportActions =", StringComparison.Ordinal);
            var end = src.IndexOf("};", start, StringComparison.Ordinal);
            return ReportKey.Matches(src[start..end]).Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static string FirstLine(string s)
        {
            var first = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return first.Length > 200 ? first[..200] + "…" : first;
        }
    }
}
