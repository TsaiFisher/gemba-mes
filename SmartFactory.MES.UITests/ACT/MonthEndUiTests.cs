using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 月結作業畫面（<c>/MonthEnd</c>）。
    ///
    /// <para><b>為什麼先補這一支</b>：2026-09-18 盤點時它是「寫入動作最多、而且連整合測試
    /// 都沒有」的那一個——<c>MonthEndController</c> 有 <b>8 個 POST</b>，
    /// 兩層測試都是零。而它改的是帳：鎖期間、鎖工單成本、跑 NRV／外幣評價／ECL 批次。</para>
    ///
    /// <para>🚨 <b>這支測試刻意不製造任何成功的狀態變更。</b>:5110 的 app 與 ProjectZ 庫是
    /// 多 session 共用的，在這裡按下「完成步驟」或「取消月結」會真的改掉別人的資料，
    /// 而且沒有還原路徑。所以走的是三條**不留痕跡**的路：</para>
    /// <list type="number">
    ///   <item>唯讀渲染（沒選期間的提示、選了期間之後的十列步驟）；</item>
    ///   <item>「初始化」按鈕的**出現條件**（清單已存在時 View 根本不渲染它），只看不按；</item>
    ///   <item>失敗路徑用**不存在的** checklistId 觸發——伺服器在任何寫入之前就拋掉了。</item>
    /// </list>
    ///
    /// <para>因此本檔**沒有涵蓋**八個 POST 的成功路徑（完成／略過／取消／四支批次）。
    /// 那些要在自己的資料庫上跑，不能借用共用環境；缺口留在這裡寫明，比在共用庫上
    /// 跑出一次沒人預期的關帳好。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class MonthEndUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public MonthEndUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        /// <summary>月結清單的固定步驟數（<c>MonthEndService.Steps</c>）。</summary>
        private const int ExpectedStepCount = 10;

        private async Task<IPage> OpenAsync(string query = "")
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd{query}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            return page;
        }

        [Fact]
        public async Task 沒選期間時_畫面給的是提示而不是一張空表格()
        {
            var page = await OpenAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            // 期間下拉必須有東西可選。空的下拉與「這個期間沒有清單」在畫面上長得很像，
            // 但成因完全不同（前者是 AccountingPeriods 沒資料／被廠別過濾光了）。
            var options = page.Locator("select[name='periodId'] option");
            Assert.True(await options.CountAsync() > 0,
                "期間下拉一個選項都沒有。這通常是 AccountingPeriods 在目前廠別下查不到資料，"
                + "不是月結畫面本身的問題——但使用者看到的是一個按了沒反應的查詢列。");

            // 沒選期間時不該出現步驟表格：出現了就代表它拿了某個「預設期間」的清單，
            // 而使用者並不知道自己在看哪一期。
            Assert.Equal(0, await page.Locator("table tbody tr").CountAsync());

            Assert.Empty(jsErrors);
        }

        [Fact]
        public async Task 選了期間之後_十個步驟一列不少地渲染出來()
        {
            var (page, periodId) = await OpenFirstPeriodWithChecklistAsync();
            _out.WriteLine($"用來驗證渲染的期間：{periodId}");

            var rows = page.Locator("table tbody tr");
            var count = await rows.CountAsync();

            // 逐列取步驟號再比對集合，而不是只數列數：少了第 7 步、卻多渲染一列別的東西，
            // 數量一樣會通過。步驟號是這張表唯一能證明「十個步驟都在」的欄位。
            var stepNos = new List<string>();
            for (var i = 0; i < count; i++)
                stepNos.Add((await rows.Nth(i).Locator("td").First.InnerTextAsync()).Trim());

            Assert.Equal(
                Enumerable.Range(1, ExpectedStepCount).Select(n => n.ToString()).ToArray(),
                stepNos.ToArray());
        }

        /// <summary>
        /// 「初始化清單」按鈕只在**這一期還沒有清單**時出現（View 的條件是
        /// <c>canUpdate &amp;&amp; periodId 有值 &amp;&amp; Model == null</c>）。
        ///
        /// <para>兩個方向都驗。只驗「沒清單時按鈕在」的話，按鈕變成永遠都在也照樣通過，
        /// 而那是這個閘門唯一會壞的方向——已經有清單還給人按下去。</para>
        ///
        /// <para>ⓘ 這支測試**不按**那顆按鈕。按下去會在多 session 共用的資料庫裡
        /// 新增一份清單與十列步驟，而且沒有還原路徑。</para>
        /// </summary>
        [Fact]
        public async Task 初始化按鈕只在這一期還沒有清單時才出現()
        {
            var (page, withChecklist) = await OpenFirstPeriodWithChecklistAsync();
            Assert.Equal(0, await page.Locator("[data-action='initChecklist']").CountAsync());

            var without = await FirstPeriodWithoutChecklistAsync(page);
            _out.WriteLine($"有清單：{withChecklist}；沒有清單：{without}");

            Assert.Equal(1, await page.Locator("[data-action='initChecklist']").CountAsync());

            // 按鈕得帶著自己那一期的代碼才按得出東西來——沒帶的話點下去會送出
            // /MonthEnd/Init?periodId=（空字串），而那是個沒有訊號的失敗。
            Assert.Equal(without, await page.Locator("[data-action='initChecklist']")
                .GetAttributeAsync("data-period-id"));
        }

        [Fact]
        public async Task 動作失敗時_紅色toast要出現而且訊息說得出哪裡不對()
        {
            var (page, _) = await OpenFirstPeriodWithChecklistAsync();

            var button = page.Locator("[data-action='completeStep']").First;
            Assert.True(await button.CountAsync() > 0,
                "清單上沒有任何『完成步驟』按鈕可按——可能十個步驟都已完成／略過，"
                + "或目前角色沒有 MonthEnd 的更新權限（沒有權限時整個 Actions 欄不會渲染）。");

            // 把按鈕指向一個不存在的清單。伺服器會在任何寫入之前就拋掉
            // （MonthEndService.CompleteStepAsync 第一件事就是找那一列步驟），
            // 所以這條路不會動到共用資料庫的任何一列。
            // 這種「改掉畫面上的值再送出」的手法與 CrudSmokeTestBase 驗
            // 「拔掉 maxlength 之後伺服器仍須拒絕」是同一個用意：驗的是伺服器，不是瀏覽器。
            await button.EvaluateAsync(
                "el => el.dataset.checklistId = 'E2E-NO-SUCH-CHECKLIST'");
            await button.ClickAsync();

            var error = page.Locator("#errorToast");
            await Assertions.Expect(error).ToBeVisibleAsync(new() { Timeout = 10_000 });

            // 成功 toast 不可以同時出現——出現了代表失敗的那一支走了成功分支。
            await Assertions.Expect(page.Locator("#successToast")).Not.ToBeVisibleAsync();

            var message = (await page.Locator("#errorMsg").InnerTextAsync()).Trim();
            _out.WriteLine($"失敗訊息：{message}");

            Assert.False(string.IsNullOrWhiteSpace(message),
                "失敗了，但紅色 toast 裡是空的——使用者只看到一條紅色的東西閃過去。");

            // 訊息要說得出哪裡不對，不是把 .NET 的型別名與堆疊倒給使用者看。
            // 這條與 manual-test-plan 的 R-4 是同一個要求（那一項的人工驗收就是在看這個）。
            foreach (var leak in new[] { "Exception", "   at ", "System.", "StackTrace" })
                Assert.False(message.Contains(leak, StringComparison.Ordinal),
                    $"失敗訊息裡出現了「{leak}」，這是把內部細節倒給使用者：{message}");
        }

        // ── 共用 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 逐一試期間下拉的選項，停在**第一個真的渲染得出步驟表格**的期間。
        ///
        /// <para>不寫死某個期間代碼：那會綁死在某一份資料上，換一個環境就紅，
        /// 而紅的理由（「這期沒有清單」）與真正的回歸（「表格壞了」）長得一模一樣。
        /// 一個都找不到時給的是可行動的訊息，不是 <c>NullReferenceException</c>。</para>
        /// </summary>
        private async Task<(IPage page, string periodId)> OpenFirstPeriodWithChecklistAsync()
        {
            var page = await OpenAsync();
            var values = await PeriodOptionsAsync(page);

            foreach (var periodId in values)
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd?periodId={periodId}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                if (await page.Locator("table tbody tr").CountAsync() > 0)
                    return (page, periodId);
            }

            Assert.Fail(
                $"試過 {values.Length} 個期間，沒有任何一個有已初始化的月結清單，"
                + "所以渲染與動作都驗不到。請在目標環境先對其中一期按下「初始化清單」。"
                + "（這支測試刻意不自己去初始化：那會在多 session 共用的資料庫留下新資料。）");
            return default; // Assert.Fail 已拋出，走不到這裡。
        }

        /// <summary>停在第一個**沒有**清單的期間，回傳那個期間代碼。</summary>
        private async Task<string> FirstPeriodWithoutChecklistAsync(IPage page)
        {
            foreach (var periodId in await PeriodOptionsAsync(page))
            {
                await page.GotoAsync($"{_fixture.BaseUrl}/MonthEnd?periodId={periodId}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                if (await page.Locator("table tbody tr").CountAsync() == 0)
                    return periodId;
            }

            Assert.Fail("每一個期間都已經有月結清單了，驗不到「還沒有清單」那一半。");
            return string.Empty; // Assert.Fail 已拋出。
        }

        private static async Task<string[]> PeriodOptionsAsync(IPage page) =>
            await page.EvalOnSelectorAllAsync<string[]>(
                "select[name='periodId'] option",
                "els => els.map(e => e.value).filter(v => v)");
    }
}
