using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>計量單位維護（BAS）CRUD smoke test。</summary>
    public class UomSmokeTests : BasCrudSmokeTestBase
    {
        public UomSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Uom";
        protected override string NewButtonSelector  => "button[data-url$='/Uom/Create']";
        protected override string PanelReadySelector => "input[name='UomCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='UomCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='UomName']", $"E2E單位{uniqueSuffix}");
        }
    }
}
