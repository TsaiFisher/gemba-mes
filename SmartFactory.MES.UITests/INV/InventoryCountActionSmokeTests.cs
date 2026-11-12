using Microsoft.Playwright;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>
    /// 盤點單（InventoryCount）面板動作守門。
    ///
    /// 單據類、單號系統採番且列表預設 date_desc（新單在前），故沿用 base 預設的
    /// 「開列表第一列」即為本次新增那張，不需 ReopenIndexQuery。
    /// 盤點模式（CountMode）是必填的 radio，選 WithSheet 會觸發 toggleRangeFields
    /// 顯示範圍欄位，但範圍本身可留空（全盤）。
    /// </summary>
    public class InventoryCountActionSmokeTests : DocActionSmokeTestBase
    {
        public InventoryCountActionSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/InventoryCount";
        protected override string NewButtonSelector  => "button[data-url$='/InventoryCount/Create']";
        protected override string PanelReadySelector => "#sideForm";

        protected override Task PrepareDataAsync() => SeedFixture.ResetAsync();

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // CountMode 必填；WithSheet＝有盤點單，範圍留空代表全盤。
            await page.CheckAsync($"{PanelBodySel} input[name='CountMode'][value='WithSheet']");
            await page.WaitForTimeoutAsync(300);   // 等 toggleRangeFields 切換完
        }
    }
}
