using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>
    /// 量測儀器（Instrument）面板動作守門。
    ///
    /// 主檔類、主鍵由使用者輸入，故鎖定「本次新增那筆」重開（理由見
    /// <see cref="DocActionSmokeTestBase.ReopenIndexQuery"/>）。
    /// 表單本身沒有 id 屬性，以 data-action-submit 當面板就緒判斷。
    /// </summary>
    public class InstrumentActionSmokeTests : DocActionSmokeTestBase
    {
        public InstrumentActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _code = "";

        protected override string IndexPath          => "/Instrument";
        protected override string NewButtonSelector  => "button[data-url$='/Instrument/Create']";
        protected override string PanelReadySelector => "form[data-action-submit='submitPanel']";

        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { $"/Instrument/Edit/{_code}" };

        protected override string ReopenIndexQuery => $"searchCode={_code}";

        // 面板動作只有表單自身的 data-action-submit，沒有 data-action 按鈕。
        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // InstrumentCode 上限 20 字元；base 的 uniqueSuffix 只到分鐘，補隨機碼避免
            // 同一分鐘內 4 個 [Fact] 撞主鍵。
            _code = $"SMK{uniqueSuffix}{Random.Shared.Next(1000, 9999)}";

            await page.FillAsync($"{PanelBodySel} input[name='InstrumentCode']", _code);
            await page.FillAsync($"{PanelBodySel} input[name='InstrumentName']", "Smoke 測試儀器");
        }
    }
}
