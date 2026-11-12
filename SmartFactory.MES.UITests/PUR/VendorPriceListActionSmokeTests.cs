using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>
    /// 供應商價目表（VendorPriceList）面板動作守門。
    ///
    /// 主鍵 Id 由系統產生，改以「廠商 + 品號」查詢縮小列表鎖定本次新增那筆。
    /// 廠商／品號皆為 select2 包裝的原生 select，SelectOptionAsync 對底層 select 有效
    /// （同 SalesOrderActionSmokeTests 的作法）。選第一個真實選項而不綁死種子代碼。
    /// </summary>
    public class VendorPriceListActionSmokeTests : DocActionSmokeTestBase
    {
        public VendorPriceListActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _supplierCode = "";
        private string _productCode  = "";

        protected override string IndexPath          => "/VendorPriceList";
        protected override string NewButtonSelector  => "button[data-url$='/VendorPriceList/Create']";
        protected override string PanelReadySelector => "#vendorPriceForm";

        protected override string ReopenIndexQuery =>
            $"supplierCode={_supplierCode}&productCode={_productCode}";

        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            var supplier = page.Locator($"{PanelBodySel} select[name='SupplierCode']");
            await supplier.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            _supplierCode = await supplier.InputValueAsync();

            var product = page.Locator($"{PanelBodySel} select[name='ProductCode']");
            await product.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            _productCode = await product.InputValueAsync();

            await page.Locator($"{PanelBodySel} select[name='CurrencyCode']")
                      .SelectOptionAsync(new SelectOptionValue { Index = 1 });

            await page.FillAsync($"{PanelBodySel} input[name='UnitPrice']", "10");
            await page.FillAsync($"{PanelBodySel} input[name='ValidFrom']", "2026-01-01");
        }
    }
}
