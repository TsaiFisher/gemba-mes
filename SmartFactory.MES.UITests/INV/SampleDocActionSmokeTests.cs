using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>樣品單（SampleDoc）「已存在單據」面板動作守門。</summary>
    public class SampleDocActionSmokeTests : InvLineDocActionSmokeTestBase
    {
        public SampleDocActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/SampleDoc";

        // ⑤：確認 → 取消確認，兩步都讀資料庫驗狀態。
        protected override IReadOnlyList<DocActionStep> ActionSteps => new[]
        {
            // 沒有簽核設定時存檔即已確認，所以先取消確認、再確認回來。
            // 已確認的單列表只給「檢視」，取消確認在唯讀面板上（9/30 補上，之前按不到）。
            new DocActionStep("CancelConfirm", ExpectedStatus: "0"),   // 退回 Draft
            new DocActionStep("Confirm",       ExpectedStatus: "1"),   // DocStatus.Confirmed
        };

        protected override string DocStatusSql =>
            """SELECT "Status"::text FROM "SampleDocs" WHERE "SampleDocId" = '{0}'""";
        protected override string NewButtonSelector  => "button[data-url$='/SampleDoc/Create']";
        protected override string PanelReadySelector => "#sampleForm";

        protected override bool RequiresEntityCode => true;
    }
}
