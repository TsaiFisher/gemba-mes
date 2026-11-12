using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 作業類型簽核設定（<c>/DocumentApprovalConfig</c>）——「這種單據要不要走線上簽核、走哪一份範本」。
    ///
    /// <para><b>為什麼這一頁非得有瀏覽器層測試不可</b>：整份設定的意義集中在
    /// 「<c>IsApprovalRequired</c> 打勾 ⇒ 必須選一份範本」這個**只存在於前端**的連動上。
    /// 後端從來沒有驗過這個蘊含關係（<c>Create</c>／<c>Edit</c> 的 <c>ModelState.IsValid</c>
    /// 只看 <c>DocumentType</c> 與 <c>DocumentTypeName</c>），而下游一律寫成
    /// <c>config?.IsApprovalRequired == true &amp;&amp; !string.IsNullOrEmpty(config.ApprovalTemplateId)</c>
    /// ——**「要簽核但沒範本」會被當成「未設線上簽核」，單據直接 Approved**。
    /// 也就是說：前端連動一旦失效，症狀不是「畫面怪怪的」，
    /// 而是<b>管理員以為打開了簽核，單據卻全部自動放行，且沒有任何訊息</b>。</para>
    ///
    /// <para>🚨 全部唯讀：只開面板、只切換勾選框，<b>不按儲存</b>。
    /// :5110 與 ProjectZ 庫是多 session 共用的。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class DocumentApprovalConfigUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public DocumentApprovalConfigUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        private async Task<IPage> OpenIndexAsync()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);
            await page.GotoAsync($"{_fixture.BaseUrl}/DocumentApprovalConfig?searched=true",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            return page;
        }

        /// <summary>
        /// 開新增面板，並等 AJAX 進來的 <c>_ConfigForm</c> 真的掛上 DOM。
        /// </summary>
        private static async Task OpenCreatePanelAsync(IPage page)
        {
            var addButton = page.Locator("button[data-action='openPanel'][data-url*='/Create']").First;
            await Assertions.Expect(addButton).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await addButton.ClickAsync();

            // 等的是**面板裡的欄位**而不是 offcanvas 容器：容器一開始就在 DOM 上，
            // 等它等於沒等；欄位是 fetch 回來以後才 DOMPurify.sanitize 進去的。
            await Assertions.Expect(page.Locator("#crudPanelBody #isApprovalRequired"))
                .ToBeVisibleAsync(new() { Timeout = 15_000 });
        }

        /// <summary>
        /// 🚨 本檔最重要的一支：<b>把「需要簽核」打勾之後，範本選擇欄要真的看得見</b>。
        ///
        /// <para>斷言用 <c>ToBeVisibleAsync</c> 而不是比對 class 字串——Playwright 依
        /// <b>computed style</b> 判定，帶 <c>!important</c> 的 class 也擋不住它；
        /// 而「class 上有沒有 d-none」這種字串比對，正好看不出
        /// 「inline style 蓋不過 class 的 !important」這一類回歸。</para>
        ///
        /// <para>兩個方向都驗。只驗「打勾之後看得見」的話，
        /// 一個永遠顯示的欄位照樣通過，就漏掉了「不需簽核卻逼人選範本」那一半。</para>
        /// </summary>
        [Fact]
        public async Task 需要簽核打勾之後_範本選擇欄要真的看得見()
        {
            var page = await OpenIndexAsync();
            await OpenCreatePanelAsync(page);

            var checkbox = page.Locator("#crudPanelBody #isApprovalRequired");
            var templateDiv = page.Locator("#crudPanelBody #templateDiv");

            // 新增的預設是 IsApprovalRequired=false（Model 的預設值），所以一開始該藏著。
            Assert.False(await checkbox.IsCheckedAsync(),
                "新增面板的「需要簽核」預設應該是沒有打勾的。");
            await Assertions.Expect(templateDiv).Not.ToBeVisibleAsync();

            await checkbox.CheckAsync();
            await Assertions.Expect(templateDiv).ToBeVisibleAsync(new() { Timeout = 5_000 });

            // 看得見還不夠——真正要能選得到那一份範本。
            var select = templateDiv.Locator("select");
            await Assertions.Expect(select).ToBeVisibleAsync();
            var optionCount = await select.Locator("option").CountAsync();
            _out.WriteLine($"範本下拉選項數（含空白項）：{optionCount}");
            Assert.True(optionCount > 1,
                "範本下拉只有空白項——就算欄位露出來了，管理員仍然選不到任何範本。");

            // 反向：取消勾選要收回去。
            await checkbox.UncheckAsync();
            await Assertions.Expect(templateDiv).Not.ToBeVisibleAsync(new() { Timeout = 5_000 });
        }
    }
}
