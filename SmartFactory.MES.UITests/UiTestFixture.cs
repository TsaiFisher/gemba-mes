using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using SmartFactory.MES.UITests.Pages;
using Xunit;

namespace SmartFactory.MES.UITests
{
    public class UiTestFixture : IAsyncLifetime, IDisposable
    {
        public IPlaywright Playwright { get; private set; } = null!;
        public IBrowser Browser { get; private set; } = null!;
        public IBrowserContext Context { get; private set; } = null!;
        public IConfiguration Configuration { get; private set; } = null!;
        public string BaseUrl { get; private set; } = null!;

        /// <summary>E2E 使用的帳號。</summary>
        public string AdminUserName { get; private set; } = "admin";

        /// <summary>
        /// 實際可用的密碼。乾淨 DB 的 admin 種子帶 MustChangePassword=true（Program.cs），
        /// 首次登入會被 ForcePasswordChangeMiddleware 攔到改密頁；bootstrap 完成改密後
        /// 此屬性即為新密碼。測試一律讀這裡，不可再寫死 "Dev12345"。
        /// </summary>
        public string AdminPassword { get; private set; } = "Dev12345";

        /// <summary>
        /// 強制改密時換成的密碼。必須與種子預設值不同——Identity 的密碼重用檢查
        /// 會擋下「換回原密碼」。
        /// </summary>
        private const string BootstrapPassword = "E2eBootstrap!2026";

        /// <summary>
        /// 登入後等待首頁的逾時。刻意短：登入失敗改由 URL 分類即時判定，
        /// 這裡只涵蓋「已導向首頁、標題尚未渲染完」的正常等待。
        /// 原本沿用 Playwright 預設 30 秒，任何登入問題都要空等滿 30 秒才失敗，
        /// 213 個測試因此跑了 1.8 小時（2026-07-17 nightly）。
        /// </summary>
        private int _loginTimeoutMs = 15_000;

        /// <summary>
        /// bootstrap 完成後為 true。共用 <see cref="Context"/> 已持有驗證 cookie，
        /// 之後每個測試不需要（也不該）再跑一次登入流程。
        /// </summary>
        private bool _authenticated;

        /// <summary>
        /// 已發出、尚未關閉的測試分頁。測試在同一個 collection 內**序列**執行，
        /// 因此下一個測試要頁時，前一個測試的頁必定已用完 → 直接關掉回收。
        ///
        /// 為何必要：原本每個測試都 <c>Context.NewPageAsync()</c> 卻從不關閉，
        /// 219 個測試 = 219 個常駐 Chromium 分頁。記憶體吃滿 → swap → 磁碟 100%，
        /// 後段測試全數卡在「navigating to /Identity/Account/Login 逾時 30s」。
        /// 2026-07-23 實測：整輪 34 分鐘、尾段 13 個 BAS 測試因此假性失敗。
        /// </summary>
        private readonly List<IPage> _pages = new();

        /// <summary>
        /// Playwright trace 的保留策略：<c>on-failure</c>（預設）只留失敗那幾支，<c>off</c> 完全不錄。
        /// 由 <c>Trace</c> 設定值（環境變數或 appsettings.test.json）指定。
        /// </summary>
        private bool _tracing;

        /// <summary>目前這段 trace chunk 屬於哪一支測試；沒有進行中的 chunk 時為 null。</summary>
        private TestOutcomeTracker.CurrentTest? _chunkOwner;

        /// <summary>
        /// 失敗測試的 trace 輸出目錄（測試組件輸出目錄下）。nightly.yml 上傳的就是這個目錄；
        /// 用 <c>npx playwright show-trace &lt;zip&gt;</c> 或 https://trace.playwright.dev 開啟。
        ///
        /// 為何必要：nightly 原本就上傳這個目錄，但從來沒有任何程式碼寫進去——
        /// 紅燈時下載到的只有 app.log，失敗當下頁面長什麼樣、點了什麼、網路回了什麼一概不知。
        /// </summary>
        public static string TraceDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "playwright-traces");

        public async Task InitializeAsync()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.test.json", optional: true, reloadOnChange: true);

