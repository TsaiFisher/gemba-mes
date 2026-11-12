using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Docs
{
    /// <summary>
    /// 手冊全文搜尋的瀏覽器層守門。
    ///
    /// <para><b>刻意不進 "UI Tests" collection、不連 :5110。</b>手冊是靜態檔，
    /// 搜尋不需要登入、不需要資料庫、不需要 app 跑起來——把它掛到共用 app 上，
    /// 只會讓「別的 session 正在重啟 app」變成這支測試的假紅，
    /// 而它測的東西跟那個 app 一點關係也沒有。這裡自備一個 loopback 靜態檔伺服器。</para>
    ///
    /// <para><b>與 R40 互補不重複</b>：R40 是靜態掃描，答的是「每頁都接上資產了嗎」；
    /// 這裡答的是「接上之後它真的會搜、會跳、會高亮嗎」。兩者都綠才代表功能在。</para>
    /// </summary>
    public class ManualSearchUiTests : IAsyncLifetime
    {
        private HttpListener _listener = null!;
        private CancellationTokenSource _cts = null!;
        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;
        private string _baseUrl = null!;
        private string _wwwroot = null!;

        public async Task InitializeAsync()
        {
            _wwwroot = FindWwwroot();

            var port = FreePort();
            _baseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl + "/");
            _listener.Start();

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ServeAsync(_cts.Token));

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }

        public async Task DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _browser.CloseAsync();
            _playwright.Dispose();
        }

        // ── 測試 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task 每頁的導覽列都注入搜尋框()
        {
            var page = await _browser.NewPageAsync();

            // 首頁與內頁的資產相對深度不同（doc-search.js vs ../doc-search.js），
            // 只測其中一種等於沒測到另一種——而使用者多半直接落在內頁。
            foreach (var path in new[] { "/docs/user/index.html", "/docs/user/bas/product.html", "/docs/user/mobile/index.html" })
            {
                await page.GotoAsync(_baseUrl + path);
                var box = page.Locator("nav.navbar .doc-search-input");
                Assert.True(await box.CountAsync() == 1, $"{path} 的導覽列沒有搜尋框");
            }
        }

        [Fact]
        public async Task 搜尋中文詞會列出結果且連結指向對應頁()
        {
            var page = await _browser.NewPageAsync();
            await page.GotoAsync(_baseUrl + "/docs/user/index.html");

            await page.FillAsync(".doc-search-input", "產品主檔");
            await page.WaitForSelectorAsync(".doc-search-link");

            var links = await page.Locator(".doc-search-link").AllAsync();
            Assert.True(links.Count > 0, "搜尋「產品主檔」沒有任何結果");

            var hrefs = new List<string>();
            foreach (var link in links) hrefs.Add(await link.GetAttributeAsync("href") ?? "");

            Assert.Contains(hrefs, h => h.Contains("bas/product.html", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 點擊結果會跳到段落錨點並高亮命中詞()
        {
            var page = await _browser.NewPageAsync();
            await page.GotoAsync(_baseUrl + "/docs/user/index.html");

            await page.FillAsync(".doc-search-input", "欄位說明");
            await page.WaitForSelectorAsync(".doc-search-link");

            // 挑一筆帶錨點的結果——頁首塊沒有錨點是正常的，拿它測跳段落會誤判。
            var anchored = page.Locator(".doc-search-link[href*='#']").First;
            Assert.True(await anchored.CountAsync() > 0, "沒有任何一筆結果帶段落錨點");

            var href = await anchored.GetAttributeAsync("href") ?? "";
            await anchored.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var anchor = href[(href.IndexOf('#') + 1)..];
            Assert.Equal(anchor, page.Url[(page.Url.IndexOf('#') + 1)..]);

            // 錨點必須真的存在於落地頁——不存在的話瀏覽器靜默留在頁首，這正是要擋的失敗。
            Assert.True(await page.Locator($"#{anchor}").CountAsync() == 1,
                $"落地頁沒有 id=\"{anchor}\" 的段落");

            await page.WaitForSelectorAsync("mark.doc-search-hit");
            Assert.True(await page.Locator("mark.doc-search-hit").CountAsync() > 0, "落地後沒有任何命中詞被高亮");

            // 跳過去還不夠，要真的看得到。手冊每頁都有 loading="lazy" 的截圖，
            // 在它們撐開版面之前捲動，目標會被推到畫面外——修正前實測停在 619px（視窗 800），
            // 修正後 140px。門檻放在視窗上半部：夠寬鬆以容納「段落太靠文件底部、捲不到頂」
            // 的正常情形，也夠嚴格以抓住那個回歸。
            await page.WaitForLoadStateAsync(LoadState.Load);
            await page.WaitForTimeoutAsync(300);

            var headingTop = await page.EvaluateAsync<int>(
                "a => Math.round(document.getElementById(a).getBoundingClientRect().top)", anchor);
            var viewport = page.ViewportSize!.Height;

            Assert.True(headingTop >= -10 && headingTop <= viewport / 2,
                $"落地後目標段落在 {headingTop}px（視窗高 {viewport}），不在畫面上半部——" +
                "多半是延遲載入的截圖在捲動之後才撐開版面");
        }

        [Fact]
        public async Task 英文模式只會搜到英文內容()
        {
            var page = await _browser.NewPageAsync();
            await page.GotoAsync(_baseUrl + "/docs/user/index.html?lang=en");

            await page.FillAsync(".doc-search-input", "purchase order");
            await page.WaitForSelectorAsync(".doc-search-link");

            var headings = await page.Locator(".doc-search-heading").AllInnerTextsAsync();
            Assert.True(headings.Count > 0, "英文模式搜尋 \"purchase order\" 沒有結果");

            // 中英同頁並存，索引沒分語系的話這裡會撈到中文標題——
            // 而搜尋框本身看起來完全正常，只是英文使用者拿到中文引文。
            var chinese = headings.Where(h => h.Any(c => c >= 0x4E00 && c <= 0x9FFF)).ToList();
            Assert.True(chinese.Count == 0,
                $"英文模式出現 {chinese.Count} 筆中文結果：{string.Join(" / ", chinese.Take(3))}");
        }

        [Fact]
        public async Task 結果連結在英文模式會帶著語系參數()
        {
            var page = await _browser.NewPageAsync();
            await page.GotoAsync(_baseUrl + "/docs/user/index.html?lang=en");

            await page.FillAsync(".doc-search-input", "product");
            await page.WaitForSelectorAsync(".doc-search-link");

            var href = await page.Locator(".doc-search-link").First.GetAttributeAsync("href") ?? "";

            // doc-i18n.js 的 decorateLinks 只處理 DOM 裡既有的連結，看不到動態產生的結果連結；
            // 掉了 lang=en 的症狀是英文讀者點進去看到整頁中文。
            Assert.Contains("lang=en", href);
        }

        // ── 自備的靜態檔伺服器 ───────────────────────────────────────────

        private static string FindWwwroot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "wwwroot", "docs", "user");
                if (Directory.Exists(candidate)) return Path.Combine(dir.FullName, "wwwroot");
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("找不到 wwwroot——測試需要真實手冊檔案");
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }   // Stop() 之後 GetContextAsync 會擲出，那就是收工訊號

                try
                {
                    var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath).TrimStart('/');
                    var full = Path.GetFullPath(Path.Combine(_wwwroot, rel.Replace('/', Path.DirectorySeparatorChar)));

                    // 路徑跳脫防護：測試伺服器也不該能被誘導去讀 wwwroot 以外的檔案。
                    if (!full.StartsWith(_wwwroot, StringComparison.Ordinal) || !File.Exists(full))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    ctx.Response.ContentType = ContentTypeOf(full);
                    var bytes = await File.ReadAllBytesAsync(full, ct);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* 客戶端已離開 */ }
                }
            }
        }

        private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
