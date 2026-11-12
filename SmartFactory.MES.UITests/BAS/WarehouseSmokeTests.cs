using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>倉庫維護（BAS）CRUD smoke test。</summary>
    public class WarehouseSmokeTests : BasCrudSmokeTestBase
    {
        public WarehouseSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Warehouse";
        protected override string NewButtonSelector  => "button[data-url$='/Warehouse/Create']";
        protected override string PanelReadySelector => "input[name='WhCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='WhCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='WhName']", $"E2E倉庫{uniqueSuffix}");
        }
    }
}
