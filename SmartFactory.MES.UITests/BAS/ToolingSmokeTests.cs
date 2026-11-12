using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>模具/治具維護（BAS）CRUD smoke test。</summary>
    public class ToolingSmokeTests : BasCrudSmokeTestBase
    {
        public ToolingSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Tooling";
        protected override string NewButtonSelector  => "button[data-url$='/Tooling/Create']";
        protected override string PanelReadySelector => "input[name='ToolingCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ToolingCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='ToolingName']", $"E2E模具{uniqueSuffix}");
        }
    }
}
