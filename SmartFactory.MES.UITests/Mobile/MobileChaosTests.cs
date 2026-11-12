using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 斷線保護的混沌邊界：W-2～W-5（<see cref="MobileDisconnectProtectionTests"/>）驗的是「乾淨地斷、乾淨地回來」，
    /// 這裡驗現場真正難纏的那幾種——回應在路上掉了、訊號弱到請求送出去卻永遠等不到回應、補送途中時好時壞、
    /// 補送途中工作階段過期、同一支手機兩個分頁一起補送。
    ///
    /// <para><b>判準只有一條：每一筆按下去的動作，伺服器最後恰好處理一次，而且手機上沒有東西卡住或被當成失敗。</b>
    /// 假伺服器照真伺服器的語意依 clientId 去重（同一個 clientId 第二次回 200 與第一次的結果），
    /// 所以「收到幾次」可以大於一，「處理了幾筆」必須等於按下的筆數。真伺服器在兩個同 clientId 請求
    /// 同時處理時的行為，由整合測試 MobileDuplicateInFlightTests 守。</para>
    ///
    /// <para>與 W 系列同一套做法：<c>/api/**</c> 全換假資料、封鎖 Service Worker、假時鐘，不碰資料庫。</para>
    /// </summary>
    public class MobileChaosTests : IAsyncLifetime
    {
        private const string ReportPage = "/mobile/mfg/reports.html?dispatchId=D1&sessionId=S1";

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

        // ── 1. 伺服器處理完了，回應在路上掉了 ───────────────────────────────────────

        [Fact]
        public async Task 回應在路上遺失_手機轉排隊重送_伺服器只處理一次_畫面不報失敗()
        {
            await using var h = await OpenAsync();
            h.Fault = Fault.LoseResponseOnce;           // 處理完才斷：最容易「做了兩次」的情境

            await SubmitReportAsync(h, "5");
            await WaitForQueuedAsync(h, 1);              // 連不上的那一刻轉排隊，不是報錯
            await AssertProcessedAsync(h, 1);
            Assert.Empty(h.DangerToasts);

            await h.Page.Clock.RunForAsync(61_000);      // 每分鐘重試
            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 1);           // 重送被去重，不是第二筆
            Assert.True(h.ReceivedCount >= 2, "重送沒有發生——本測試沒有驗到去重。");
            Assert.Empty(h.Errors);
        }

        // ── 2. 訊號弱到請求送出去卻永遠等不到回應 ──────────────────────────────────

        [Fact]
        public async Task 送出後一直等不到回應_逾時轉排隊_按鈕不會一直轉圈_恢復後補送()
        {
            await using var h = await OpenAsync();
            h.Fault = Fault.HangOnce;                    // 第一個寫入請求永遠不回

            await h.Page.FillAsync("#qtyOk", "6");
            await h.Page.ClickAsync("#btnSubmit");
            await h.Page.Clock.RunForAsync(31_000);      // 超過逾時
            await WaitForQueuedAsync(h, 1);

            await h.Page.Clock.RunForAsync(61_000);
            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 1);
            Assert.Empty(h.Errors);
        }

        [Fact]
        public async Task 補送時卡在某一筆不回_逾時後放下_下一輪照常送完_不會整個佇列停擺()
        {
            await using var h = await OpenAsync();
            await h.Context.SetOfflineAsync(true);
            await SubmitReportAsync(h, "1");
            await SubmitReportAsync(h, "2");
            await WaitForQueuedAsync(h, 2);

            h.Fault = Fault.HangOnce;
            await h.Context.SetOfflineAsync(false);      // 'online' → 補送，第一筆卡住
            await h.Page.Clock.RunForAsync(31_000);      // 逾時放下
            await h.Page.Clock.RunForAsync(61_000);      // 下一輪重試
            await h.Page.Clock.RunForAsync(61_000);
            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 2);
            Assert.Empty(h.Errors);
        }

        // ── 3. 補送途中時好時壞 ───────────────────────────────────────────────

        [Fact]
        public async Task 補送途中網路時好時壞_五筆最後各處理一次_沒有遺失沒有重複()
        {
            await using var h = await OpenAsync();
            await h.Context.SetOfflineAsync(true);
            for (var i = 1; i <= 5; i++) await SubmitReportAsync(h, i.ToString());
            await WaitForQueuedAsync(h, 5);

            h.Fault = Fault.Flaky;                       // 每三個請求：一個處理完掉回應、一個沒到伺服器、一個正常
            await h.Context.SetOfflineAsync(false);
            for (var round = 0; round < 15 && await PendingCountAsync(h) > 0; round++)
                await h.Page.Clock.RunForAsync(61_000);

            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 5);
            Assert.Equal(new[] { 1m, 2m, 3m, 4m, 5m }, h.ProcessedQty.OrderBy(q => q));
            Assert.Empty(h.Errors);
        }

        // ── 4. 補送途中工作階段過期 ─────────────────────────────────────────────

        [Fact]
        public async Task 補送途中工作階段過期_項目留在手機不丟_重新登入後補送完成()
        {
            await using var h = await OpenAsync();
            await h.Context.SetOfflineAsync(true);
            await SubmitReportAsync(h, "8");
            await SubmitReportAsync(h, "9");
            await WaitForQueuedAsync(h, 2);

            h.Fault = Fault.Unauthorized;                // Cookie 過期：寫入一律 401
            await h.Context.SetOfflineAsync(false);
            await h.Page.Clock.RunForAsync(61_000);
            await AssertProcessedAsync(h, 0);
            Assert.Equal(2, await PendingCountAsync(h)); // 401 不是失敗：留著等重新登入
            Assert.Equal(0, await StatusCountAsync(h, "error"));

            h.Fault = Fault.None;                        // 重新登入
            await h.Page.Clock.RunForAsync(61_000);
            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 2);
        }

        // ── 5. 同一支手機兩個分頁一起補送 ────────────────────────────────────────

        [Fact]
        public async Task 兩個分頁同時補送同一批_伺服器各處理一次_兩邊都不顯示失敗()
        {
            await using var h = await OpenAsync();
            var second = await h.Context.NewPageAsync();
            await second.Clock.InstallAsync();
            await second.GotoAsync(_baseUrl + ReportPage, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await h.Context.SetOfflineAsync(true);
            for (var i = 1; i <= 3; i++) await SubmitReportAsync(h, i.ToString());
            await WaitForQueuedAsync(h, 3);

            h.Fault = Fault.Slow;                        // 每個寫入慢一點，讓兩邊的補送交錯
            await h.Context.SetOfflineAsync(false);      // 兩個分頁同時收到 'online'
            for (var round = 0; round < 5 && await PendingCountAsync(h) > 0; round++)
            {
                await h.Page.Clock.RunForAsync(61_000);
                await second.Clock.RunForAsync(61_000);
            }

            await WaitForDrainedAsync(h);
            await AssertProcessedAsync(h, 3);
            Assert.Equal(0, await StatusCountAsync(h, "error"));
            Assert.Empty(h.Errors);
        }

        // ── 假伺服器與輔助 ─────────────────────────────────────────────────────

        private enum Fault { None, LoseResponseOnce, HangOnce, Flaky, Unauthorized, Slow }

        private sealed class Harness : IAsyncDisposable
        {
            public required IBrowserContext Context { get; init; }
            public required IPage Page { get; init; }
            public Fault Fault { get; set; }
            /// <summary>依 clientId 去重後真正處理過的報工（值＝良品數）。</summary>
            public ConcurrentDictionary<string, decimal> Processed { get; } = new();
            public int ProcessedCount => Processed.Count;
            public IEnumerable<decimal> ProcessedQty => Processed.Values;
            public int ReceivedCount;
            public int FlakyCounter;
            public List<string> Errors { get; } = new();
            public List<string> DangerToasts { get; } = new();
            public ValueTask DisposeAsync() => new(Context.CloseAsync());
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

            await context.RouteAsync("**/api/**", async route =>
            {
                var req = route.Request;
                var path = new Uri(req.Url).AbsolutePath;
                if (req.Method == "GET")
                {
                    var json = path switch
                    {
                        "/api/mobile/auth/me"    => FakeUserJson,
                        "/api/mobile/dispatches" => FakeDispatchJson,
                        _                        => "{\"success\":true,\"data\":[]}",
                    };
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = json });
                    return;
                }

                Interlocked.Increment(ref h.ReceivedCount);
                var body = JsonDocument.Parse(req.PostData ?? "{}").RootElement;
                var clientId = body.TryGetProperty("clientId", out var c) ? c.GetString() ?? "" : "";
                var qty = body.TryGetProperty("qtyOk", out var q) ? q.GetDecimal() : 0m;

                switch (h.Fault)
                {
                    case Fault.Unauthorized:
                        await route.FulfillAsync(new RouteFulfillOptions { Status = 401, ContentType = "application/json", Body = "{}" });
                        return;
                    case Fault.HangOnce:
                        h.Fault = Fault.None;
                        return;                                  // 永遠不回應：route 懸著，直到頁面自己放棄
                    case Fault.LoseResponseOnce:
                        h.Fault = Fault.None;
                        h.Processed.TryAdd(clientId, qty);       // 伺服器處理完了……
                        await route.AbortAsync("connectionreset"); // ……回應沒送到
                        return;
                    case Fault.Flaky:
                        switch (Interlocked.Increment(ref h.FlakyCounter) % 3)
                        {
                            case 1: h.Processed.TryAdd(clientId, qty); await route.AbortAsync("connectionreset"); return;
                            case 2: await route.AbortAsync("internetdisconnected"); return;
                        }
                        break;
                    case Fault.Slow:
                        await Task.Delay(300);
                        break;
                }

                h.Processed.TryAdd(clientId, qty);               // 去重：第二次起不再算一筆
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200, ContentType = "application/json",
                    Body = "{\"success\":true,\"data\":null,\"message\":\"ok\"}",
                });
            });
            await MobileFakeApi.IgnoreTelemetryAsync(context);   // 遙測不算寫入（見 MobileFakeApi）

            page.PageError += (_, message) => h.Errors.Add("pageerror: " + message);
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error" && !msg.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                    h.Errors.Add("console.error: " + msg.Text);
            };

            await page.Clock.InstallAsync();
            await page.GotoAsync(_baseUrl + ReportPage, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            await page.Locator("#progressInfo .progress-bar").WaitForAsync();
            // 紅色 toast（失敗訊息）一出現就記下來：「轉排隊」是藍／黃，不該出現紅的
            await page.ExposeFunctionAsync("__chaosDanger", (string text) => { h.DangerToasts.Add(text); return 0; });
            await page.EvaluateAsync("""
                () => new MutationObserver(ms => ms.forEach(m => m.addedNodes.forEach(n => {
                    if (n.nodeType === 1 && (n.matches?.('.text-bg-danger') || n.querySelector?.('.text-bg-danger')))
                        window.__chaosDanger(n.textContent.trim());
                }))).observe(document.getElementById('toast-container'), { childList: true, subtree: true })
                """);
            return h;
        }

        private static async Task SubmitReportAsync(Harness h, string qtyOk)
        {
            var before = await h.Page.EvaluateAsync<int>("async () => (await offlineQueue.getAll()).length");
            await h.Page.FillAsync("#qtyOk", qtyOk);
            await h.Page.ClickAsync("#btnSubmit");
            Assert.True(await PollAsync(h, $"async () => (await offlineQueue.getAll()).length > {before}", 5_000),
                $"送出後沒有排進佇列（離線時應該要）。頁面錯誤：{string.Join(" | ", h.Errors)}");
        }

        /// <param name="tick">每秒（實際時間）把假時鐘往前推一分鐘，讓「每分鐘重試」再跑一輪：前一輪補送若還在等回應，
        /// 那次計時觸發會因「正在送」而略過，只推一次會等不到下一輪。最多推 10 分鐘，遠低於 30 分鐘轉「待確認」的門檻。</param>
        private static Task<bool> PollAsync(Harness h, string predicateJs, int timeoutMs = 10_000, bool tick = false)
            => PagePoll.UntilAsync(h.Page, predicateJs, timeoutMs, tick ? () => h.Page.Clock.RunForAsync(61_000) : null);

        private static async Task WaitForQueuedAsync(Harness h, int count)
        {
            if (await PollAsync(h, $"async () => (await offlineQueue.getPending()).length >= {count}")) return;
            Assert.Fail($"沒有轉進佇列（待送 {await PendingCountAsync(h)}，伺服器處理 {h.ProcessedCount}、收到 {h.ReceivedCount}）。" +
                        "頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        /// <summary>每一筆都要是 sent：只看 pending 歸零的話，卡成 error／held 或被刪掉也會被當成送完。</summary>
        private static async Task WaitForDrainedAsync(Harness h)
        {
            if (await PollAsync(h, "async () => { const a = await offlineQueue.getAll(); return a.length > 0 && a.every(r => r.status === 'sent'); }", tick: true))
                return;
            var all = await h.Page.EvaluateAsync<string>("async () => JSON.stringify((await offlineQueue.getAll()).map(r => r.status + ':' + (r.errorMsg||'')))");
            Assert.Fail($"佇列沒有送完：{all}；伺服器處理 {h.ProcessedCount}、收到 {h.ReceivedCount}。頁面錯誤：\n  " + string.Join("\n  ", h.Errors));
        }

        /// <summary>伺服器依 clientId 去重後處理的筆數；不符時把頁面與佇列狀態一起印出來，免得只看到一個數字。</summary>
        private static async Task AssertProcessedAsync(Harness h, int expected)
        {
            if (h.ProcessedCount == expected) return;
            var all = await h.Page.EvaluateAsync<string>(
                "async () => JSON.stringify((await offlineQueue.getAll()).map(r => ({ s: r.status, e: r.errorMsg, q: r.body?.qtyOk })))");
            Assert.Fail($"伺服器處理 {h.ProcessedCount} 筆，應為 {expected}（收到 {h.ReceivedCount} 次）。頁面 {h.Page.Url}；佇列 {all}；" +
                        $"紅色訊息 [{string.Join(" | ", h.DangerToasts)}]；頁面錯誤 [{string.Join(" | ", h.Errors)}]");
        }

        private static Task<int> PendingCountAsync(Harness h)
            => h.Page.EvaluateAsync<int>("async () => (await offlineQueue.getPending()).length");

        private static Task<int> StatusCountAsync(Harness h, string status)
            => h.Page.EvaluateAsync<int>($"async () => (await offlineQueue.getAll()).filter(r => r.status === '{status}').length");
    }
}
