using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// MRP 執行單（MrpRun）面板動作守門。
    ///
    /// 單號採番、列表新單在前，沿用 base 預設「開列表第一列」即本次新增那張。
    /// Create 一律存為 Draft（見 MrpRunController.Create），表頭無必填、需求明細選填，
    /// 故存檔只需按 SaveDraft（面板內第一個 submit），不觸發 doExecute 引擎執行。
    /// </summary>
    public class MrpRunActionSmokeTests : DocActionSmokeTestBase
    {
        public MrpRunActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/MrpRun";
        protected override string NewButtonSelector  => "button[data-url$='/MrpRun/Create']";
        protected override string PanelReadySelector => "#mrpRunForm";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 表頭無必填欄位；填 Remarks 讓草稿有可辨識內容即可。
            var remarks = page.Locator($"{PanelBodySel} input[name='Remarks']");
            if (await remarks.CountAsync() > 0)
                await remarks.First.FillAsync($"Smoke {uniqueSuffix}");
        }
    }
}
