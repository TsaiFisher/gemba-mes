using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 斷線保護（mobile-pwa.md §5.2）的手機端行為——人工驗收清單 W-2～W-5 的自動版。
    ///
    /// <para>以 Playwright 的假時鐘取代「真的等 5／30 分鐘」：按下送出時記的是頁面時鐘，
    /// 伺服器只用「送出時間 − 按下時間」回推發生時間（見 MobileOccurredAt），所以假時鐘的絕對值不重要，
    /// 重要的是兩者的差。伺服器半段（差值換算成 ReportedAt／EndAt）在整合測試 MobileMfgReportingTests。</para>
    ///
    /// <para>與 <see cref="MobileReportPageTests"/> 同一套做法：<c>/api/**</c> 全換假資料、封鎖 Service Worker，
    /// 不碰資料庫，只要有東西在供應 wwwroot 就能跑。</para>
    /// </summary>
    public class MobileDisconnectProtectionTests : IAsyncLifetime
    {
        private const string ReportPage = "/mobile/mfg/reports.html?dispatchId=D1&sessionId=S1";
        private const string HomePage   = "/mobile/home.html";

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

        // ── W-2：斷線報工，恢復連線後自動補送，帶「按下的時間」──────────────────────

        [Fact]
        public async Task W2_斷線送兩筆報工_恢復連線自動補送_各帶按下時間且不重複()
        {
            await using var h = await OpenAsync(ReportPage);
            await h.Context.SetOfflineAsync(true);

            await SubmitReportAsync(h, "5");
            await h.Page.Clock.FastForwardAsync("01:00");
            await SubmitReportAsync(h, "7");
            Assert.Empty(h.Posts);
            Assert.Equal(2, await PendingCountAsync(h));

            await h.Page.Clock.FastForwardAsync("05:00");   // 斷線 5 分鐘以上
            await h.Context.SetOfflineAsync(false);          // 'online' 事件 → 自動補送，不按任何按鈕
            await WaitForPostsAsync(h, 2);

            var reports = h.Posts.Where(p => p.Path == "/api/mobile/reports").ToList();
            Assert.Equal(2, reports.Count);
            Assert.Equal(2, reports.Select(r => r.Body.GetProperty("clientId").GetString()).Distinct().Count());
            Assert.Equal(new[] { 5m, 7m }, reports.Select(r => r.Body.GetProperty("qtyOk").GetDecimal()));
            foreach (var r in reports)
            {
                // 送出時間 − 按下時間＝排隊多久；伺服器據此把 ReportedAt 記回按下的那一刻
                var queued = r.SentAt!.Value - r.Body.GetProperty("clientTimestamp").GetDateTimeOffset();
                Assert.True(queued >= TimeSpan.FromMinutes(5), $"排隊 {queued}，應 ≥ 5 分鐘");
            }
            await WaitForQueueDrainedAsync(h);
            Assert.Empty(h.Errors);
        }

        // ── W-3：手機有網路、伺服器連不到（Tunnel 回 502）也要暫存，恢復後 1 分鐘內自動送 ──

        [Fact]
        public async Task W3_網路正常但伺服器回502_暫存不丟_伺服器恢復後一分鐘內自動送出()
        {
            await using var h = await OpenAsync(ReportPage);
            h.ServerDown = true;                              // navigator.onLine 仍為 true

            await SubmitReportAsync(h, "3");
            Assert.Equal(1, await PendingCountAsync(h));
            Assert.Equal(0, h.Posts.Count(p => p.Status == 200));

            await h.Page.Clock.FastForwardAsync("02:00");   // 伺服器停 2 分鐘
            h.ServerDown = false;
            await h.Page.Clock.RunForAsync(61_000);         // 沒有 'online' 事件，靠每分鐘重試
            await WaitForPostsAsync(h, 1);

            var ok = Assert.Single(h.Posts, p => p.Status == 200);
            var queued = ok.SentAt!.Value - ok.Body.GetProperty("clientTimestamp").GetDateTimeOffset();
            Assert.True(queued >= TimeSpan.FromMinutes(2), $"排隊 {queued}，發生時間要回推到按下的那一刻");
            await WaitForQueueDrainedAsync(h);
        }

        // ── W-4：排隊超過 30 分鐘不自動送，首頁標「待確認」，確認後才送 ──────────────

        [Fact]
        public async Task W4_排隊超過30分鐘_恢復連線也不自動送_首頁待確認_確認後才送出()
        {
            await using var h = await OpenAsync(ReportPage);
            await h.Context.SetOfflineAsync(true);
            await SubmitReportAsync(h, "4");

            await h.Page.Clock.FastForwardAsync("31:00");
            await h.Context.SetOfflineAsync(false);
            await h.Page.Clock.RunForAsync(61_000);
            await WaitForStatusAsync(h, "held");
            Assert.Empty(h.Posts.Where(p => p.Path == "/api/mobile/reports"));

            await OpenQueuePanelAsync(h);
            var confirm = h.Page.Locator("#queueList [data-action='confirmHeld']");
            await confirm.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            Assert.Equal(1, await confirm.CountAsync());
            Assert.True(await h.Page.Locator("#queueList .text-danger").CountAsync() > 0, "待確認項目要以紅字標示並說明原因");

            await confirm.ClickAsync();
            await WaitForPostsAsync(h, 1);
            var post = Assert.Single(h.Posts, p => p.Path == "/api/mobile/reports");
            var queued = post.SentAt!.Value - post.Body.GetProperty("clientTimestamp").GetDateTimeOffset();
            Assert.True(queued >= TimeSpan.FromMinutes(31), $"排隊 {queued}：確認後送出仍要帶按下的時間，工時不能多算");
            Assert.Empty(h.Errors);
        }

        // ── W-5：捨棄待確認的項目 ───────────────────────────────────────────────

        [Fact]
        public async Task W5_捨棄待確認項目_要先確認_確定後消失且從未送出()
        {
            await using var h = await OpenAsync(ReportPage);
            await h.Context.SetOfflineAsync(true);
            await SubmitReportAsync(h, "9");
            await h.Page.Clock.FastForwardAsync("31:00");
            await h.Context.SetOfflineAsync(false);
            await h.Page.Clock.RunForAsync(61_000);
            await WaitForStatusAsync(h, "held");

            await OpenQueuePanelAsync(h);
            var discard = h.Page.Locator("#queueList [data-action='discardItem']");
            await discard.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            string? dialogText = null;
            h.Page.Dialog += async (_, d) => { dialogText = d.Message; await d.AcceptAsync(); };
            await discard.ClickAsync();
            Assert.True(await PagePoll.UntilAsync(h.Page, "async () => (await offlineQueue.getAll()).length === 0", 5_000),
                "捨棄後佇列沒有清空");

            Assert.False(string.IsNullOrWhiteSpace(dialogText));                    // 跳了確認對話框
            Assert.Equal(0, await h.Page.Locator("#queueList [data-action='discardItem']").CountAsync());
            Assert.Empty(h.Posts.Where(p => p.Path == "/api/mobile/reports"));     // 伺服器從未收到
            Assert.Empty(h.Errors);
        }

        // ── 輔助 ─────────────────────────────────────────────────────────────

        private sealed record Post(string Path, JsonElement Body, DateTimeOffset? SentAt, int Status);

        private sealed class Harness : IAsyncDisposable
        {
            public required IBrowserContext Context { get; init; }
            public required IPage Page { get; init; }
            public List<Post> Posts { get; } = new();
            public List<string> Errors { get; } = new();
            /// <summary>true＝寫入端點回 502（像 Tunnel 後面的 app 停了），讀取照常。</summary>
            public bool ServerDown { get; set; }
            public ValueTask DisposeAsync() => new(Context.CloseAsync());
        }

        /// <summary>回首頁：待確認的徽章要是紅色；點徽章開出待送出清單（offcanvas）。</summary>
        private async Task OpenQueuePanelAsync(Harness h)
        {
            await h.Page.GotoAsync(_baseUrl + HomePage, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var badge = h.Page.Locator("#offlineBtn button");
            await badge.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            Assert.Contains("btn-danger", await badge.GetAttributeAsync("class") ?? "");
            await badge.ClickAsync();
        }

        private static async Task SubmitReportAsync(Harness h, string qtyOk)
        {
            var before = await h.Page.EvaluateAsync<int>("async () => (await offlineQueue.getAll()).length");
            await h.Page.FillAsync("#qtyOk", qtyOk);
            await h.Page.ClickAsync("#btnSubmit");
            Assert.True(await PagePoll.UntilAsync(h.Page, $"async () => (await offlineQueue.getAll()).length > {before}", 5_000),
                "送出後沒有排進佇列。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        /// <summary>
        /// 等佇列清空。不能在 <see cref="WaitForPostsAsync"/> 之後立刻讀 <see cref="PendingCountAsync"/>：
        /// 那邊數的是「請求打到攔截器」，頁面還要等回應回來才把該筆移出佇列——中間差一個非同步回合，
        /// 機器忙時就讀到 1（全套跑時依執行順序時紅時綠，單獨跑幾乎都綠）。
        /// </summary>
        private static async Task WaitForQueueDrainedAsync(Harness h)
        {
            if (await PagePoll.UntilAsync(h.Page, "async () => (await offlineQueue.getPending()).length === 0", 5_000)) return;
            Assert.Fail($"送出成功後佇列沒有清空（仍有 {await PendingCountAsync(h)} 筆待送）。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        private static Task<int> PendingCountAsync(Harness h)
            => h.Page.EvaluateAsync<int>("async () => (await offlineQueue.getPending()).length");

        private static async Task WaitForStatusAsync(Harness h, string status)
        {
            if (await PagePoll.UntilAsync(h.Page, $"async () => (await offlineQueue.getAll()).some(r => r.status === '{status}')")) return;
            var all = await h.Page.EvaluateAsync<string>("async () => JSON.stringify((await offlineQueue.getAll()).map(r => r.status))");
            Assert.Fail($"佇列沒有出現 {status}：{all}。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        private static async Task WaitForPostsAsync(Harness h, int okCount)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (h.Posts.Count(p => p.Status == 200 && p.Path == "/api/mobile/reports") >= okCount) return;
                await Task.Delay(100);
            }
            Assert.Fail($"沒有送出 {okCount} 筆報工（目前 {h.Posts.Count(p => p.Status == 200)}）。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        private async Task<Harness> OpenAsync(string path)
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
                var p = new Uri(req.Url).AbsolutePath;
                if (req.Method != "GET")
                {
                    var status = h.ServerDown ? 502 : 200;
                    DateTimeOffset? sentAt = req.Headers.TryGetValue("x-client-sent-at", out var v) ? DateTimeOffset.Parse(v) : null;
                    var body = string.IsNullOrEmpty(req.PostData) ? JsonDocument.Parse("{}").RootElement
                                                                  : JsonDocument.Parse(req.PostData).RootElement.Clone();
                    h.Posts.Add(new Post(p, body, sentAt, status));
                    if (h.ServerDown)
                        return route.FulfillAsync(new RouteFulfillOptions { Status = 502, ContentType = "text/html", Body = "Bad Gateway" });
                }
                var json = p switch
                {
                    "/api/mobile/auth/me"    => FakeUserJson,
                    "/api/mobile/dispatches" => FakeDispatchJson,
                    _                        => "{\"success\":true,\"data\":[]}",
                };
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = json });
            });
            await MobileFakeApi.IgnoreTelemetryAsync(context);   // 遙測不算寫入（見 MobileFakeApi）

            page.PageError += (_, message) => h.Errors.Add("pageerror: " + message);
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error" && !msg.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                    h.Errors.Add("console.error: " + msg.Text);
            };

            // 假時鐘要在頁面腳本載入前裝好，offline-queue.js 的每分鐘重試計時器才會跟著它走
            await page.Clock.InstallAsync();
            await page.GotoAsync(_baseUrl + path,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            await page.Locator("#progressInfo .progress-bar").WaitForAsync();
            return h;
        }
    }
}
