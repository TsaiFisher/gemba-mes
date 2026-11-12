using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>產品分類維護（BAS）CRUD smoke test。</summary>
    public class ProductCategorySmokeTests : BasCrudSmokeTestBase
    {
        public ProductCategorySmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/ProductCategory";
        protected override string NewButtonSelector  => "button[data-url$='/ProductCategory/Create']";
        protected override string PanelReadySelector => "input[name='CategoryCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='CategoryCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='CategoryName']", $"E2E分類{uniqueSuffix}");
        }
    }
}
