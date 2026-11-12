using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>
    /// 供應商評核（VendorEvaluation）面板動作守門。
    ///
    /// Save 有「廠商 × 年度 × 季度唯一」的應用層檢查，4 個 [Fact] 若都用同一組
    /// 就會在第二支之後被判重複而存不了檔，故每次以隨機年度建立。
    /// 品質／交期分數由後端重算（欄位 readonly），只需填價格與服務分數。
    /// </summary>
    public class VendorEvaluationActionSmokeTests : DocActionSmokeTestBase
    {
        public VendorEvaluationActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        private string _supplierCode = "";
        private int    _year;

        protected override string IndexPath          => "/VendorEvaluation";
        protected override string NewButtonSelector  => "button[data-url$='/VendorEvaluation/Create']";
        protected override string PanelReadySelector => "#vendorEvalForm";

        protected override string ReopenIndexQuery =>
            $"searchSupplier={_supplierCode}&year={_year}";

        protected override string ExpectedActionSelector =>
            $"{PanelBodySel} [data-action], {PanelBodySel} [data-action-submit]";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            var supplier = page.Locator($"{PanelBodySel} select[name='SupplierCode']");
            await supplier.SelectOptionAsync(new SelectOptionValue { Index = 1 });
            _supplierCode = await supplier.InputValueAsync();

            // 年度欄位限 2000..2100；隨機取值避開「廠商×年度×季度唯一」的重複檢查。
            _year = Random.Shared.Next(2000, 2101);
            await page.FillAsync($"{PanelBodySel} input[name='EvaluationYear']", _year.ToString());
            await page.FillAsync($"{PanelBodySel} input[name='EvaluationDate']", "2026-01-01");

            await page.FillAsync($"{PanelBodySel} input[name='PriceScore']", "80");
            await page.FillAsync($"{PanelBodySel} input[name='ServiceScore']", "80");
        }
    }
}
