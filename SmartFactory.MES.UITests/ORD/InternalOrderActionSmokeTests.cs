using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.ORD
{
    /// <summary>內部領用單（InternalOrder）「已存在單據」面板動作守門。</summary>
    public class InternalOrderActionSmokeTests : DocActionSmokeTestBase
    {
        public InternalOrderActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/InternalOrder";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            // 沒有簽核設定時存檔即已確認，所以先取消確認、再確認回來。
            // 已確認的單列表只給「檢視」，取消確認在唯讀面板上（9/30 補上，之前按不到）。
            new DocActionStep("CancelConfirm", ExpectedStatus: "0"),   // 退回 Draft
            new DocActionStep("Confirm",       ExpectedStatus: "1"),   // DocStatus.Confirmed
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "InternalOrders" WHERE "InternalOrderId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/InternalOrder/Create']";
        protected override string PanelReadySelector => "#ioForm";

        // 未設簽核流程時，內部領用單存檔即自動核准並轉 Confirmed；後續領料走「倉庫作業」，
        // 本面板重開為唯讀且刻意無動作鈕。故第 ④ 條改驗唯讀面板正常重開（非強求動作鈕）。
        protected override bool ReopenedDocHasActions => false;

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 無客戶欄；TypeCode 預設 NO、DepartmentCode 非必填。直接建行即可。
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
