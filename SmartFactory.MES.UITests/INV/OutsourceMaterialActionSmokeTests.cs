using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>委外領料（OutsourceMaterial）「已存在單據」面板動作守門。</summary>
    public class OutsourceMaterialActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public OutsourceMaterialActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/OutsourceMaterial";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            // 沒有簽核設定時存檔即已確認，所以先取消確認、再確認回來。
            // 已確認的單列表只給「檢視」，取消確認在唯讀面板上（9/30 補上，之前按不到）。
            new DocActionStep("CancelConfirm", ExpectedStatus: "0"),   // 退回 Draft
            new DocActionStep("Confirm",       ExpectedStatus: "1"),   // DocStatus.Confirmed
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "OutsourceMaterialDocs" WHERE "OutsourceMaterialDocId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/OutsourceMaterial/Create']";
        protected override string PanelReadySelector => "#outsourceForm";

        // 選手動發料型別 OO（無來源單）+ 委外廠（供應商，此頁 id 為 SupplierCodeSelect 非共用的 EntityCodeSelect）。
        protected override async Task FillHeaderExtrasAsync(IPage page)
        {
            await page.SelectOptionAsync($"{PanelBodySel} #TypeCode", "OO");
            await page.WaitForTimeoutAsync(400);
            await page.SelectOptionAsync($"{PanelBodySel} #SupplierCodeSelect", "E2E-CO01");
            await page.WaitForTimeoutAsync(500);
        }
    }
}
