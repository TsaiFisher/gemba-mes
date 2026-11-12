using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機報工頁按下送出之後的路徑：線上送出、離線排隊、負數擋下。
    ///
    /// <see cref="MobilePageScriptSmokeTests"/> 只驗到「載入」。2026-09-27 報工頁的第二個錯
    /// ——區域變數 <c>t</c> 遮蔽了 i18n 的 <c>t()</c>——只在離線送出、要畫「待同步」徽章時才炸，
    /// 載入測試永遠看不到。這裡就是去走那條路。
    ///
    /// 與載入測試同一套做法：<c>/api/**</c> 全換假資料、封鎖 Service Worker，不登入、不碰資料庫，
    /// 只要有東西在供應 wwwroot 就能跑。
    /// </summary>
    public class MobileReportPageTests : IAsyncLifetime
    {
        private const string PagePath = "/mobile/mfg/reports.html?dispatchId=D1&sessionId=S1";

        private static readonly string FakeUserJson = JsonSerializer.Serialize(new
        {
            id = "smoke-user", name = "smoke", displayName = "Smoke", activeRoleId = "r1",
            plantId = "P1", roles = new[] { "Admin" }
        });

        private static readonly string FakeDispatchJson = JsonSerializer.Serialize(new
        {
            success = true,
            data = new[] { new { dispatchId = "D1", workOrderId = "WO1", operationSeq = 10, quantity = 100, qtyDone = 10 } }
        });

        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;
        private string _baseUrl = null!;

        public async Task InitializeAsync()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.test.json", optional: true)
                .Build();
            _baseUrl = (Environment.GetEnvironmentVariable("BaseUrl") ?? config["BaseUrl"] ?? "http://localhost:5110")
                .TrimEnd('/');

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        public async Task DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }

        [Fact]
        public async Task 線上報工_送出一筆並列入本頁紀錄()
        {
            await using var h = await OpenAsync();

            await h.Page.FillAsync("#qtyOk", "5");
            await h.Page.FillAsync("#qtyNg", "1");
            await h.Page.ClickAsync("#btnSubmit");
            await WaitForHistoryCountAsync(h, 1);

            var post = Assert.Single(h.Posts);
            Assert.Equal("/api/mobile/reports", post.Path);
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("S1", body.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(5, body.RootElement.GetProperty("qtyOk").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("qtyNg").GetInt32());
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("clientId").GetString()));

            // 送出後表單要歸零，否則現場連按兩次會重複報同一批
            Assert.Equal("0", await h.Page.InputValueAsync("#qtyOk"));
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 小數數量原值送出不得被截斷()
        {
            // 曾用 parseInt 讀數量：12.5 公斤送出去變 12，畫面與伺服器都不會報錯
            await using var h = await OpenAsync();

            await h.Page.FillAsync("#qtyOk", "12.5");
            await h.Page.FillAsync("#qtyNg", "0.25");
            await h.Page.ClickAsync("#btnSubmit");
            await WaitForHistoryCountAsync(h, 1);

            using var body = JsonDocument.Parse(Assert.Single(h.Posts).Body);
            Assert.Equal(12.5m, body.RootElement.GetProperty("qtyOk").GetDecimal());
            Assert.Equal(0.25m, body.RootElement.GetProperty("qtyNg").GetDecimal());
        }

        [Fact]
        public async Task 離線報工_排進佇列且紀錄顯示待同步徽章()
        {
            await using var h = await OpenAsync();
            await h.Context.SetOfflineAsync(true);

            await h.Page.FillAsync("#qtyOk", "5");
            await h.Page.ClickAsync("#btnSubmit");
            await WaitForHistoryCountAsync(h, 1);

            // 徽章要以翻譯後的文字畫出來：t() 被遮蔽時這一步丟 TypeError，紀錄清單留空
            // 筆數在畫清單之前就更新了，所以要另外確認清單真的畫出來；同一個函式同步執行，不必再等
            var badges = h.Page.Locator("#historyList .badge");
            Assert.True(await badges.CountAsync() == 1,
                "本頁紀錄沒有畫出待同步徽章。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
            var badge = await badges.TextContentAsync();
            Assert.False(string.IsNullOrWhiteSpace(badge));
            Assert.DoesNotContain("report.", badge);

            var queued = await h.Page.EvaluateAsync<string[]>(
                "async () => (await offlineQueue.getPending()).map(p => p.url)");
            Assert.Equal(new[] { "/api/mobile/reports" }, queued);
            Assert.Empty(h.Posts);
            Assert.Empty(h.Errors);
        }

        [Theory]
        [InlineData("-5", "1")]
        [InlineData("5", "-1")]
        public async Task 任一數量為負_不送出也不排隊(string qtyOk, string qtyNg)
        {
            // input 的 min="0" 只在表單送出時生效，這頁不走表單，要靠腳本自己擋
            await using var h = await OpenAsync();

            await h.Page.FillAsync("#qtyOk", qtyOk);
            await h.Page.FillAsync("#qtyNg", qtyNg);
            await h.Page.ClickAsync("#btnSubmit");
            await h.Page.Locator("#toast-container .toast").First.WaitForAsync();

            Assert.Empty(h.Posts);
            Assert.Equal("0", await h.Page.Locator("#historyCount").TextContentAsync());
            Assert.Equal(0, await h.Page.EvaluateAsync<int>("async () => (await offlineQueue.getPending()).length"));
            Assert.Empty(h.Errors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private sealed record Post(string Path, string Body);

        private sealed class Harness : IAsyncDisposable
        {
            public required IBrowserContext Context { get; init; }
            public required IPage Page { get; init; }
            public List<Post> Posts { get; } = new();
            public List<string> Errors { get; } = new();
            public ValueTask DisposeAsync() => new(Context.CloseAsync());
        }

        /// <summary>
        /// 等紀錄筆數到位；等不到時把頁面錯誤直接印出來——筆數沒變的原因幾乎都是畫紀錄那段丟了例外，
        /// 只報逾時的話還得自己重跑去找。
        /// </summary>
        private static async Task WaitForHistoryCountAsync(Harness h, int count)
        {
            try
            {
                await h.Page.WaitForFunctionAsync(
                    $"() => document.getElementById('historyCount').textContent === '{count}'",
                    null, new PageWaitForFunctionOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                Assert.Fail($"送出後本頁紀錄沒有變成 {count} 筆。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
            }
        }

        private async Task<Harness> OpenAsync()
        {
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });
            var page = await context.NewPageAsync();
            var h = new Harness { Context = context, Page = page };

            await context.RouteAsync("**/api/**", route =>
            {
                var req = route.Request;
                var path = new Uri(req.Url).AbsolutePath;
                if (req.Method != "GET") h.Posts.Add(new Post(path, req.PostData ?? ""));
                var body = path switch
                {
                    "/api/mobile/auth/me"    => FakeUserJson,
                    "/api/mobile/dispatches" => FakeDispatchJson,
                    _                        => "{\"success\":true,\"data\":[]}",
                };
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = body });
            });
            await MobileFakeApi.IgnoreTelemetryAsync(context);   // 遙測不算寫入（見 MobileFakeApi）

            page.PageError += (_, message) => h.Errors.Add("pageerror: " + message);
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error" && !msg.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                    h.Errors.Add("console.error: " + msg.Text);
            };

            await page.GotoAsync(_baseUrl + PagePath,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            // 派工進度畫出來＝初始化整段跑完（requireAuth → offlineQueue.ready → loadDispatch）
            await page.Locator("#progressInfo .progress-bar").WaitForAsync();
            return h;
        }
    }
}
