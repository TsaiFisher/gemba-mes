using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>班制維護（BAS）CRUD smoke test。</summary>
    public class ShiftSmokeTests : BasCrudSmokeTestBase
    {
        public ShiftSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Shift";
        protected override string NewButtonSelector  => "button[data-url$='/Shift/Create']";
        protected override string PanelReadySelector => "input[name='ShiftCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ShiftCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='ShiftName']", $"E2E班制{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='StartTime']", "08:00");
            await page.FillAsync($"{PanelBodySel} input[name='EndTime']", "17:00");
        }
    }
}
