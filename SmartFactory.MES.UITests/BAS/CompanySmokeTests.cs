using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>公司（客戶/供應商）維護（BAS）CRUD smoke test。</summary>
    public class CompanySmokeTests : BasCrudSmokeTestBase
    {
        public CompanySmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Company";
        protected override string NewButtonSelector  => "button[data-url$='/Company/Create']";
        protected override string PanelReadySelector => "input[name='CompanyCode']";

        // TaxRate 為費率欄位，精確小數 step 屬合理設計（R9 baseline 豁免精神）
        protected override IReadOnlySet<string> AllowedSmallStepNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TaxRate" };

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='CompanyCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='CompanyName']", $"E2E公司{uniqueSuffix}");
        }
    }
}
