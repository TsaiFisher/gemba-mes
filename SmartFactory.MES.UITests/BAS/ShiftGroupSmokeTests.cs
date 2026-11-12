using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>班別群組維護（BAS）CRUD smoke test。</summary>
    public class ShiftGroupSmokeTests : BasCrudSmokeTestBase
    {
        public ShiftGroupSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/ShiftGroup";
        protected override string NewButtonSelector  => "button[data-url$='/ShiftGroup/Create']";
        protected override string PanelReadySelector => "input[name='GroupCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='GroupCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='GroupName']", $"E2E群組{uniqueSuffix}");
        }
    }
}
