using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    /// <summary>
    /// 手機殼裡的錯誤頁不得套用桌機版面。
    ///
    /// <para><b>2026-09-08 在 Core 上實測到的缺陷</b>：手機登入後點功能磚，畫面直接變成
    /// 電腦版。原因是一條完整的鏈——手機首頁的磚由前端用 <c>moduleKey.toLowerCase()</c>
    /// 拼出 <c>/mobile/{key}/list.html</c>，而 Core 的 <c>wwwroot/mobile</c> 只有
    /// home／index 兩頁、一個模組頁都沒有；於是 404 被
    /// <c>UseStatusCodePagesWithReExecute("/Error/{0}")</c> 接到 <c>/Error/404</c>，
    /// 而 <c>Views/Error/Index.cshtml</c> 沒有 <c>Layout</c> 指示，吃的是
    /// <c>_ViewStart</c> 的桌機版面。</para>
    ///
    /// <para><b>為什麼這比「錯誤頁不好看」嚴重</b>：手機 PWA 是 standalone 啟動的，
    /// 沒有網址列也沒有分頁列。畫面一換成桌機版面，使用者就<b>回不到手機殼</b>，
    /// 只剩 OS 的返回手勢；而桌機錯誤頁上那顆「返回首頁」指向 <c>/</c>，
    /// 按下去只會把他推得更深。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileErrorPageTests
    {
        // 桌機版面的可辨識標記：側邊欄容器。整個 _Layout 只有它會出現這個 id。
        private const string DesktopLayoutMarker = "sidebar-container";

        // 手機錯誤頁一定要給得出「回得去手機殼」的路徑。
        private const string MobileHomeLink = "/mobile/home.html";

        private readonly TestWebApplicationFactory _factory;

        public MobileErrorPageTests(TestWebApplicationFactory factory) => _factory = factory;

        // ⚠️ 這裡的網址一律用**不存在的模組鍵／頁名**，不可以寫真實模組。
        //
        // 一開始這組資料寫的是 `/mobile/mfg/list.html`，理由是「Core 沒有這一頁」。
        // 但 Core 是部署專案的基底，這支測試會跟著被繼承——而部署專案**真的有**
        // 那一頁（ProjectZ 就有 wwwroot/mobile/mfg/list.html）。於是同一支測試在
        // Core 綠、在部署專案紅，而且紅的原因與它要測的東西完全無關。
        // 下面 GetBodyAsync 裡那顆狀態碼斷言就是這樣抓到的。
        [Theory]
        [InlineData("/mobile/nosuchmodule/list.html")]  // 事故現場的形狀：磚用 moduleKey 拼出來的模組頁
        [InlineData("/mobile/mfg/nosuchpage.html")]     // 模組目錄有、頁面沒有（部署專案才碰得到的情境）
        [InlineData("/mobile/nosuchthing")]             // 單層路徑
        public async Task 手機路徑的404不得套用桌機版面(string url)
        {
            var body = await GetBodyAsync(url);

            Assert.DoesNotContain(DesktopLayoutMarker, body, StringComparison.Ordinal);
            Assert.Contains(MobileHomeLink, body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 桌機路徑的404仍然是桌機版面()
        {
            // 反面對照。少了這一支，一個「把所有錯誤頁都改成手機版」的實作
            // 也會讓上面那組變綠——而那會讓桌機使用者失去導覽列。
            var body = await GetBodyAsync("/nosuchdesktoppage");

            Assert.Contains(DesktopLayoutMarker, body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task api路徑不算手機頁面()
        {
            // /api/mobile/... 是 fetch 呼叫，回應由 api.js 依狀態碼處理、不會被當畫面顯示。
            // 把它也導到手機錯誤頁只是讓 JSON 解析失敗變成看不懂的訊息。
            // 這一支釘的是 IsMobilePath 的邊界：它比對的是 "/mobile/" 開頭，不是「路徑含 mobile」。
            var body = await GetBodyAsync("/api/mobile/nosuchendpoint");

            Assert.DoesNotContain(MobileHomeLink, body, StringComparison.Ordinal);
        }

        private async Task<string> GetBodyAsync(string url)
        {
            var client = _factory.CreateClient();
            var res = await client.GetAsync(url);

            // 狀態碼一併斷言：哪天路由變成有東西接住這些網址，這幾支測試就不再測到
            // 「404 走哪個版面」，而是安靜地測了別的東西。
            Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);

            return await res.Content.ReadAsStringAsync();
        }
    }
}
