using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 計畫停機（PlannedDowntime）面板動作守門。
    ///
    /// Id 系統自增；列表依 StartTime 遞減，故用未來起始時間讓本次新增那筆排第一列，
    /// 沿用 base 預設「開第一列 + /PlannedDowntime/Edit」命中它。
    /// </summary>
    public class PlannedDowntimeActionSmokeTests : DocActionSmokeTestBase
    {
        public PlannedDowntimeActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/PlannedDowntime";
        protected override string NewButtonSelector  => "button[data-url$='/PlannedDowntime/Create']";
        protected override string PanelReadySelector => "select[name='WorkCenterId']";

        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit], {PanelBodySel} button[type='submit']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 工作中心必填；選第一個真實選項而不綁死種子代碼。
            await page.Locator($"{PanelBodySel} select[name='WorkCenterId']")
                      .SelectOptionAsync(new SelectOptionValue { Index = 1 });

            // 起訖時間必填；用未來時間讓本次新增排在列表第一列（StartTime 遞減）。
            var start = new DateTime(2099, 1, 1).AddDays(Random.Shared.Next(0, 3000));
            await page.FillAsync($"{PanelBodySel} input[name='StartTime']", start.ToString("yyyy-MM-ddTHH:mm"));
            await page.FillAsync($"{PanelBodySel} input[name='EndTime']", start.AddHours(2).ToString("yyyy-MM-ddTHH:mm"));
        }
    }
}
