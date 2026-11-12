using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>稅別維護（BAS）CRUD smoke test。</summary>
    public class TaxSmokeTests : BasCrudSmokeTestBase
    {
        public TaxSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Tax";
        protected override string NewButtonSelector  => "button[data-url$='/Tax/Create']";
        protected override string PanelReadySelector => "input[name='TaxCode']";

        // 稅率為比率欄位，精確小數 step 屬合理設計（同 R9 baseline 豁免精神）
        protected override IReadOnlySet<string> AllowedSmallStepNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TaxRate" };

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='TaxCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='TaxName']", $"E2E稅別{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='TaxRate']", "0.05");
        }
    }
}
