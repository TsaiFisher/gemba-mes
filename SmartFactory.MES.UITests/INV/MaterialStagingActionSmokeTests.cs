using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 備料單（MaterialStaging）面板動作守門。
    ///
    /// 用 Manual 來源型別＝不綁工單/派工，最能自成一單；備料倉必填，明細需品號＋需求量。
    /// 明細要能備料就得有可用庫存，故沿用 <see cref="SeedFixture"/> 每次重跑把 E2E-RM01
    /// 的庫存回到基準值（測試執行順序不固定，不能倚賴前一個測試留下的庫存）。
    /// </summary>
    public class MaterialStagingActionSmokeTests : DocActionSmokeTestBase
    {
        public MaterialStagingActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/MaterialStaging";
        protected override string NewButtonSelector  => "button[data-url$='/MaterialStaging/Create']";
        protected override string PanelReadySelector => "#stagingForm";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // SourceType 預設 Manual（第一個選項）＝不綁來源單。

            // 備料倉必填：選第一個實際倉庫，等 filterLocationsByWh 帶出儲位後選第一個儲位。
            await page.SelectOptionAsync($"{PanelBodySel} #stagingWh",
                new SelectOptionValue { Index = 1 });
            await page.WaitForTimeoutAsync(500);
            var locOpts = await page.Locator($"{PanelBodySel} #stagingLoc option").CountAsync();
            if (locOpts > 1)
                await page.SelectOptionAsync($"{PanelBodySel} #stagingLoc", new SelectOptionValue { Index = 1 });

            // 一筆手動明細：addStagingLine 附加空白列到 #linesBody；品號取第一個實際品號
            // （Save 僅建草稿、不做庫存預留，故任一品號皆可），再填需求量。
            await page.ClickAsync($"{PanelBodySel} [data-action='addStagingLine']");
            await page.WaitForTimeoutAsync(400);
            var lastRow = page.Locator($"{PanelBodySel} #linesBody tr").Last;
            await lastRow.Locator("select.line-product").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await page.WaitForTimeoutAsync(800);   // onProductChange 內含 fetch（帶出 UOM）
            var qty = lastRow.Locator("input[name$='.RequiredQty']");
            if (await qty.CountAsync() > 0) await qty.First.FillAsync("1");
        }
    }
}
