using System.Text;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.Nav
{
    /// <summary>
    /// 側邊選單上的**每一頁**都打得開，而且沒有未攔截的 JS 錯誤。
    ///
    /// <para><b>為什麼要有這一支</b>：2026-09-18 逐支比對 Controller 與 UITests，
    /// 152 支裡有 86 支**完全沒有任何 Playwright 覆蓋**，其中 80 支掛在選單上——
    /// 也就是使用者點得到、但沒有任何測試點過。那批多半是唯讀報表頁，
    /// 一支一支寫 CRUD smoke 不划算，可是它們壞掉的形狀很固定，而且已經發生過兩次：</para>
    /// <list type="bullet">
    ///   <item><c>Utilization</c>／<c>MachineDowntime</c> 直接 500（<c>DateTime.Kind</c>
    ///         沒對齊欄位型別）——兩支都在那 86 支裡。</item>
    ///   <item>甘特圖色階圖例缺 <c>domain()</c> 的 TypeError，被 <c>.catch(() =&gt; {})</c>
    ///         吞掉，圖照畫得出來，不看 <c>PageError</c> 根本不會發現。</item>
    /// </list>
    ///
    /// <para><b>清單從 DOM 列舉，不寫死</b>：側邊選單渲染的是
    /// <c>&lt;a class="nav-link" href="/Controller/Action"&gt;</c>
    /// （<c>Views/Shared/Components/SidebarMenu/Default.cshtml</c>），
    /// 所以新畫面上線會自動進這支測試的母體。手抄一份 150 條的清單只會過期，
    /// 而過期的方向正好是「新畫面沒被測到」——那是這支測試要擋的東西本身。</para>
    ///
    /// <para><b>它不取代逐頁的功能測試</b>。這裡只回答「打得開嗎、有沒有炸」，
    /// 答不了「數字對不對」。把它當成那 80 頁的地板，不是天花板。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MenuWalkSmokeTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public MenuWalkSmokeTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        /// <summary>
        /// 選單至少要有這麼多條連結，否則判定為「選單根本沒渲染出來」。
        ///
        /// <para>🚨 <b>這道下限是本測試最重要的一行。</b>走訪清單是從 DOM 撈的，
        /// selector 一旦失效就會撈到空清單，而空清單走訪起來**永遠是綠的**——
        /// 「每一頁都沒問題」與「一頁都沒走到」長得一模一樣。
        /// 現況 143 支 Controller 掛在選單上，取 100 當下限：
        /// 它擋的是「掉到 0」這種量級的失效，不是精確計數（權限、開關都會讓數字浮動）。</para>
        /// </summary>
        private const int MinimumMenuLinks = 100;

        /// <summary>單頁的導覽上限。報表頁會真的查資料庫，給寬一點；逾時本身算一種失敗。</summary>
        private const int PerPageTimeoutMs = 30_000;

        [Fact]
        public async Task 選單上的每一頁都打得開而且沒有未攔截的JS錯誤()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);

            // ⚠️ SignInAsync 在 fixture 已完成 bootstrap 時**直接返回**（cookie 已在共用
            // Context 上），分頁因此還停在 about:blank——不自己導一次首頁的話，
            // 選單一條都撈不到。第一次跑就是這樣：撈到 0 條、被下限守門擋下。
            await page.GotoAsync($"{_fixture.BaseUrl}/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = PerPageTimeoutMs,
            });

            var links = await CollectMenuLinksAsync(page);

            _out.WriteLine($"選單連結（去重後）：{links.Count} 條");
            Assert.True(links.Count >= MinimumMenuLinks,
                $"只從側邊選單撈到 {links.Count} 條連結（下限 {MinimumMenuLinks}）。"
                + "這通常不是選單變少了，而是 selector 失效或使用者沒有任何選單權限——"
                + "不擋下來的話，走訪 0 頁會以『每一頁都沒問題』的樣子通過。"
                + "請確認 Views/Shared/Components/SidebarMenu/Default.cshtml 的 .nav-link 結構。");

            var current = "(尚未導覽)";
            var jsErrors = new List<string>();
            var consoleErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add($"{current} → {Collapse(e)}");
            page.Console += (_, m) =>
            {
                if (m.Type == "error") consoleErrors.Add($"{current} → {Collapse(m.Text)}");
            };

            var badStatus = new List<string>();

            foreach (var link in links)
            {
                current = link;
                var url = $"{_fixture.BaseUrl}{link}";

                try
                {
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = PerPageTimeoutMs,
                    });

                    // 沒有 response 的情況是導覽被取消（例如同頁 hash 跳轉），不是失敗。
                    if (response is not null && response.Status >= 400)
                        badStatus.Add($"{link} → HTTP {response.Status}");
                }
                catch (TimeoutException)
                {
                    badStatus.Add($"{link} → 導覽逾時（>{PerPageTimeoutMs} ms）");
                    continue;
                }

                // 讓頁面的 JS 真的跑起來——只等 DOMContentLoaded 的話，
                // 圖表初始化那一類的錯誤還來不及發生就走到下一頁了。
                // NetworkIdle 在有輪詢的頁面上永遠等不到，所以逾時**不算失敗**：
                // 這裡等的是「給 JS 一點時間」，不是「網路真的靜下來」。
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                        new PageWaitForLoadStateOptions { Timeout = 5_000 });
                }
                catch (TimeoutException) { /* 見上方註解，刻意吞掉 */ }
            }

            // console error 的判定分三層，理由是**量過的**，不是「怕有雜訊」：
            // 2026-09-18 首跑 145 頁得到 8 則，全部是 CSP 擋掉 inline style，來源兩類——
            //   ① 6 則是我們自己的 Modules/RPS/Views/Shared/_SavedQueries.cshtml
            //      （style="min-width: 18rem;"，雜湊 sha256-YIjOYk2B… 逐字對得上）；
            //   ② 2 則來自 /Schedule 的第三方圖表套件（其中一則的雜湊是**空字串**的雜湊，
            //      也就是它自己生出 style=""），那是我們改不到的程式碼。
            // ①已於同日修掉（11 處全部外部化）並由**慣例規則 R38** 靜態守門。
            //
            // 2026-09-24 起 CSP 違規改為**斷言**，只豁免②：
            //   • R38 只掃 Razor 裡寫死的 style 屬性。script-src 被擋（inline handler、漏 nonce）
            //     與 JS 執行期才生出來的 style 屬性，靜態掃描都看不到——而前者的症狀是
            //     「按了沒反應」，比樣式跑掉嚴重得多。那兩類之前只會被印出來，沒有人看。
            //   • ②以「頁面 + style-src」豁免，不綁雜湊：套件生出的 style 內容隨資料變，
            //     綁雜湊會隨資料忽紅忽綠。代價是 /Schedule 自己的 inline style 在這裡看不到，
            //     那一塊由 R38 守。
            //   • 非 CSP 的 console error 仍只印不斷言（資源載入失敗、第三方警告混雜，
            //     尚未量過能否穩定歸零）。
            var cspViolations = consoleErrors
                .Where(e => e.Contains("Content Security Policy", StringComparison.Ordinal))
                .Where(e => !IsKnownThirdPartyCspViolation(e))
                .Distinct()
                .ToList();

            if (consoleErrors.Count > 0)
            {
                _out.WriteLine($"── console error {consoleErrors.Count} 則（CSP 違規以外僅供參考）──");
                foreach (var e in consoleErrors.Distinct().Take(40)) _out.WriteLine("  " + e);
            }

            var report = new StringBuilder();
            if (badStatus.Count > 0)
            {
                report.AppendLine($"以下 {badStatus.Count} 頁打不開：");
                foreach (var b in badStatus) report.AppendLine("  " + b);
            }
            if (jsErrors.Count > 0)
            {
                report.AppendLine($"以下 {jsErrors.Count} 頁有未攔截的 JS 錯誤：");
                foreach (var e in jsErrors.Distinct()) report.AppendLine("  " + e);
            }

            if (cspViolations.Count > 0)
            {
                report.AppendLine($"以下 {cspViolations.Count} 則 CSP 違規（被瀏覽器擋掉、畫面不會報錯）：");
                foreach (var e in cspViolations) report.AppendLine("  " + e);
            }

            Assert.True(report.Length == 0,
                $"走訪選單 {links.Count} 頁，結果：\n{report}");
        }

        /// <summary>
        /// 從側邊選單撈出所有實際指向某一頁的連結。
        ///
        /// <para>群組標題的 <c>&lt;a&gt;</c> 是 <c>href="#"</c> 的 collapse 開關，要濾掉；
        /// 收合狀態下子項目**仍然在 DOM 裡**（Bootstrap 的 collapse 只是加 class），
        /// 所以不需要先把每一組點開。</para>
        /// </summary>
        private static async Task<List<string>> CollectMenuLinksAsync(IPage page)
        {
            var hrefs = await page.EvalOnSelectorAllAsync<string[]>(
                ".nav .nav-link[href]", "els => els.map(e => e.getAttribute('href'))");

            return hrefs
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h!.Trim())
                // 只走站內的絕對路徑。"#"（群組開關）、"javascript:"、外站連結都不是頁面。
                .Where(h => h.StartsWith('/') && !h.StartsWith("//"))
                // 登出是 POST 觸發的連結，走訪它會把共用 Context 的 cookie 清掉，
                // 後面每一頁都會被重導到登入頁——症狀是「剩下的頁全部失敗」。
                .Where(h => !h.Contains("Logout", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 已知且改不到的 CSP 違規：/Schedule 的第三方圖表套件自己生出 inline style
        /// （見走訪結尾的註解②）。只豁免 style-src——同一頁若出現 script-src 違規照樣擋。
        /// </summary>
        private static bool IsKnownThirdPartyCspViolation(string entry)
            => entry.StartsWith("/Schedule →", StringComparison.OrdinalIgnoreCase)
            && entry.Contains("inline style", StringComparison.Ordinal)
            && entry.Contains("'style-src", StringComparison.Ordinal);

        /// <summary>把多行訊息壓成一行，失敗報告才讀得下去（堆疊的第一行就夠定位）。</summary>
        private static string Collapse(string message)
        {
            var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return first.Length > 200 ? first[..200] + "…" : first.Trim();
        }
    }
}