            Configuration = builder.Build();
            BaseUrl = Setting("BaseUrl") ?? "http://localhost:5110";
            bool headless = bool.Parse(Setting("Headless") ?? "true");
            AdminUserName = Setting("AdminUserName") ?? AdminUserName;
            AdminPassword = Setting("AdminPassword") ?? AdminPassword;
            if (int.TryParse(Setting("LoginTimeoutMs"), out var timeout) && timeout > 0)
                _loginTimeoutMs = timeout;

            // Initialize Playwright
            Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless
            });

            Context = await Browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true
            });

            await BootstrapAdminLoginAsync();

            // 刻意在 bootstrap 之後才開錄：登入/改密流程不屬於任何一支測試，
            // 而且那段會把密碼打進表單，不該進到上傳的 artifact。
            _tracing = !string.Equals(Setting("Trace"), "off", StringComparison.OrdinalIgnoreCase);
            if (_tracing)
            {
                Directory.CreateDirectory(TraceDirectory);
                await Context.Tracing.StartAsync(new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                });
            }
        }

        /// <summary>
        /// 設定值解析：環境變數優先於 appsettings.test.json。
        /// nightly.yml 是以環境變數傳 BaseUrl / Headless 的，只讀 json 會拿不到。
        /// </summary>
        private string? Setting(string key)
            => Environment.GetEnvironmentVariable(key) ?? Configuration[key];

        // ════════════════════════════════════════════════════════════════════
        // 登入
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 取得一個測試用分頁（已帶共用 Context 的驗證 cookie），並回收上一個測試留下的分頁。
        /// 所有測試都必須經由這裡取頁，不要直接用 <c>Context.NewPageAsync()</c>——
        /// 否則分頁會一路累積到記憶體耗盡（見 <see cref="_pages"/>）。
        /// </summary>
        public async Task<IPage> NewPageAsync()
        {
            await SwitchTraceChunkAsync();

            foreach (var stale in _pages)
            {
                try { await stale.CloseAsync(); }
                catch { /* 已被測試自行關閉或 context 已收掉，忽略 */ }
            }
            _pages.Clear();

            var page = await Context.NewPageAsync();
            _pages.Add(page);
            return page;
        }

        /// <summary>
        /// 讓 trace chunk 跟著「目前這支測試」走：換了測試就先結算上一段（失敗才存檔），再為這支開新的一段。
        /// 掛在 <see cref="NewPageAsync"/> 上而不是另寫 hook，是因為所有用共用 Context 的測試都必經這裡；
        /// 同一支測試多次取頁則沿用同一段。
        /// </summary>
        private async Task SwitchTraceChunkAsync()
        {
            if (!_tracing) return;

            var current = TestOutcomeTracker.Current;
            if (current != null && current.Key == _chunkOwner?.Key) return;

            await FinishTraceChunkAsync();

            if (current == null) return;
            await Context.Tracing.StartChunkAsync(new TracingStartChunkOptions { Title = current.DisplayName });
            _chunkOwner = current;
        }

        /// <summary>
        /// 結算進行中的 chunk。呼叫時機必然在該測試結束之後（下一支取頁、或 fixture 收尾），
        /// 此時 xUnit 已同步送出它的結果（見 <see cref="TestOutcomeTracker"/>）。
        /// 萬一查不到結果，寧可多留一份也不丟——丟掉的 trace 無法事後補錄。
        /// </summary>
        private async Task FinishTraceChunkAsync()
        {
            if (_chunkOwner == null) return;
            var owner = _chunkOwner;
            _chunkOwner = null;

            try
            {
                if (TestOutcomeTracker.Failed(owner.Key) == false)
                {
                    await Context.Tracing.StopChunkAsync();
                    return;
                }

                var path = Path.Combine(TraceDirectory, TraceFileName(owner.DisplayName));
                await Context.Tracing.StopChunkAsync(new TracingStopChunkOptions { Path = path });
                Console.Error.WriteLine($"[UITests] 失敗測試的 trace：{path}");
            }
            catch (Exception ex)
            {
                // trace 只是診斷輔助，存不下來不該讓下一支測試跟著紅。
                Console.Error.WriteLine($"[UITests] 無法結算 trace（{owner.DisplayName}）：{ex.Message}");
            }
        }

        private static string TraceFileName(string displayName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(displayName.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
            if (safe.Length > 150) safe = safe[..150];
            return $"{DateTime.Now:HHmmss}-{safe}.zip";
        }

        /// <summary>
        /// 開新分頁並確保已登入，回傳可直接導往目標畫面的 page。
        /// 登入不成立時**立即**拋出可行動的訊息，不空等逾時。
        /// </summary>
        public async Task<IPage> NewLoggedInPageAsync()
        {
            var page = await NewPageAsync();
            await SignInAsync(page);
            return page;
        }

        /// <summary>
        /// 開一個**未登入**的獨立 context 分頁。共用的 <see cref="Context"/> 在 fixture
        /// 初始化時就已登入，要驗登入本身（例如錯誤帳密）必須用乾淨 cookie 的分頁，
        /// 否則造訪登入頁會直接被重導走、根本沒有表單可填。
        /// 呼叫端負責 <c>DisposeAsync</c> 回傳的 context。
        /// </summary>
        public async Task<(IBrowserContext context, IPage page)> NewAnonymousPageAsync()
        {
            var context = await Browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true
            });
            var page = await context.NewPageAsync();
            return (context, page);
        }

        /// <summary>
        /// 在既有 page 上登入並停在首頁。呼叫端已自行建頁（例如需要先掛 PageError
        /// 收集登入過程的 JS 錯誤）時用這個。
        /// </summary>
        public async Task SignInAsync(IPage page)
        {
            // 共用 Context 已在 bootstrap 時登入，cookie 對新分頁一樣有效。
            // 這裡直接返回，呼叫端隨即導往自己的目標畫面即可。
            // 不這樣做的話，每個測試都會多跑一次「登入頁 → 轉址 → 首頁」的完整往返，
            // 219 個測試就是 219 次純浪費的頁面載入。
            if (_authenticated)
                return;

            var outcome = await SignInAsync(page, AdminPassword);
            if (outcome != LoginOutcome.SignedIn)
                throw new InvalidOperationException(DescribeFailure(outcome, page.Url));

            await page.WaitForFunctionAsync(
                HomeTitleCheck,
                null,
                new PageWaitForFunctionOptions { Timeout = _loginTimeoutMs });
        }

        /// <summary>
        /// 「已到首頁」的判斷。首頁標題取自選單資源鍵 Home（Core #61），依介面語系是 Home 或「首頁」；
        /// 沒有語系 cookie 的新 context 走預設語系（中文），只比對 'Home' 會等到逾時。
        /// </summary>
        public const string HomeTitleCheck =
            "() => document.title.includes('Home') || document.title.includes('\u9996\u9801')";

        /// <summary>
        /// 一次性探測：找出當前 DB 可用的密碼，必要時完成強制改密。
        /// 在 fixture 初始化跑一次，之後所有測試共用結果——避免每個測試各自撞改密頁。
        /// </summary>
        private async Task BootstrapAdminLoginAsync()
        {
            // 同一個 DB 重跑時，密碼已在前一輪被換成 BootstrapPassword，故兩個都要試。
            var candidates = new List<string> { AdminPassword };
            if (!candidates.Contains(BootstrapPassword))
                candidates.Add(BootstrapPassword);

            var page = await Context.NewPageAsync();
            try
            {
                foreach (var password in candidates)
                {
                    var outcome = await SignInAsync(page, password);

                    if (outcome == LoginOutcome.ForcedPasswordChange)
                    {
                        outcome = await CompleteForcedPasswordChangeAsync(page, BootstrapPassword);
                        if (outcome == LoginOutcome.SignedIn)
                        {
                            AdminPassword = BootstrapPassword;
                            _authenticated = true;
                            return;
                        }
                    }

                    if (outcome == LoginOutcome.SignedIn)
                    {
                        AdminPassword = password;
                        _authenticated = true;
                        return;
                    }

                    // 2FA 之類的環境性阻擋，換密碼也不會過 —— 直接拋，別再試下一個。
                    if (outcome != LoginOutcome.Rejected)
                        throw new InvalidOperationException(DescribeFailure(outcome, page.Url));
                }

                throw new InvalidOperationException(
                    $"UITests 無法以 '{AdminUserName}' 登入 {BaseUrl}：候選密碼全部被拒。" +
                    "請確認 app 已啟動，且資料庫是由本專案 seed 出來的（Program.cs 會建立 admin）。" +
                    "亦可用 AdminPassword 環境變數指定密碼。");
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        private async Task<LoginOutcome> SignInAsync(IPage page, string password)
        {
            var login = new LoginPage(page, BaseUrl);
            await login.GotoAsync();

            // Context 是共用的：若前一個測試已登入，造訪登入頁可能直接被重導走，
            // 此時沒有表單可填，直接依落地位置判定即可。
            if (await page.QuerySelectorAsync("input[name='Input.Email']") == null)
                return await ClassifyAsync(page);

            await login.LoginAsync(AdminUserName, password);
            return await ClassifyAsync(page);
        }

        private async Task<LoginOutcome> CompleteForcedPasswordChangeAsync(IPage page, string newPassword)
        {
            await page.FillAsync("input[name='newPassword']", newPassword);
            await page.FillAsync("input[name='confirmPassword']", newPassword);

            // ⚠️ 必須用表單內容鎖定按鈕：改密頁套了版面，"form button[type=submit]"
            // 會命中兩顆，而第一顆是導覽列的「登出」——點下去直接被登出、落回登入頁，
            // 症狀看起來像「帳密被拒」，極難追。
            await page.ClickAsync("form:has(input[name='newPassword']) button[type='submit']");
            return await ClassifyAsync(page);
        }

        /// <summary>
        /// 依落地 URL 判定登入結果。用 URL 而非等標題，是為了讓失敗即時可辨識、
        /// 且能區分「帳密錯」「被強制改密」「被強制 2FA」三種完全不同的原因。
        /// </summary>
        private async Task<LoginOutcome> ClassifyAsync(IPage page)
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var url = page.Url;

            if (url.Contains("/Account/ForceChangePassword", StringComparison.OrdinalIgnoreCase))
                return LoginOutcome.ForcedPasswordChange;
            if (url.Contains("/TwoFactor", StringComparison.OrdinalIgnoreCase))
                return LoginOutcome.TwoFactorRequired;
            if (url.Contains("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase))
                return LoginOutcome.Rejected;

            return LoginOutcome.SignedIn;
        }

        private string DescribeFailure(LoginOutcome outcome, string url) => outcome switch
        {
            LoginOutcome.TwoFactorRequired =>
                $"UITests 登入後被導向兩階段驗證（{url}）。UI 測試不涵蓋 2FA，" +
                "請在執行環境設定 Security__TwoFactorEnforcementEnabled=false。" +
                "（本機是靠 appsettings.Development.json 關掉的，該檔不進版控，CI 必須另外設。）",

            LoginOutcome.ForcedPasswordChange =>
                $"UITests 仍停在強制改密頁（{url}）——自動改密未成功，" +
                "可能被密碼原則或密碼重用檢查擋下。請檢查 Identity 密碼規則。",

            LoginOutcome.Rejected =>
                $"UITests 帳密被拒（{url}）。請確認 '{AdminUserName}' 的密碼，" +
                "或以 AdminPassword 環境變數指定。",

            _ => $"UITests 登入落在非預期頁面：{url}",
        };

        private enum LoginOutcome
        {
            SignedIn,
            ForcedPasswordChange,
            TwoFactorRequired,
            Rejected,
        }

        public async Task DisposeAsync()
        {
            if (_tracing && Context != null)
            {
                await FinishTraceChunkAsync();
                try { await Context.Tracing.StopAsync(); } catch { /* context 已收掉 */ }
            }

            foreach (var page in _pages)
            {
                try { await page.CloseAsync(); } catch { /* ignore */ }
            }
            _pages.Clear();

            if (Context != null)
            {
                await Context.CloseAsync();
                await Context.DisposeAsync();
            }
            if (Browser != null)
            {
                await Browser.CloseAsync();
                await Browser.DisposeAsync();
            }
            Playwright?.Dispose();
        }

        public void Dispose()
        {
            // Do not dispose async objects here
        }
    }

    [CollectionDefinition("UI Tests")]
    public class UiTestCollection : ICollectionFixture<UiTestFixture>
    {
        // This class has no code, and is never created. Its purpose is simply
        // to be the place to apply [CollectionDefinition] and all the
        // ICollectionFixture<> interfaces.
    }
}
