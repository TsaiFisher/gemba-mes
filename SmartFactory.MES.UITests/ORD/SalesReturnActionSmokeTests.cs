using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.ORD
{
    /// <summary>銷售退回（SalesReturn）「已存在單據」面板動作守門。</summary>
    public class SalesReturnActionSmokeTests : DocActionSmokeTestBase
    {
        public SalesReturnActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/SalesReturn";
        protected override string NewButtonSelector  => "button[data-url$='/SalesReturn/Create']";
        protected override string PanelReadySelector => "#grForm";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.Locator($"{PanelBodySel} select[name='CompanyCode']").SelectOptionAsync("E2E-CO01");

            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var row = page.Locator($"{PanelBodySel} #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(400);

            var qty = row.Locator("input[name$='.ReturnQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");
        }
    }
}
