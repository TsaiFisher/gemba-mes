using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>
    /// 檢驗規格（InspectionSpec）面板動作守門。
    ///
    /// 主鍵 SpecId 由系統產生（測試事先不知道），但使用者輸入的 SpecCode 可查詢，
    /// 故用 searchText 把列表縮到只剩本次新增那筆，再開第一列——效果等同鎖定該筆，
    /// 避免新增靜默失敗卻打開別人的資料而假綠。
    /// </summary>
    public class InspectionSpecActionSmokeTests : DocActionSmokeTestBase
    {
        public InspectionSpecActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _specCode = "";

        protected override string IndexPath          => "/InspectionSpec";
        protected override string NewButtonSelector  => "button[data-url$='/InspectionSpec/Create']";
        protected override string PanelReadySelector => "#sideForm";

        protected override string ReopenIndexQuery => $"searchText={_specCode}";

        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // SpecCode 上限 30 字元，欄位為 text-uppercase，直接用大寫值。
            _specCode = $"SMK{uniqueSuffix}{Random.Shared.Next(1000, 9999)}";

            await page.FillAsync($"{PanelBodySel} input[name='SpecCode']", _specCode);
            await page.FillAsync($"{PanelBodySel} input[name='SpecName']", "Smoke 測試檢驗規格");
        }
    }
}
