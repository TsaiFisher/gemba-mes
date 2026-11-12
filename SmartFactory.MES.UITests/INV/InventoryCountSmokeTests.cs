using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.INV
{
    /// <summary>盤點清冊（INV）CRUD smoke test。</summary>
    public class InventoryCountSmokeTests : CrudSmokeTestBase
    {
        public InventoryCountSmokeTests(UiTestFixture fixture) : base(fixture) { }

        protected override string IndexPath          => "/InventoryCount";
        protected override string NewButtonSelector  => "button[data-url$='/InventoryCount/Create']";
        // CountMode radio 是第一個出現在面板的互動元素
        protected override string PanelReadySelector => "input[name='CountMode']";

        protected override async Task FillRequiredFieldsAsync(IPage page, string uniqueSuffix)
        {
            // CountMode 為必填，選擇 WithoutSheet（最簡單，不需填倉庫範圍）
            await page.ClickAsync("#sidePanelBody input[name='CountMode'][value='WithoutSheet']");
        }

        /// <summary>
        /// 覆寫：InventoryCount 表單預設 CountMode="WithSheet" 已合法，
        /// 空白送出應成功建立清冊而非回傳驗證錯誤，基類斷言不適用此畫面。
        /// </summary>
        [Fact]
        public override async Task NewPanel_EmptySubmit_ShowsValidationError_StaysOnPanel()
        {
            var (page, panel) = await NewLoggedInPageAsync();
            await panel.GotoIndexAsync(IndexPath);
            await panel.OpenNewPanelAsync(NewButtonSelector, PanelReadySelector);

            // 空白送出（CountMode 預選 WithSheet）應成功儲存
            var result = await panel.ClickSaveAsync(timeoutMs: 8000);
            Assert.Equal(SaveResult.Success, result);
        }
    }
}
