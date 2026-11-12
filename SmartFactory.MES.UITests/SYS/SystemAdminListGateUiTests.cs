using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.UITests.SYS
{
    /// <summary>
    /// 系統管理七頁的 <c>searched=true</c> 閘門：<b>沒按查詢之前不倒資料出來</b>。
    ///
    /// <para><b>為什麼這七頁特別值得守</b>：這裡倒出來的是使用者名單、角色、組織階層、
    /// 廠別與簽核路徑——也就是<b>這套系統裡誰是誰、誰簽得了什麼</b>。
    /// 閘門失效的症狀是「一進頁面就看到全部」，那看起來像功能正常，
    /// 不像缺陷，所以不會有人回報。</para>
    ///
    /// <para>這七支 Controller 在此之前<b>一支都沒有瀏覽器層測試</b>
    /// （<c>grep -rl "Permission\|Organization\|ApprovalTemplate" SmartFactory.MES.UITests/</c>
    /// 是 0）。整合測試驗得到 ViewBag.Searched 的值，驗不到「表格到底有沒有渲染出來」。</para>
    ///
    /// <para>🚨 全部唯讀：只讀兩個狀態，不新增、不修改、不刪除。
    /// :5110 與 ProjectZ 庫是多 session 共用的。</para>
    /// </summary>
    [Collection("UI Tests")]
    public class SystemAdminListGateUiTests
    {
        private readonly UiTestFixture _fixture;
        private readonly ITestOutputHelper _out;

        public SystemAdminListGateUiTests(UiTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _out = output;
        }

        /// <param name="resultSelector">
        /// 這一頁「查完之後應該長出來的東西」。
        ///
        /// <para>⚠️ 六頁是表格，<b><c>/Organization</c> 不是</b>——它在瀏覽模式渲染的是
        /// 組織<b>樹</b>（<c>ul.list-group</c>），只有帶了搜尋字串、走
        /// <c>isSearchMode</c> 那一支時才改用扁平表格。第一版整批寫成
        /// <c>table thead th</c>，於是 <c>/Organization</c> 紅在「連表頭都沒渲染出來」，
        /// 而那是測試的假設錯了，不是畫面壞了。逐頁帶自己的選擇器就是為了記住這件事。</para>
        /// </param>
        [Theory]
        [InlineData("/Role", "table thead th")]
        [InlineData("/User", "table thead th")]
        [InlineData("/Permission", "table thead th")]
        [InlineData("/Organization", ".list-group .list-group-item")]
        [InlineData("/Plant", "table thead th")]
        [InlineData("/ApprovalTemplate", "table thead th")]
        [InlineData("/DocumentApprovalConfig", "table thead th")]
        public async Task 七頁在按查詢之前不倒資料出來_按了之後內容才在(string path, string resultSelector)
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);

            // ── 閘門關著 ──────────────────────────────────────────────────────
            await page.GotoAsync($"{_fixture.BaseUrl}{path}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.True(await page.Locator("form[method='get']").CountAsync() > 0,
                $"{path} 沒有查詢列——使用者連查都查不了。");
            Assert.Equal(0, await page.Locator(resultSelector).CountAsync());

            // ── 閘門開著 ──────────────────────────────────────────────────────
            await page.GotoAsync($"{_fixture.BaseUrl}{path}?searched=true",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // 骨架與資料分開看：骨架（表頭／樹節點）證明那一段真的渲染了，
            // 資料列是環境有沒有資料。兩件事混在一起斷言的話，
            // 「畫面壞了」與「這個環境沒資料」分不出來。
            var skeleton = await page.Locator(resultSelector).CountAsync();
            var rows = await page.Locator("table tbody tr").CountAsync();
            _out.WriteLine($"{path}：{resultSelector} → {skeleton}／資料列 {rows}");
            Assert.True(skeleton > 0,
                $"{path}?searched=true 的「{resultSelector}」一個都沒渲染出來。");
        }

        /// <summary>
        /// 這七頁都是 <c>[Authorize(Roles = "Admin")]</c> 或等價的權限閘門把關的。
        /// 這裡只確認**已登入的管理員拿得到 200**，不是驗權限本身
        /// （那是整合測試的範圍，而且要拿真的低權角色才驗得出來）。
        /// 之所以還是要有這一支：上面那支若因為登入流程壞掉而每一頁都拿到重導，
        /// 「沒有資料列」這個斷言會**照樣通過**。
        /// </summary>
        [Fact]
        public async Task 七頁對已登入的管理員都回二百而不是重導到登入頁()
        {
            var page = await _fixture.NewPageAsync();
            await _fixture.SignInAsync(page);

            string[] paths =
            [
                "/Role", "/User", "/Permission", "/Organization",
                "/Plant", "/ApprovalTemplate", "/DocumentApprovalConfig"
            ];

            foreach (var path in paths)
            {
                var response = await page.GotoAsync($"{_fixture.BaseUrl}{path}?searched=true",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                Assert.NotNull(response);
                Assert.True(response!.Status < 400, $"{path} 回了 {response.Status}。");

                // 導到登入頁也是 200，所以狀態碼不夠——要確認人還在原本那一頁。
                Assert.DoesNotContain("/Identity/Account/Login", page.Url, StringComparison.OrdinalIgnoreCase);
                _out.WriteLine($"{path} → {response.Status}");
            }
        }
    }
}
