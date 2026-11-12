using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>單號設定維護（BAS）CRUD smoke test。</summary>
    public class DocNumberConfigSmokeTests : BasCrudSmokeTestBase
    {
        public DocNumberConfigSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/DocNumberConfig";
        protected override string NewButtonSelector  => "button[data-url$='/DocNumberConfig/Create']";
        protected override string PanelReadySelector => "input[name='DocType']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='DocType']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='DocTypeName']", $"E2E單號{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='Prefix']", "E2E");
        }
    }
}
