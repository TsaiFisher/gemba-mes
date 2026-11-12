using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 工作日曆（WorkCalendar）面板動作守門。
    ///
    /// Id 為系統自增、測試事先不知道；列表依 Date 遞減排序，故用未來日期讓本次新增
    /// 那筆穩定排在第一列，沿用 base 預設的「開第一列 + /WorkCalendar/Edit」即命中它。
    /// 4 個 [Fact] 各用隨機未來日期，避免同日期＋空工作中心撞唯一鍵。
    /// </summary>
    public class WorkCalendarActionSmokeTests : DocActionSmokeTestBase
    {
        public WorkCalendarActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/WorkCalendar";
        protected override string NewButtonSelector  => "button[data-url$='/WorkCalendar/Create']";
        protected override string PanelReadySelector => "input[name='Date']";

        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit], {PanelBodySel} button[type='submit']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 只有 Date 必填；用未來隨機日期保證排在列表第一列，且避開既有資料的唯一鍵。
            var d = new DateTime(2099, 1, 1).AddDays(Random.Shared.Next(0, 3000));
            await page.FillAsync($"{PanelBodySel} input[name='Date']", d.ToString("yyyy-MM-dd"));
        }
    }
}
