using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests
{
    // R9 對應的瀏覽器端回歸（consistency-framework.md Phase E）：
    // 生產管理（MFG 途程）新增面板的數量/工時/率欄位,step 必為 "any"
    // → 上下三角形(spinner)每按一次 ±1、仍可鍵入小數;序號欄刻意維持 step="10"
    // 不被波及。step 屬性即決定 spinner 增減量,故以 DOM 屬性斷言為穩定回歸點
    // （原生 spin 按鈕屬 shadow pseudo-element,Playwright 無法穩定點擊;且 stepUp()
    //   於 step="any" 會丟 InvalidStateError,故不採函式式點擊驗證）。
    [Collection("UI Tests")]
    public class RoutingFormStepTests
    {
        private readonly UiTestFixture _fixture;

        public RoutingFormStepTests(UiTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task RoutingCreatePanel_QuantityInputs_UseStepAny()
        {
            // 登入
            var page = await _fixture.NewLoggedInPageAsync();

            // 進入途程主檔,開啟「新增」側滑面板（openPanel 注入 _Form 至 #sidePanelBody）
            await page.GotoAsync($"{_fixture.BaseUrl}/Routing");
            await page.ClickAsync("button[data-url$='/Routing/Create']");
            await page.WaitForSelectorAsync("#sidePanelBody #BatchSize");

            // 數量/工時/率欄位:step 必為 "any"（spinner ±1）
            foreach (var id in new[]
            {
                "BatchSize", "AvgLeadTime", "opQtBor", "opSetup", "opUnit", "bomQty", "bomScrap"
            })
            {
                var step = await page.GetAttributeAsync($"#{id}", "step");
                Assert.True(step == "any",
                    $"#{id} 的 step 應為 \"any\"（spinner ±1）,實際為 \"{step}\"");
            }

            // 序號欄刻意維持 step="10",不應被 R9 波及（防過度套用的反向守門）
            Assert.Equal("10", await page.GetAttributeAsync("#opSeq", "step"));
            Assert.Equal("10", await page.GetAttributeAsync("#bomSeq", "step"));
        }
    }
}
