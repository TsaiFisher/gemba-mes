using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>產品主檔維護（BAS）CRUD smoke test。</summary>
    public class ProductSmokeTests : BasCrudSmokeTestBase
    {
        public ProductSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Product";
        protected override string NewButtonSelector  => "button[data-url$='/Product/Create']";
        protected override string PanelReadySelector => "input[name='ProductCode']";

        // StandardCost 為金額欄位，step 依本位幣小數位（CLAUDE.md 金額精度規範），
        // 屬 R9 費率/金額豁免精神
        protected override IReadOnlySet<string> AllowedSmallStepNames
            => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "StandardCost" };

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ProductCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='ProductName']", $"E2E產品{uniqueSuffix}");
            // StockUom 為必填 select，選第一個非空選項
            await page.Locator($"{PanelBodySel} select[name='StockUom']")
                      .SelectOptionAsync(new SelectOptionValue { Index = 1 });
            // 主分類（radio）為必填（ValidateAndNormalizeCategories），點第一個
            await page.Locator($"{PanelBodySel} input[name='CategoryCode']").First.CheckAsync();
        }
    }
}
