using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.MFG
{
    /// <summary>
    /// 委外報價（OutsourceQuotation）面板動作守門。
    ///
    /// QuotationId 由 DocNumber 系統採番，測試事先不知道；改以「廠商 + 品號」查詢縮小
    /// 列表鎖定本次新增那筆。廠商／品號／幣別／單位皆為 select，選第一個真實選項。
    /// </summary>
    public class OutsourceQuotationActionSmokeTests : DocActionSmokeTestBase
    {
        public OutsourceQuotationActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _supplierCode = "";
        private string _productCode  = "";

        protected override string IndexPath          => "/OutsourceQuotation";
        protected override string NewButtonSelector  => "button[data-url$='/OutsourceQuotation/Form'], button[data-url$='/OutsourceQuotation/Create']";
        protected override string PanelReadySelector => "#outsourceQuotationForm";

        // 新增與重開都走 /OutsourceQuotation/Form；列上編輯鈕是 Form/{id}（非 Edit/Details）。
        protected override IReadOnlyList<string> OpenUrlFragments =>
            new[] { "/OutsourceQuotation/Form/" };

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
            await page.Locator($"{PanelBodySel} select[name='UomCode']")
                      .SelectOptionAsync(new SelectOptionValue { Index = 1 });

            await page.FillAsync($"{PanelBodySel} input[name='UnitPrice']", "10");
            await page.FillAsync($"{PanelBodySel} input[name='LeadDays']", "7");
            await page.FillAsync($"{PanelBodySel} input[name='EffectiveFrom']", "2026-01-01");

            // 勾選啟用：列表預設 activeOnly=true，未啟用的報價查不到，重開會落空。
            await page.CheckAsync($"{PanelBodySel} input[name='IsActive']");
        }
    }
}
