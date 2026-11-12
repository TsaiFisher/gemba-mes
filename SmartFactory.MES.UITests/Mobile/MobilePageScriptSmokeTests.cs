using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機每一頁載入時，頁面腳本都必須真的跑起來。
    ///
    /// 2026-09-27 手機實測：<c>mfg/reports.page.js</c> 自 2026-08-12 雙語化起有語法錯誤，
    /// 整支不執行——報工頁永遠「載入中」、按鈕全死，六週沒人發現。當時唯一打開報工頁的
    /// UI 測試（<see cref="MobilePwaOfflineTests"/>）只斷言 <c>&lt;title&gt;</c> 含「報工」：
    /// HTML 照常送出，所以標題永遠對，**腳本死了照樣綠**。這裡斷言的是腳本層的訊號。
    ///
    /// 收集的四種錯誤都是「畫面上看不出來、只在主控台留一行」的形狀：
    /// 未捕捉例外（含語法錯誤）、console.error、靜態資源 404、data-action 找不到函式。
    ///
    /// 刻意**不掛** <see cref="UiTestFixture"/>：那個 fixture 啟動就要登入真的 app，
    /// 而這裡把 /api/** 全數換成假資料，不需要登入、也不碰資料庫——
    /// 只要有東西在供應 wwwroot 就能跑，main 與分支可以直接對照。
    /// Service Worker 一律封鎖：SW 發出的請求不經過 page.route，會繞過假資料打到真後端。
    ///
    /// 限制：只驗到「載入」。按下送出之後的路徑每頁不同，不在這支的範圍。
    /// </summary>
    public class MobilePageScriptSmokeTests : IAsyncLifetime
    {
        // 各頁需要的參數聯集；不帶的話 reports／sessions 等頁會在讀參數時提早 return，
        // 後半段初始化根本沒跑到，等於沒驗。
        private const string QueryString =
            "?dispatchId=D1&sessionId=S1&workOrderId=WO1&operationSeq=10&machineCode=M1&id=1";

        private static readonly string FakeUserJson = JsonSerializer.Serialize(new
        {
            id = "smoke-user", name = "smoke", displayName = "Smoke", activeRoleId = "r1",
            plantId = "P1", roles = new[] { "Admin" }
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
            // 與 UiTestFixture 同一個解析順序：nightly.yml 以環境變數傳 BaseUrl。
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

        /// <summary>repo 內 wwwroot/mobile 底下的每一個 HTML，以網址路徑表示。新頁面自動納入。</summary>
        public static IEnumerable<object[]> MobilePages() =>
            Directory.EnumerateFiles(MobileDir(), "*.html", SearchOption.AllDirectories)
                .Select(f => "/mobile/" + Path.GetRelativePath(MobileDir(), f).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => new object[] { p });

        private static string MobileDir()
        {
            // 測試從 bin/<組態>/net10.0 執行；往上找到含 wwwroot/mobile 的 repo 根目錄。
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "wwwroot", "mobile");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("往上找不到 wwwroot/mobile，無法列舉手機頁面。");
        }

        [Fact]
        public void 頁面清單沒有縮水()
        {
            // MemberData 列舉失敗或目錄搬走時，Theory 會變成零筆而安靜地全綠。
            var pages = MobilePages().Select(r => (string)r[0]).ToList();
            Assert.True(pages.Count >= 20, $"只列舉到 {pages.Count} 頁：\n  " + string.Join("\n  ", pages));
            Assert.Contains("/mobile/mfg/reports.html", pages);
        }

        [Theory]
        [MemberData(nameof(MobilePages))]
        public async Task 頁面載入時腳本不得出錯(string pagePath)
        {
            var errors = await LoadAndCollectErrorsAsync(pagePath);
            Assert.True(errors.Count == 0,
                $"{pagePath} 載入時出現腳本層錯誤（畫面上通常看不出來，只會卡在載入中或按鈕沒反應）：\n  "
                + string.Join("\n  ", errors));
        }

        [Fact]
        public async Task 偵測器本身抓得到語法錯誤()
        {
            // 收集錯誤的接線一旦被改壞（事件名、過濾條件），上面整批會永遠綠。
            // 這裡把報工頁的腳本換成一個語法錯誤，斷言一定收得到。
            var errors = await LoadAndCollectErrorsAsync("/mobile/mfg/reports.html",
                ctx => ctx.RouteAsync("**/mobile/mfg/reports.page.js", r => r.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/javascript",
                    Body = "const s = '<b>${t('x')}</b>';"
                })));

            Assert.Contains(errors, e => e.StartsWith("pageerror", StringComparison.Ordinal));
        }

        private async Task<List<string>> LoadAndCollectErrorsAsync(
            string pagePath, Func<IBrowserContext, Task>? extraRoutes = null)
        {
            await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ServiceWorkers = ServiceWorkerPolicy.Block,
            });

            await context.RouteAsync("**/api/**", route =>
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                var body = path == "/api/mobile/auth/me" ? FakeUserJson : "{\"success\":true,\"data\":[]}";
                return route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = body });
            });
            await MobileFakeApi.IgnoreTelemetryAsync(context);   // 遙測不算寫入（見 MobileFakeApi）
            if (extraRoutes != null) await extraRoutes(context);

            var errors = new List<string>();
            var page = await context.NewPageAsync();
            page.PageError += (_, message) => errors.Add("pageerror: " + FirstLine(message));
            page.Console += (_, msg) =>
            {
                // 資源 404 由下面的 Response 事件點名路徑，這裡不重複記。
                if (msg.Type == "error" && !msg.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                    errors.Add("console.error: " + FirstLine(msg.Text));
                if (msg.Text.Contains("[data-action]", StringComparison.Ordinal))
                    errors.Add("data-action: " + FirstLine(msg.Text));
            };
            page.Response += (_, resp) =>
            {
                if (resp.Status == 404 && !resp.Url.Contains("/api/", StringComparison.Ordinal))
                    errors.Add("404: " + new Uri(resp.Url).AbsolutePath);
            };

            await page.GotoAsync(_baseUrl + pagePath + QueryString,
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            // 各頁初始化是 async IIFE，networkidle 之後仍可能有一兩個微任務在跑。
            await page.WaitForTimeoutAsync(500);

            return errors.Distinct().ToList();
        }

        private static string FirstLine(string s)
        {
            var i = s.IndexOf('\n');
            return i < 0 ? s : s[..i];
        }
    }
}
