using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.RPS
{
    /// <summary>
    /// 報表工具三頁：排程（<c>/ReportSchedule</c>）、收件匣（<c>/ReportInbox</c>）、
    /// 執行紀錄（<c>/ReportRunHistory</c>）。
    ///
    /// <para><b>缺口在哪一層</b>：這三頁的單元與整合測試這兩週補得很厚（R-4 到 R-7），
    /// 但 <c>grep -rln "ReportSchedule|ReportInbox|ReportRunHistory" SmartFactory.MES.UITests/</c>
    /// 一直是 <b>0</b>——manual-test-plan §10.2 就是這樣整批漏列的，
    /// 也是 R-1 到 R-8 的人工驗收非得業主自己點不可的原因。</para>
    ///
    /// <para><b>這裡只補瀏覽器層獨有的那一半</b>，不重複整合測試做得到的事：</para>
    /// <list type="bullet">
    ///   <item>側面板真的<b>開得起來</b>（整合測試打 <c>/ReportSchedule/Create</c> 拿到的是
    ///         partial 的 HTML，證明不了 offcanvas 有沒有展開、DOMPurify 有沒有把它吃掉）；</item>
    ///   <item>週期欄位的<b>顯示與隱藏</b>。R-5 的整合測試只能比對 class 字串，
    ///         而那份測試自己的註解就寫著「只往 id 之後找會永遠判成沒隱藏」——
    ///         <b>真正的「看得見／看不見」只有瀏覽器判得出來</b>（Playwright 依 computed style 判定，
    ///         class 帶 <c>!important</c> 也抓得到）。<c>DetailTabSmokeTests</c> 當初就是為了
    ///         同一個回歸樣態而寫的：class 隱藏 vs <c>style.display</c> 切換。</item>
    /// </list>
    ///
    /// <para>🚨 全部唯讀：不新增排程、不按「立即執行」、不下載檔案。
    /// :5110 與 ProjectZ 庫是多 session 共用的。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class ReportToolsUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public ReportToolsUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        private async Task<IPage> OpenAsync(string path, List<string>? jsErrors = null)
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            if (jsErrors is not null) page.PageError += (_, e) => jsErrors.Add(e);
            await page.GotoAsync($"{_fixture.BaseUrl}{path}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return page;
        }

        /// <summary>
        /// 三頁共同的 <c>searched=true</c> 閘門：**沒按查詢之前不倒資料出來**。
        ///
        /// <para>兩個狀態都驗。只驗「查詢後表格在」的話，閘門失效（一進頁面就把全部列倒出來）
        /// 照樣通過；而這三頁倒出來的是誰收到了哪一份含金額的報表，
        /// 那正是 §八「機密報表不得由靜態路徑直出」要擋的東西的畫面版本。</para>
        /// </summary>
        [Theory]
        [InlineData("/ReportSchedule")]
        [InlineData("/ReportInbox")]
        [InlineData("/ReportRunHistory")]
        public async Task 三頁在按查詢之前不倒資料出來_按了之後表格才在(string path)
        {
            var jsErrors = new List<string>();
            var page = await OpenAsync(path, jsErrors);

            // 查詢列一定要在——它是三頁唯一的篩選入口，不在的話使用者連查都查不了。
            Assert.True(await page.Locator("form[method='get']").CountAsync() > 0,
                $"{path} 沒有查詢列。");
            Assert.Equal(0, await page.Locator("table tbody tr").CountAsync());

            await page.GotoAsync($"{_fixture.BaseUrl}{path}?searched=true",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // 表頭與資料列分開看：表頭證明表格渲染出來了，資料列是環境有沒有資料，
            // 兩件事混在一起斷言的話，「表格壞了」與「這個環境沒資料」分不出來。
            Assert.True(await page.Locator("table thead th").CountAsync() > 0,
                $"{path}?searched=true 的表格沒有任何欄位標題。");

            Assert.Empty(jsErrors);
        }

        [Fact]
        public async Task 新增排程的側面板真的展開而且表單欄位在裡面()
        {
            var page = await OpenAsync("/ReportSchedule");

            await page.ClickAsync("[data-action='openPanel'][data-url*='/ReportSchedule/Create']");

            // 等的是「面板展開」而不是「表單欄位出現在 DOM 裡」：
            // partial 被塞進 DOM 但 offcanvas 沒展開時，欄位查得到、使用者卻什麼都看不到。
            var panel = page.Locator("#sidePanel");
            await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = 10_000 });

            foreach (var field in new[] { "#scheduleKind", "input[name='Name']", "select[name='ToolKey']" })
                await Assertions.Expect(page.Locator(field)).ToBeVisibleAsync(new() { Timeout = 5_000 });
        }

        /// <summary>
        /// 週期換成「每週」只露出星期幾、換成「每月」只露出幾號、「每天」兩個都不露。
        ///
        /// <para>三種都驗，而且**兩列的可見性各驗一次**。只驗「該出現的出現了」的話，
        /// 兩列永遠都顯示也會通過——而那正是使用者會填錯資料的方向：
        /// 每月排程的畫面上還留著「星期幾」，填了它，存進去的是一筆自相矛盾的規則。</para>
        /// </summary>
        [Theory]
        [InlineData("Daily", false, false)]
        [InlineData("Weekly", true, false)]
        [InlineData("Monthly", false, true)]
        public async Task 換週期時該露的那一列才露出來(string kind, bool weekVisible, bool monthVisible)
        {
            var page = await OpenAsync("/ReportSchedule");
            await page.ClickAsync("[data-action='openPanel'][data-url*='/ReportSchedule/Create']");
            await Assertions.Expect(page.Locator("#sidePanel")).ToBeVisibleAsync(new() { Timeout = 10_000 });

            await page.SelectOptionAsync("#scheduleKind", kind);

            var week = page.Locator("#rowDayOfWeek");
            var month = page.Locator("#rowDayOfMonth");

            if (weekVisible) await Assertions.Expect(week).ToBeVisibleAsync();
            else await Assertions.Expect(week).Not.ToBeVisibleAsync();

            if (monthVisible) await Assertions.Expect(month).ToBeVisibleAsync();
            else await Assertions.Expect(month).Not.ToBeVisibleAsync();
        }

        /// <summary>
        /// 執行紀錄的狀態徽章：成功與失敗不能長成同一個樣子。
        ///
        /// <para>R-8 的人工驗收看的就是這個（而且 R-4 那次只走到紅色那條，
        /// 黃色的 <c>SkippedNoPermission</c> 是另一段程式產生的）。
        /// 這裡用狀態篩選器各查一次，確認**篩出來的每一列**都掛著該狀態的徽章——
        /// 只看第一列的話，篩選器沒生效（把全部列都倒出來）也會通過。</para>
        /// </summary>
        [Theory]
        [InlineData("Success", "bg-success")]
        [InlineData("Failed", "bg-danger")]
        public async Task 執行紀錄依狀態篩選後_每一列的徽章都是那個狀態(string status, string badgeClass)
        {
            var page = await OpenAsync($"/ReportRunHistory?status={status}&searched=true");

            var rows = page.Locator("table tbody tr");
            var count = await rows.CountAsync();
            _out.WriteLine($"status={status} 篩出 {count} 列");

            Assert.True(count > 0,
                $"執行紀錄裡沒有任何 {status} 的列，這個狀態的徽章驗不到。"
                + "（這是資料前提不是回歸：請先在目標環境跑出一筆該狀態的執行紀錄。）");

            for (var i = 0; i < count; i++)
            {
                var badge = rows.Nth(i).Locator($"span.badge.{badgeClass}");
                Assert.True(await badge.CountAsync() > 0,
                    $"第 {i + 1} 列沒有 {badgeClass} 徽章——篩選器篩的是 {status}，"
                    + "列卻不是那個狀態，代表篩選條件沒有真的套進查詢。");
            }
        }

        /// <summary>
        /// 收件匣：有產出檔的那一列要給得出下載連結，而且連結指向那一列自己的 runId。
        ///
        /// <para>⚠️ <b>不按下去</b>——下載會真的把檔案讀出來（R-6 的整合測試已經驗過
        /// 下載時重查權限那一段）。這裡驗的是**連結對不對**：
        /// 每一列都指向同一個 runId 是會發生的（迴圈變數寫錯），
        /// 而症狀是使用者不管點哪一列都下載到同一份報表。</para>
        /// </summary>
        [Fact]
        public async Task 收件匣的下載連結各自指向自己那一列的執行編號()
        {
            var page = await OpenAsync("/ReportInbox?searched=true");

            var links = page.Locator("a[href*='/ReportInbox/Download']");
            var count = await links.CountAsync();
            _out.WriteLine($"收件匣可下載的列：{count}");

            Assert.True(count > 0,
                "收件匣裡沒有任何可下載的列（FilePath 皆為 null）。"
                + "這是資料前提：請先跑出一筆有產出檔的執行紀錄。");

            var runIds = new List<string>();
            for (var i = 0; i < count; i++)
                runIds.Add(await links.Nth(i).GetAttributeAsync("href") ?? "");

            Assert.Equal(runIds.Count, runIds.Distinct(StringComparer.Ordinal).Count());
            Assert.All(runIds, href => Assert.Contains("runId=", href, StringComparison.Ordinal));
        }
    }
}
