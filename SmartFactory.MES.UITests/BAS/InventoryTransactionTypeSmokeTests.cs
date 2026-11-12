using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>
    /// 庫存異動類型維護（BAS）CRUD smoke test。
    /// 注意：此畫面是 BAS 中唯一採共用 crud-panel.js/sidePanel 契約者，
    /// 直接繼承 CrudSmokeTestBase（勿用 BasCrudSmokeTestBase）。
    /// </summary>
    public class InventoryTransactionTypeSmokeTests : CrudSmokeTestBase
    {
        public InventoryTransactionTypeSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/InventoryTransactionType";
        protected override string NewButtonSelector  => "button[data-url$='/InventoryTransactionType/Create']";
        protected override string PanelReadySelector => "input[name='TypeCode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            await page.FillAsync($"{PanelBodySel} input[name='TypeCode']", $"E2E{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='TypeName']", $"E2E異動{uniqueSuffix}");
            await page.FillAsync($"{PanelBodySel} input[name='TypeCategory']", "E2E");
        }
    }
}
