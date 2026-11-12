using System.Text.Json;
using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.ACT
{
    /// <summary>
    /// 手動傳票（JournalEntry/Create）面板動作守門。
    ///
    /// 手動傳票是總帳唯一一個由人直接寫分錄的入口，之前沒有任何 Playwright 覆蓋。
    /// 新增走 <c>_CreateForm</c>、重開走唯讀 <c>_Form</c>（兩個不同的 View，且後者沒有 form 元素），
    /// 正是 <see cref="DocActionSmokeTestBase"/> 要守的縫。
    ///
    /// <para><b>前提</b>：乾淨庫的科目全是系統科目、一律不可手動選用，
    /// 故由 seed-e2e-fixture.sql ⑱ 補兩個 E2E 科目。</para>
    ///
    /// <para><b>為什麼要從存檔回應取單號</b>：列表只依分錄日期排序，同一天的傳票先後不固定，
    /// 而同一天必定還有前幾支測試與流程測試產生的傳票。沿用「開第一列」的話，
    /// 存檔失敗也會因為打開別張而假綠。</para>
    /// </summary>
    public class JournalEntryActionSmokeTests : DocActionSmokeTestBase
    {
        public JournalEntryActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private const string DebitAccount  = "E2E-6101";
        private const string CreditAccount = "E2E-1101";

        private Task<string>? _saveBody;
        private string _journalId = "";

        protected override string IndexPath          => "/JournalEntry";
        protected override string NewButtonSelector  => "button[data-url$='/JournalEntry/Create']";
        protected override string PanelReadySelector => "#manualJournalForm";
        protected override string ReopenedPanelReadySelector => "code";

        // 傳票過帳即定案，重開為唯讀、刻意沒有動作鈕（沖銷另走調整分錄）。
        protected override bool ReopenedDocHasActions => false;
        protected override string ReadonlyPanelSelector => $"{PanelBodySel} code";

        protected override string ReopenIndexQuery => "searchDocType=Manual";

        // 存檔沒成立時 _journalId 為空；給一個不可能命中的片段，讓 base 以「列表沒有可開啟的單據」
        // 失敗並附上面板驗證訊息，而不是退回去開隨便一張。
        protected override IReadOnlyList<string> OpenUrlFragments => new[]
        {
            string.IsNullOrEmpty(_journalId) ? "__journal_not_saved__" : $"/JournalEntry/Details/{_journalId}",
        };

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            _journalId = "";
            _saveBody = ReadSaveBodyAsync(page);

            await page.FillAsync($"{PanelBodySel} input[name='Description']", $"E2E 手動傳票 {uniqueSuffix}");

            await page.Locator($"{PanelBodySel} select[name='Lines[0].AccountCode']")
                      .SelectOptionAsync(DebitAccount);
            await page.FillAsync($"{PanelBodySel} input[name='Lines[0].Debit']", "123");

            await page.Locator($"{PanelBodySel} select[name='Lines[1].AccountCode']")
                      .SelectOptionAsync(CreditAccount);
            await page.FillAsync($"{PanelBodySel} input[name='Lines[1].Credit']", "123");
        }

        /// <summary>
        /// 回應一到就讀 body。存檔成功後 refreshList 在 800ms 後整頁重新整理，瀏覽器隨即丟掉
        /// 上一頁的回應內容；等 base 存檔後的固定等待（1.5 秒）結束才讀，必得
        /// 「No resource with given identifier」——存檔**成功**反而讀不到（9/30 全套 5 支同時紅）。
        /// </summary>
        private static async Task<string> ReadSaveBodyAsync(IPage page)
        {
            var response = await page.WaitForResponseAsync(
                r => r.Request.Method == "POST" && r.Url.Contains("/JournalEntry/Create"),
                new PageWaitForResponseOptions { Timeout = 15_000 });
            return await response.TextAsync();
        }

        protected override async Task AfterSaveAsync(IPage page)
        {
            var text = await _saveBody!;

            // 驗證失敗時伺服端回的是 partial HTML（不是 JSON）；留給 base 的訊息處理。
            if (!text.TrimStart().StartsWith('{')) return;

            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(),
                $"手動傳票存檔被拒：{(root.TryGetProperty("message", out var m) ? m.GetString() : text)}");
            _journalId = root.GetProperty("data").GetProperty("id").GetString() ?? "";
        }

        /// <summary>
        /// 重開的必須是**剛建的那張**，且兩條分錄照原樣寫進去——
        /// 這支才是「存檔真的成立」的證據，base 的四支只證明「某一張打得開」。
        /// </summary>
        [Fact]
        public async Task 重開的是剛存的那張且借貸兩行都在()
        {
            var (page, _) = await CreateThenReopenAsync();
            var body = await page.InnerTextAsync(PanelBodySel);

            Assert.Contains(_journalId, body);
            Assert.Contains(DebitAccount, body);
            Assert.Contains(CreditAccount, body);
        }
    }
}
