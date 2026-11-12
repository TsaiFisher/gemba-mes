using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>重工領料（ReworkMaterial）「已存在單據」面板動作守門。</summary>
    public class ReworkMaterialActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public ReworkMaterialActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/ReworkMaterial";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            // 沒有簽核設定時存檔即已確認，所以先取消確認、再確認回來。
            // 已確認的單列表只給「檢視」，取消確認在唯讀面板上（9/30 補上，之前按不到）。
            new DocActionStep("CancelConfirm", ExpectedStatus: "0"),   // 退回 Draft
            new DocActionStep("Confirm",       ExpectedStatus: "1"),   // DocStatus.Confirmed
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "ReworkMaterialDocs" WHERE "ReworkMaterialDocId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/ReworkMaterial/Create']";
        protected override string PanelReadySelector => "#reworkForm";

        // 選手動發料型別 RI（無來源單），才能直接建行；RR 需回沖來源不適合 smoke。
        // 工作中心為必填，用種子的 E2E-WC01。
        protected override async Task FillHeaderExtrasAsync(IPage page)
        {
            await page.SelectOptionAsync($"{PanelBodySel} #TypeCode", "RI");
            await page.WaitForTimeoutAsync(400);
            await page.SelectOptionAsync($"{PanelBodySel} #WorkCenterCodeSelect", "E2E-WC01");
            await page.WaitForTimeoutAsync(500);
        }
    }
}
