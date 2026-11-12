using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace SmartFactory.MES.UITests.Mobile
{
    /// <summary>
    /// 手機 PWA 的 Service Worker 離線行為守門（作業書 5-4 的「機器做得到」那一半）。
    ///
    /// 與 <c>wwwroot/mobile/js/__tests__</c> 的 jest 守門分工：那些用正規式掃 sw.js 的
    /// **原始碼**，釘的是「每個 caches.open 的名字都要列進 activate 的保留清單」這類
    /// 結構規則；這裡在**真的瀏覽器**裡註冊真的 SW、真的斷網，釘的是行為。
    ///
    /// 為什麼兩種都要：2026-09-02 修掉的 MASTER_CACHE 缺陷（宣告在 handleMasterCache
    /// 函式內 → activate 的保留清單看不到它 → 每次版本更新就把主檔快取整個刪掉）是
    /// **讀程式讀出來的，不是測出來的**。當時 jest 有兩支手機測試、Playwright 對 /mobile
    /// 的覆蓋是 0，所以沒有任何東西驗過「SW 實際上留下了什麼」。
    ///
    /// 這幾支都用 <see cref="UiTestFixture.NewAnonymousPageAsync"/> 開獨立 context：
    /// CacheStorage 與 SW 註冊都是 origin 級的，共用 context 會讓前一支測試的快取
    /// 變成後一支的前提，而順序在 xUnit 不保證。
    /// </summary>
    [Collection("UI Tests")]
    public class MobilePwaOfflineTests
    {
        private const string EntryPage    = "/mobile/index.html";
        private const string ReportsPage  = "/mobile/mfg/reports.html";
        private const string OfflinePage  = "/mobile/offline.html";
        private const string HomePage     = "/mobile/home.html";
        private const string MfgListPage  = "/mobile/mfg/list.html";
        // v30 起 API 一律 Network-Only（斷線保護），這兩個舊版的資料快取必須被 activate 清掉
        private static readonly string[] LegacyDataCaches = { "mes-master-v1", "mes-dispatch-v1" };

        private readonly UiTestFixture _fixture;
        public MobilePwaOfflineTests(UiTestFixture fixture) => _fixture = fixture;

        // ════════════════════════════════════════════════════════════════════
        // ① 預快取清單一項都不能缺
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// install 對離線替代頁以外的檔案是逐檔盡力而為，失敗只會 <c>console.warn('[SW] 預快取未完成…')</c>——
        /// 註冊照樣成功、頁面照常運作，只有**離線時**才會發現某個檔沒進快取。
        /// 清單裡打錯一個路徑（改名、搬目錄、換 CSS 檔名）就是這個症狀，而且線上沒有任何訊號。
        ///
        /// 清單直接從**線上供應的 sw.js** 解析，不在測試裡另抄一份：抄一份的話，
        /// 兩邊一起漏掉同一個檔時測試照樣綠。
        /// </summary>
        [Fact]
        public async Task 預快取清單裡的每一個檔都必須真的進到快取()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                var assets = await ReadStaticAssetsAsync(page);
                Assert.True(assets.Count >= 20,
                    $"從 sw.js 解析出的預快取清單只有 {assets.Count} 項，解析八成壞了——" +
                    "STATIC_ASSETS 的寫法若改變，這支測試會安靜地不再守任何東西。");

                await RegisterServiceWorkerAsync(page);

                var cachedUrls = await page.EvaluateAsync<string[]>(@"async () => {
                    const names = await caches.keys();
                    const out = [];
                    for (const n of names) {
                        const c = await caches.open(n);
                        for (const req of await c.keys()) out.push(new URL(req.url).pathname);
                    }
                    return out;
                }");

                var missing = assets.Where(a => !cachedUrls.Contains(a)).ToList();
                Assert.True(missing.Count == 0,
                    "以下預快取項目沒有進到 CacheStorage（install 對個別檔案失敗只會 console.warn，" +
                    $"線上不會有任何訊號）：\n  {string.Join("\n  ", missing)}");
            }
            finally { await context.DisposeAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // ② 離線導覽：帶查詢字串仍要命中快取
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>handleNavigate</c> 用 <c>ignoreSearch: true</c> 比對，因為
        /// <c>reports.html?dispatchId=1&amp;sessionId=2</c> 與快取裡的 <c>reports.html</c>
        /// 是同一份靜態檔。少了這個旗標則**只要帶參數就必然 miss**，而現場最常走的四條
        /// 動線（sessions→reports、list→sessions、ipqc→ipqc-process、fpi→ipqc-process）
        /// 全都帶參數——離線時每一條都會掉到 offline.html。
        ///
        /// 這支刻意斷言「拿到的是報工頁」而不是「沒有錯誤」：掉到 offline.html 時
        /// 頁面一樣載入成功、一樣沒有 JS error，只有標題不一樣。
        /// </summary>
        [Fact]
        public async Task 離線時帶查詢字串的導覽仍要拿到快取裡的那一頁()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);

                await context.SetOfflineAsync(true);
                await page.GotoAsync($"{_fixture.BaseUrl}{ReportsPage}?dispatchId=1&sessionId=2");

                var title = await page.TitleAsync();
                Assert.False(title.Contains("需要連線"),
                    "帶查詢字串的導覽掉到了離線替代頁——handleNavigate 的 ignoreSearch 沒有生效。" +
                    $"實際標題：{title}");
                Assert.Contains("報工", title);
            }
            finally
            {
                await context.SetOfflineAsync(false);
                await context.DisposeAsync();
            }
        }

        /// <summary>
        /// 反面：沒有預快取、也沒有被造訪過的頁面，離線時要落到 offline.html，
        /// 不是瀏覽器原生錯誤頁——原生錯誤頁上沒有「你的報工還在佇列裡」這個訊息，
        /// 現場只會以為資料不見了。
        /// </summary>
        [Fact]
        public async Task 離線且未快取的頁面要落到離線替代頁而不是瀏覽器錯誤頁()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);

                await context.SetOfflineAsync(true);
                // 這個路徑不在 STATIC_ASSETS，也沒被造訪過 → 快取必定 miss。
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/mfg/this-page-was-never-cached.html");

                var title = await page.TitleAsync();
                Assert.Contains("需要連線", title);   // v30 前叫「目前離線」
            }
            finally
            {
                await context.SetOfflineAsync(false);
                await context.DisposeAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ③ activate 只留目前的殼層：舊版殼層與舊版資料快取都要清掉
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// activate 的清理是「保留清單以外一律刪」。v24–v29 另有派工／主檔兩個**資料**快取
        /// （連不上時回最近一次的回應）；v30 改為斷線保護（mobile-pwa.md §5.0）後讀取一律走網路，
        /// 那兩個快取裡是前一個人看過的派工清單，升級後必須被清掉。
        ///
        /// 同時斷言兩個方向：舊的全不見、目前的殼層還在——只驗「舊的不見」的話，
        /// 一個什麼都刪的 activate 也會通過（那樣斷線時連 offline.html 都沒有）。
        /// </summary>
        [Fact]
        public async Task SW重新啟用後只留目前的殼層_舊版殼層與資料快取都被刪掉()
        {
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);

                const string staleCache = "mes-mobile-v0-stale";
                await page.EvaluateAsync(@"async (names) => {
                    for (const n of names) {
                        const c = await caches.open(n);
                        await c.put('/api/mobile/machines/TEST', new Response('{}'));
                    }
                }", LegacyDataCaches.Append(staleCache).ToArray());

                // 重新註冊：解除後再註冊同一支 sw.js，會重跑 install → activate，
                // 而 CacheStorage 是 origin 級的，不隨註冊解除而清空——正好模擬版本更新。
                await page.EvaluateAsync(@"async () => {
                    const regs = await navigator.serviceWorker.getRegistrations();
                    await Promise.all(regs.map(r => r.unregister()));
                }");
                await RegisterServiceWorkerAsync(page);

                var names = await page.EvaluateAsync<string[]>("async () => await caches.keys()");

                var leftover = names.Where(n => LegacyDataCaches.Contains(n) || n == staleCache).ToList();
                Assert.True(leftover.Count == 0,
                    $"舊版快取沒有被清掉：{string.Join(", ", leftover)}（現存：{string.Join(", ", names)}）");
                Assert.Contains(names, n => n.StartsWith("mes-mobile-v", StringComparison.Ordinal));
            }
            finally { await context.DisposeAsync(); }
        }

        // ════════════════════════════════════════════════════════════════════
        // ④ 斷線冷啟動：認得人，但不拿舊資料頂替
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 從主畫面圖示開 PWA＝新的 sessionStorage。斷線時仍要認得人（佇列靠它判斷「現在是誰」，
        /// 恢復後只送本人排的項目），但 v30 起**畫面資料一律要連線**：模組格與作業捷徑不得用
        /// 上次的回應頂替，要提示「需要連線」（v24–v29 會顯示上次的模組格，看起來像能離線作業）。
        ///
        /// 「新分頁」是關鍵：在同一個分頁裡斷網，sessionStorage 還在，驗不到冷啟動。
        /// </summary>
        [Fact]
        public async Task 斷線冷啟動時首頁認得人_但不顯示舊資料而是提示需要連線()
        {
            await SeedFixture.ResetAsync();   // ㉓ Admin 的手機作業權限
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);
                await MobileSignInAsync(page);

                Assert.True(await WaitModuleCardsAsync(page) > 0,
                    "線上時首頁一個模組都沒有——admin 沒有手機模組權限，這支測試的反面不成立。");
                await page.GotoAsync($"{_fixture.BaseUrl}{MfgListPage}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Assert.True(await VisibleOpShortcutsAsync(page) > 0, "線上時作業捷徑一個都沒有，反面不成立。");
                await page.CloseAsync();

                await context.SetOfflineAsync(true);
                var cold = await context.NewPageAsync();   // 新分頁＝sessionStorage 是空的
                await cold.GotoAsync($"{_fixture.BaseUrl}{HomePage}");

                Assert.DoesNotContain(EntryPage, cold.Url);
                await Assertions.Expect(cold.Locator("#userName")).ToHaveTextAsync(_fixture.AdminUserName);
                await Assertions.Expect(cold.Locator("#toast-container")).ToContainTextAsync("需要連線");
                Assert.Equal(0, await cold.Locator("#moduleGrid .module-card").CountAsync());

                await cold.GotoAsync($"{_fixture.BaseUrl}{MfgListPage}");
                await cold.WaitForTimeoutAsync(500);       // 斷線時沒有網路請求可等，給初始化跑完
                Assert.Equal(0, await VisibleOpShortcutsAsync(cold));
            }
            finally
            {
                await context.SetOfflineAsync(false);
                await context.DisposeAsync();
            }
        }

        /// <summary>
        /// 反面：登出之後離線冷啟動**不得**認得人。離線沿用的身分存在 localStorage，
        /// 登出沒清掉的話，斷網等於自動登回上一個人——共用手機上正是交班那一刻。
        /// </summary>
        [Fact]
        public async Task 登出後離線冷啟動不得沿用上一個人的身分()
        {
            await SeedFixture.ResetAsync();   // ㉓ Admin 的手機作業權限
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);
                await MobileSignInAsync(page);
                await WaitModuleCardsAsync(page);

                await page.ClickAsync("button[data-action='logout']");
                await page.WaitForURLAsync(new Regex(Regex.Escape(EntryPage)));
                await page.CloseAsync();

                await context.SetOfflineAsync(true);
                var cold = await context.NewPageAsync();
                await cold.GotoAsync($"{_fixture.BaseUrl}{HomePage}");
                await cold.WaitForTimeoutAsync(1000);

                Assert.Equal("", (await cold.Locator("#userName").TextContentAsync() ?? "").Trim());
                Assert.Equal(0, await cold.Locator("#moduleGrid .module-card").CountAsync());
                var leftovers = await cold.EvaluateAsync<int>(
                    "() => Object.keys(localStorage).filter(k => k === 'mobileUserOffline' || k.startsWith('mobileOfflineGet:')).length");
                Assert.Equal(0, leftovers);
            }
            finally
            {
                await context.SetOfflineAsync(false);
                await context.DisposeAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ⑤ 盤點斷線暫存 → 恢復連線自動送出 → 重送不重複建明細
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 真瀏覽器＋真 PostgreSQL 走一遍。單元測試（InMemory）驗得到去重邏輯，
        /// 驗不到「SyncLog 與業務變更在同一個交易」與 SW／IndexedDB／online 事件的串接。
        ///
        /// 重送那一步直接用同一個 body（同一個 clientId）再打一次端點，模擬
        /// 「伺服端已處理、回應在路上斷線、佇列恢復連線後又送一次」。
        /// 改動前自由盤點每送一次就多建一筆明細。
        /// </summary>
        [Fact]
        public async Task 盤點離線排隊後自動送出且同一筆重送不會多建明細()
        {
            await SeedFixture.ResetAsync();
            const string loc = "E2E-OFFCNT";
            await CleanupOfflineCountAsync(loc);   // 前一輪中斷時可能留下明細
            var (context, page) = await _fixture.NewAnonymousPageAsync();
            try
            {
                await RegisterServiceWorkerAsync(page);
                await MobileSignInAsync(page);
                await page.GotoAsync($"{_fixture.BaseUrl}/mobile/inv/stockcount.html");
                await page.WaitForFunctionAsync("() => typeof submitOrQueue === 'function' && !!window.offlineQueue");
                var before = await DbCountAsync($@"SELECT count(*) FROM ""InventoryCountItems"" WHERE ""LocationCode"" = '{loc}'");

                await context.SetOfflineAsync(true);
                var clientId = await page.EvaluateAsync<string>(@"async (loc) => {
                    await offlineQueue.ready();
                    const r = await submitOrQueue({ url: '/api/mobile/inv/count/first-count',
                        body: { lines: [{ productCode: 'E2E-RM01', locationCode: loc, whCode: '', lotNo: '', qty: 7 }] },
                        label: 'e2e' });
                    if (!r.queued) throw new Error('斷線時 submitOrQueue 沒有排隊');
                    const all = await offlineQueue.getAll();
                    return all.find(r => r.url.endsWith('/first-count') && r.status === 'pending').clientId;
                }", loc);

                await context.SetOfflineAsync(false);   // 'online' 事件 → 佇列自動 flush
                await WaitUntilAsync(page, @"async (id) => {
                    const r = (await offlineQueue.getAll()).find(x => x.clientId === id);
                    return !!r && r.status === 'sent';
                }", clientId, "佇列恢復連線後沒有把盤點送出（15 秒內狀態未轉 sent）");

                Assert.Equal(before + 1, await DbCountAsync(
                    $@"SELECT count(*) FROM ""InventoryCountItems"" WHERE ""LocationCode"" = '{loc}'"));
                Assert.Equal(1, await DbCountAsync($@"SELECT count(*) FROM ""SyncLogs"" WHERE ""ClientId"" = '{clientId}'"));

                // 重送同一筆（同 body、同 clientId）
                var status = await page.EvaluateAsync<int>(@"async (id) => {
                    const r = (await offlineQueue.getAll()).find(x => x.clientId === id);
                    const res = await fetch(r.url, { method: 'POST', credentials: 'include',
                        headers: { 'Content-Type': 'application/json', 'X-Plant-Context': r.plantId || '' }, body: JSON.stringify(r.body) });
                    return res.status;
                }", clientId);

                Assert.Equal(200, status);
                Assert.Equal(before + 1, await DbCountAsync(
                    $@"SELECT count(*) FROM ""InventoryCountItems"" WHERE ""LocationCode"" = '{loc}'"));
            }
            finally
            {
                await context.SetOfflineAsync(false);
                await context.DisposeAsync();
                // 自由盤點會替 admin 開一張盤點中的 WithoutSheet 清冊；清掉本測試的明細，
                // 清冊若因此變空也一併刪，免得變成其他盤點測試的前提。
                await CleanupOfflineCountAsync(loc);
            }
        }

        private static Task CleanupOfflineCountAsync(string loc) =>
            DbExecAsync($@"DELETE FROM ""InventoryCountItems"" WHERE ""LocationCode"" = '{loc}';
                DELETE FROM ""InventoryCountSheets"" s WHERE s.""CountMode"" = 'WithoutSheet'
                  AND s.""SheetStatus"" = 'Counting'
                  AND NOT EXISTS (SELECT 1 FROM ""InventoryCountItems"" i WHERE i.""SheetId"" = s.""SheetId"");");

        /// <summary>
        /// 輪詢一個**非同步**的頁面條件。不能用 <c>WaitForFunctionAsync</c>：它把回傳值當真值判斷，
        /// async 函式回的是 Promise（物件＝真值），於是**立刻通過**——這支測試第一版就是這樣，
        /// 在佇列還沒送出前就斷言，而那筆請求在清理之後才到伺服器、留下殘留資料。
        /// <c>EvaluateAsync</c> 會等 Promise 完成，拿到的才是真正的布林。
        /// </summary>
        private static async Task WaitUntilAsync(IPage page, string asyncPredicate, object? arg, string failMessage,
            int seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await page.EvaluateAsync<bool>(asyncPredicate, arg)) return;
                await Task.Delay(250);
            }
            Assert.Fail(failMessage);
        }

        private static string DbConnection() =>
            Environment.GetEnvironmentVariable("TestDbConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("需要 TestDbConnection 才能驗證資料庫狀態。");

        private static async Task<long> DbCountAsync(string sql)
        {
            await using var db = new Npgsql.NpgsqlConnection(DbConnection());
            await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(sql, db);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private static async Task DbExecAsync(string sql)
        {
            await using var db = new Npgsql.NpgsqlConnection(DbConnection());
            await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task MobileSignInAsync(IPage page)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}{EntryPage}");
            await page.FillAsync("#username", _fixture.AdminUserName);
            await page.FillAsync("#password", _fixture.AdminPassword);
            await page.ClickAsync("button[data-action='login']");
            await page.WaitForURLAsync(new Regex(Regex.Escape(HomePage)), new PageWaitForURLOptions { Timeout = 20_000 });
        }

        private static async Task<int> WaitModuleCardsAsync(IPage page)
        {
            var cards = page.Locator("#moduleGrid .module-card");
            try { await cards.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 }); }
            catch (TimeoutException) { return 0; }
            return await cards.CountAsync();
        }

        private static Task<int> VisibleOpShortcutsAsync(IPage page) =>
            page.EvaluateAsync<int>(@"() => {
                const wrap = document.getElementById('opShortcuts');
                if (!wrap || wrap.closest('.container-fluid')?.classList.contains('d-none')) return 0;
                return [...wrap.querySelectorAll('[data-op]')].filter(c => !c.classList.contains('d-none')).length;
            }");

        // ════════════════════════════════════════════════════════════════════
        // 輔助
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 造訪入口頁（index.page.js 會註冊 SW），並等到 SW 真的 activated 且已接管本頁。
        /// 只等 <c>navigator.serviceWorker.ready</c> 不夠：ready 在 activated 就 resolve，
        /// 但此時 <c>controller</c> 可能還是 null，接著的離線導覽就不會經過 SW。
        /// sw.js 的 install 有 <c>skipWaiting()</c>、activate 有 <c>clients.claim()</c>，
        /// 所以 controller 一定會出現，等它是安全的。
        /// </summary>
        private async Task RegisterServiceWorkerAsync(IPage page)
        {
            await page.GotoAsync($"{_fixture.BaseUrl}{EntryPage}");
            // ⚠️ 原本用 WaitForFunctionAsync(async …)：Promise 是真值，**立刻通過**（2026-09-25 實測
            //    `async () => false` 87 ms 就返回），這裡從來沒有真的等到 SW 接管。見 WaitUntilAsync。
            await WaitUntilAsync(page, @"async () => {
                if (!('serviceWorker' in navigator)) return false;
                const reg = await navigator.serviceWorker.ready;
                return reg.active?.state === 'activated' && !!navigator.serviceWorker.controller;
            }", null, "20 秒內 SW 沒有 activated 並接管本頁", seconds: 20);
        }

        /// <summary>
        /// 從**線上供應的** sw.js 解析 STATIC_ASSETS。刻意連 <c>OFFLINE_PAGE</c> 這個
        /// 以常數列入清單的項目一併補上——它在陣列裡是識別字不是字串，只掃字串會漏掉，
        /// 而離線替代頁沒進快取正是離線功能最致命的一種壞法。
        /// </summary>
        private async Task<List<string>> ReadStaticAssetsAsync(IPage page)
        {
            var resp = await page.APIRequest.GetAsync($"{_fixture.BaseUrl}/mobile/sw.js");
            Assert.True(resp.Ok, $"取不到 /mobile/sw.js（HTTP {resp.Status}）。");
            var src = await resp.TextAsync();

            var block = Regex.Match(src, @"const STATIC_ASSETS\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
            Assert.True(block.Success, "在 sw.js 裡找不到 STATIC_ASSETS 陣列。");

            var list = Regex.Matches(block.Groups[1].Value, @"'([^']+)'")
                            .Select(m => m.Groups[1].Value)
                            .ToList();
            list.Add(OfflinePage);
            return list.Distinct().ToList();
        }
    }
}
