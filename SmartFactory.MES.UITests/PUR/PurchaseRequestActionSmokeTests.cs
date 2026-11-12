using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>請購單（PurchaseRequest）「已存在單據」面板動作守門。</summary>
    public class PurchaseRequestActionSmokeTests : DocActionSmokeTestBase
    {
        public PurchaseRequestActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/PurchaseRequest";
        protected override string NewButtonSelector  => "button[data-url$='/PurchaseRequest/Create']";
        protected override string PanelReadySelector => "#prForm";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var row = page.Locator($"{PanelBodySel} #linesTbody tr").Last;

            // 選品號會觸發 onProductChange（帶出採購 UOM），等它安定再補值
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(600);

            var qty = row.Locator("input[name$='.RequestQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");

            // onProductChange 沒帶出 UOM 時自己補一個有效值
            var uom = row.Locator("select[name$='.UomCode']");
            if (await uom.CountAsync() > 0 && string.IsNullOrEmpty(await uom.First.InputValueAsync()))
                await uom.First.SelectOptionAsync("E2E-EA");
        }
    }
}
