using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests.BAS
{
    /// <summary>
    /// BAS 主檔「明細分頁」可見性回歸守門（Uom 單位換算 / Warehouse 儲位 /
    /// ShiftGroup 週間班別 / Currency 匯率）。
    ///
    /// 背景：CSP style-src 外部化（de550f5）將 tabDetailNavItem 的
    /// inline style="display:none" 改為 u-display-none class（!important），
    /// 但 JS 仍以 style.display = '' 顯示 → class 蓋不掉，明細分頁永久隱藏，
    /// 使用者看起來就是「單位換算／儲位設定不見了」。
    ///
    /// 本測試點開任一筆資料的明細面板後，斷言 #tabDetailNavItem 真正可見
    /// （Playwright 的 visible 依 computed style 判斷，class 隱藏也抓得到）。
    /// </summary>
    [Collection("UI Tests")]
    public class DetailTabSmokeTests
    {
        private readonly UiTestFixture _fixture;

        public DetailTabSmokeTests(UiTestFixture fixture) => _fixture = fixture;

        private async Task<IPage> NewLoggedInPageAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            return page;
        }

        [Theory]
        [InlineData("/Uom")]
        [InlineData("/Warehouse")]
        [InlineData("/ShiftGroup")]
        [InlineData("/Currency")]
        public async Task OpenDetailPanel_DetailTab_IsVisible_AndLoadsContent(string indexPath)
        {
            var page = await NewLoggedInPageAsync();
            var jsErrors = new List<string>();
            page.PageError += (_, e) => jsErrors.Add(e);

            await page.GotoAsync($"{_fixture.BaseUrl}{indexPath}?searched=true");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var detailButtons = page.Locator("[data-action='openDetailPanel']");
            Assert.True(await detailButtons.CountAsync() > 0,
                $"{indexPath} 列表沒有任何資料列（openDetailPanel 按鈕），無法驗證明細分頁");

            await detailButtons.First.ClickAsync();
            await page.WaitForSelectorAsync("#crudPanel.show",
                new() { Timeout = 8000 });

            // 核心斷言：明細分頁的 nav item 必須「實際可見」
            var tabNav = page.Locator("#tabDetailNavItem");
            await Assertions.Expect(tabNav).ToBeVisibleAsync(new() { Timeout = 5000 });

            // 點分頁 → 內容區應載入且無 JS 錯誤
            await tabNav.Locator("button").ClickAsync();
            await page.WaitForSelectorAsync("#tabDetail.active",
                new() { Timeout = 8000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.Empty(jsErrors);
        }
    }
}
