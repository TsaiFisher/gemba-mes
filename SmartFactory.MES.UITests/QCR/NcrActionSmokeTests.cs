using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.QCR
{
    /// <summary>異常單（NCR）「已存在單據」面板動作守門。</summary>
    public class NcrActionSmokeTests : DocActionSmokeTestBase
    {
        public NcrActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/Ncr";
        protected override string NewButtonSelector  => "button[data-url$='/Ncr/Create']";
        protected override string PanelReadySelector => "input[name='ProductCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='ProductCode']", "E2E-RM01");
            await page.FillAsync($"{PanelBodySel} input[name='Qty']", "1");

            var uom = page.Locator($"{PanelBodySel} input[name='UomCode']");
            if (await uom.CountAsync() > 0) await uom.First.FillAsync("E2E-EA");
            // 預設處置是報廢，存檔即要求倉庫與儲位（2026-10-27）
            await page.SelectOptionAsync($"{PanelBodySel} #whSel", "E2E-WRAW");
            await page.FillAsync($"{PanelBodySel} input[name='LocationCode']", "E2E-LRAW");
        }
    }
}
