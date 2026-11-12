using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.ORD
{
    /// <summary>銷售訂單（SalesOrder）「已存在單據」面板動作守門。</summary>
    public class SalesOrderActionSmokeTests : DocActionSmokeTestBase
    {
        public SalesOrderActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/SalesOrder";
        protected override string NewButtonSelector  => "button[data-url$='/SalesOrder/Create']";
        protected override string PanelReadySelector => "#soForm";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 表頭：客戶（必填，select2 底層 select 由 SelectOptionAsync 設值）
            await page.Locator($"{PanelBodySel} select[name='CompanyCode']").SelectOptionAsync("E2E-CO01");

            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var row = page.Locator($"{PanelBodySel} #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(400);

            var qty = row.Locator("input[name$='.OrderQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");
        }
    }
}
