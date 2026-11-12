using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.ORD
{
    /// <summary>出貨單（GoodsShipment）「已存在單據」面板動作守門。</summary>
    public class GoodsShipmentActionSmokeTests : DocActionSmokeTestBase
    {
        public GoodsShipmentActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/GoodsShipment";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        // 完整模式確認即開應收發票；2026-10-22 起發票未作廢不准取消確認（ShipmentUnconfirmInvoiceFirst），
        // 所以取消確認前先走真正的作廢端點（BeforeStepAsync）。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            new DocActionStep("Confirm",       ExpectedStatus: "1"),   // DocStatus.Confirmed
            new DocActionStep("CancelConfirm", ExpectedStatus: "0"),   // 退回 Draft
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "GoodsShipments" WHERE "GoodsShipmentId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/GoodsShipment/Create']";
        protected override string PanelReadySelector => "#gsForm";

        protected override async Task BeforeStepAsync(IPage page, DocActionStep step, string docId)
        {
            if (step.Action != "CancelConfirm") return;
            var invoiceId = await SeedFixture.ScalarAsync<string>(
                $"""SELECT "ARInvoiceId" FROM "ARInvoices" WHERE "SourceDocType"='GoodsShipment' AND "SourceDocId"='{docId}' AND "Status"<>3 LIMIT 1""");
            if (invoiceId == null) return;   // 簡化模式等未自動開票的設定（3＝ARInvoiceStatus.Void）
            var (ok, message) = await PostFormAsync(page, "/ARInvoice/Void", new Dictionary<string, string> { ["id"] = invoiceId });
            Assert.True(ok, $"/GoodsShipment {docId}：取消確認前作廢應收發票 {invoiceId} 失敗：{message}");
        }

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.Locator($"{PanelBodySel} select[name='CompanyCode']").SelectOptionAsync("E2E-CO01");

            await page.ClickAsync($"{PanelBodySel} [data-action='addLine']");
            await page.WaitForTimeoutAsync(400);

            var row = page.Locator($"{PanelBodySel} #linesTbody tr").Last;
            await row.Locator("select[name$='.ProductCode']").SelectOptionAsync("E2E-RM01");
            await page.WaitForTimeoutAsync(400);

            var qty = row.Locator("input[name$='.ShipQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");
        }
    }
}
