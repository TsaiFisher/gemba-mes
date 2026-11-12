using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>進貨單（GoodsReceipt）「已存在單據」面板動作守門。</summary>
    public class GoodsReceiptActionSmokeTests : DocActionSmokeTestBase
    {
        public GoodsReceiptActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/GoodsReceipt";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            // 沒有簽核設定時存檔即已確認，所以先取消確認、再確認回來。
            new DocActionStep("CancelConfirm", ExpectedStatus: "0", ButtonSelector: "[data-action='cancelConfirmDoc']"),
            new DocActionStep("Confirm",       ExpectedStatus: "1", ButtonSelector: "[data-action='confirmDoc']"),
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "GoodsReceipts" WHERE "GoodsReceiptId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/GoodsReceipt/Create']";
        protected override string PanelReadySelector => "#grForm";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 表頭：供應商（必填，會觸發 onSupplierChange）
            await page.Locator($"{PanelBodySel} select[name='SupplierCode']").SelectOptionAsync("E2E-CO01");
            await page.WaitForTimeoutAsync(400);

            // 明細直接建（非從採購單匯入）
            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var row = page.Locator($"{PanelBodySel} #linesTbody tr").Last;

            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(600);

            var qty = row.Locator("input[name$='.ReceiptQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");

            var uom = row.Locator("select[name$='.UomCode']");
            if (await uom.CountAsync() > 0 && string.IsNullOrEmpty(await uom.First.InputValueAsync()))
                await uom.First.SelectOptionAsync("E2E-EA");
        }
    }
}
