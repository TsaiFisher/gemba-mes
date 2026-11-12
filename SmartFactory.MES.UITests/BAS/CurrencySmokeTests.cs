using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>幣別維護（BAS）CRUD smoke test。</summary>
    public class CurrencySmokeTests : BasCrudSmokeTestBase
    {
        public CurrencySmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Currency";
        protected override string NewButtonSelector  => "button[data-url$='/Currency/Create']";
        protected override string PanelReadySelector => "input[name='CurrencyCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='CurrencyCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='CurrencyName']", $"E2E幣別{uniqueSuffix}");
        }
    }
}
