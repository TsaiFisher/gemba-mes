using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.PUR
{
    /// <summary>
    /// 委外收貨單（OutsourceReceipt）面板動作守門。
    ///
    /// 單號採番、列表新單在前（依 CreatedAt 遞減），沿用 base 預設「開列表第一列」。
    /// Create 下拉只列「本廠 + Status=Confirmed」的委外採購單，Save 亦驗其存在，故 E2E 種子
    /// 補了一張 Confirmed 委外採購單 E2E-OPO01（供應商 E2E-CO01）。明細選填，選 OPO 即可存草稿。
    /// </summary>
    public class OutsourceReceiptActionSmokeTests : DocActionSmokeTestBase
    {
        public OutsourceReceiptActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/OutsourceReceipt";
        protected override string NewButtonSelector  => "button[data-url$='/OutsourceReceipt/Create']";
        protected override string PanelReadySelector => "#panelForm";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // 委外採購單必填：選種子的 Confirmed 委外採購單（loadOpoLines 會帶出供應商與明細）。
            await page.SelectOptionAsync($"{PanelBodySel} #opoSelect",
                new SelectOptionValue { Value = "E2E-OPO01" });
            await page.WaitForTimeoutAsync(600);   // loadOpoLines 內含 fetch

            // 供應商亦為必填；若 loadOpoLines 未自動帶入，明確選種子供應商 E2E-CO01。
            var supplier = page.Locator($"{PanelBodySel} select[name='SupplierCode']");
            if (await supplier.CountAsync() > 0)
                await supplier.SelectOptionAsync(new SelectOptionValue { Value = "E2E-CO01" });
        }
    }
}
