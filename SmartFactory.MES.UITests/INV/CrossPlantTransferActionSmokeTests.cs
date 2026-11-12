using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 跨廠調撥單（CrossPlantTransfer）面板動作守門。
    ///
    /// 單號採番、列表新單在前，沿用 base 預設「開列表第一列」。Create 存為草稿，
    /// 表頭僅 TransferDate（預填）與 ToPlantId 必填、明細選填，故只需選一個「非本廠」的目的廠。
    /// E2E 種子補了第二廠 E2E-P2 讓 ToPlantId 下拉有可選項（乾淨庫僅 P1）。
    /// </summary>
    public class CrossPlantTransferActionSmokeTests : DocActionSmokeTestBase
    {
        public CrossPlantTransferActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/CrossPlantTransfer";

        // ⑤ 不設：確認需要明細（來源廠庫存＋目的倉儲位），整合測試 CrossPlantTransferFlowTests／
        // CrossPlantTransferPostingResidueTests 已以真實資料庫完整跑過確認與取消確認。
        protected override string NewButtonSelector  => "button[data-url$='/CrossPlantTransfer/Create']";
        protected override string PanelReadySelector => "#xpForm";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 目的廠必填且不得等於本廠（P1）；選種子的第二廠 E2E-P2。
            await page.SelectOptionAsync($"{PanelBodySel} #toPlantSelect",
                new SelectOptionValue { Value = "E2E-P2" });
            await page.WaitForTimeoutAsync(400);   // onToPlantChange 內含 fetch（帶出目的倉/儲位）
        }
    }
}
