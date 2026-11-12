using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// 報表三頁的權限守門（人工驗收計畫 Wave R 的 R-2 與 R-6）。
    ///
    /// <para><b>為什麼補這一檔</b>：Wave R 的自動化覆蓋表在「UI／端到端」那一列寫的是
    /// <b>無</b>——<c>grep</c> 這三頁在 <c>UITests/</c> 與 <c>Flows/</c> 命中 0，
    /// R-1～R-4、R-6 全部落在那個零裡面。單元測試很厚（<c>RPS/</c> 10 檔 121 支）
    /// 但守的是計算與判定，**沒有一支走真實 HTTP**，於是「選單看不到」與「網址打不進去」
    /// 這兩件事在測試上無從分辨。</para>
    ///
    /// <para><b>R-2</b>：拿掉角色的 <c>CanRead</c> 之後，**直接輸入網址**也要被擋，
    /// 不是只有選單消失。<b>R-6</b>：報表產出後把收件人的權限拿掉，他**仍在收件匣看得到那一列**，
    /// 但下載要被擋——這是 §7.8 S5 刻意的設計（附件一旦寄出就永久脫離 RBAC，
    /// 所以擋在下載那一刻），要驗的是「擋得住」而不是「看不到」。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReportPermissionIntegrationTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        /// <summary>三頁的 MenuKey ＝ 網址，R-2 逐頁驗。</summary>
        private static readonly (string MenuKey, string Url)[] Pages =
        {
            ("ReportSchedule",   "/ReportSchedule"),
            ("ReportInbox",      "/ReportInbox"),
            ("ReportRunHistory", "/ReportRunHistory"),
        };

        /// <summary>
        /// R-6 收窄的目標工具。
        ///
        /// <para>⚠️ <b>刻意挑一支沒有別的測試在用的工具。</b>測試庫的 <c>MenuPermissions</c>
        /// 是跨進程共用的——<c>SavedQueryIntegrationTests</c> 會把 <c>StockShortage</c> 與
        /// <c>SalesRanking</c> 設成有權限，<c>QA</c> 那批動 <c>QaAssistant</c>／<c>QaExtract</c>，
        /// MFG 那批動 <c>Dispatch</c>／<c>Schedule</c>。在它們用的鍵上做「收窄」，
        /// 兩邊會互相把對方的前提改掉，而症狀是隨執行順序飄移的紅燈。</para>
        /// </summary>
        private const string TargetTool = "ReturnRate";

        private const string NamePrefix = "ITEST-RPS-PERM-";

        private readonly AuthedScheduleFactory _factory;

        public ReportPermissionIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        /// <summary>
        /// 每支測試前把權限補齊。**還原不是禮貌而是必須**：測試庫跨進程共用，
        /// 留一個 <c>CanRead=false</c> 給別的量測，症狀是那一輪的數字看起來很正常但少了一整頁。
        /// </summary>
        public async Task InitializeAsync()
        {
            await CleanupAsync();
            await GrantAllAsync();
        }

        public async Task DisposeAsync()
        {
            await GrantAllAsync();
            await CleanupAsync();
        }

        // ── R-2：選單隱藏不等於擋得住 ──────────────────────────────────────

        [Fact]
        public async Task R2_有讀取權限時三頁都打得開()
        {
            // 先釘住「正常情況是 200」。沒有這一支，下面那支即使因為別的理由
            // （驗證掉了、路由改名）永遠回非 200，也會一直是綠的。
            using var client = Client();

            foreach (var (_, url) in Pages)
            {
                var res = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            }
        }

        [Fact]
        public async Task R2_拿掉讀取權限後直接輸入網址也要被擋()
        {
            using var client = Client();

            foreach (var (menuKey, url) in Pages)
            {
                await SetAsync(menuKey, canRead: false);
                try
                {
                    var res = await client.GetAsync(url);

                    // Forbid() 在有登入的情況下是 403。轉導（302）代表驗證掉了或被
                    // 強制改密／2FA 中介攔下，那是另一回事，**不該被當成權限有擋**——
                    // 這正是 AuthedScheduleFactory 的註解在警告的「以錯的理由通過」。
                    Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
                }
                finally
                {
                    await SetAsync(menuKey, canRead: true);
                }
            }
        }

        /// <summary>
        /// <b>R-2 的另一半：矩陣調整後「立即生效」。</b>
        ///
        /// <para>上一支驗的是「擋得住」——它在**請求之前**就把權限改掉了，所以就算系統
        /// 在登入時把權限烘進 cookie、或在行程裡快取一份，那支測試依然會綠。
        /// 本支驗的是**時效**：在同一個已登入的連線上先成功打開一次（把任何可能的快取
        /// 預熱起來），再改權限，然後看**下一個請求**。</para>
        ///
        /// <para><b>為什麼值得測一個目前結構上必然成立的性質</b>：
        /// 今天 `PermissionService` 每次都查 `MenuPermissions`、`UserContext.ActiveRoleId`
        /// 每個請求重讀使用者，所以沒有快取可言。但「為權限查詢加一層快取」是效能調校時
        /// 極可能出現的改動，而它的失效方式是**沉默的**：撤掉的權限在快取過期前仍然通行，
        /// 管理者在矩陣上看到的是已經關掉的開關。這支測試就是那一天的安全網。</para>
        ///
        /// <para>兩個方向都驗：撤銷要立刻擋下，授予也要立刻放行——只驗一個方向的話，
        /// 「永遠擋住」這種同樣錯誤的實作會通過。</para>
        /// </summary>
        [Fact]
        public async Task R2_權限在同一個連線上調整後_下一個請求就要生效()
        {
            const string menuKey = "ReportInbox";
            const string url     = "/ReportInbox";

            using var client = Client();

            // ① 先成功打開一次：預熱任何可能存在的每連線／每行程快取。
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

            try
            {
                // ② 撤銷 → 下一個請求就必須被擋，不得等到重新登入或快取過期。
                await SetAsync(menuKey, canRead: false);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);

                // ③ 再授予 → 下一個請求就必須放行。
                //    少了這一段，一個「撤銷後永遠擋住」的錯誤實作也會讓 ② 通過。
                await SetAsync(menuKey, canRead: true);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
            }
            finally
            {
                await SetAsync(menuKey, canRead: true);
            }
        }

        [Fact]
        public async Task R2_拿掉收件匣讀取權限後_連下載端點也要被擋()
        {
            // 下載是**另一個端點**，它自己也守一次收件匣的 CanRead（§7.8 S5 那層之外的第一層）。
            // 這一支是突變測試逼出來的：把 Download 開頭那道守門整行刪掉，
            // 其餘三支測試**全部照樣綠**——Index 的守門沒被動到，而 R-6 驗的是第三層
            // （工具權限）。也就是說「頁面進不去但檔案還載得到」這個破口，
            // 在補這一支之前沒有任何測試看得見。
            var (definitionId, runId) = await SeedRunAsync();

            using var client = Client();
            try
            {
                // 工具權限保持開著，確保擋下來的是收件匣這一層而不是第三層——
                // 兩層都回 403，不分開設定就分不出實際擋它的是誰。
                await SetToolAsync(canRead: true);
                await SetAsync("ReportInbox", canRead: false);

                var res = await client.GetAsync($"/ReportInbox/Download?runId={runId}");
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            }
            finally
            {
                await SetAsync("ReportInbox", canRead: true);
                await CleanupAsync(definitionId);
            }
        }

        // ── R-6：產出後才失去權限，看得到但下載不到 ────────────────────────

        [Fact]
        public async Task R6_收件人失去報表權限後_仍看得到那一列但下載被擋()
        {
            var (definitionId, runId) = await SeedRunAsync();

            using var client = Client();

            // ① 先確認有權限時真的下載得到。少了這一步，後面的 403 可能只是
            //    「這筆資料根本不存在」或「收件人判定沒過」——那兩種都不是 R-6 要驗的東西。
            await SetToolAsync(canRead: true);
            var granted = await client.GetAsync($"/ReportInbox/Download?runId={runId}");
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

            // ② 拿掉那支報表的權限（不是收件匣本身的）。
            await SetToolAsync(canRead: false);

            // ③ 收件匣仍看得到那一列——附件已經產出，列不會因為調職而消失。
            var listed = await client.GetAsync("/ReportInbox?searched=true");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Contains(NamePrefix, await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // ④ 但下載這一刻要重查權限並擋下來（§7.8 S5）。
            //    403 而不是 404：這一層是「你不再有這支報表的權限」，
            //    404 是上一層「你不是收件人」——兩者混用會讓這支測試在收件人判定
            //    壞掉時照樣通過。
            var denied = await client.GetAsync($"/ReportInbox/Download?runId={runId}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            await CleanupAsync(definitionId);
        }

        // ── 前置 ────────────────────────────────────────────────────────────

        /// <summary>建立一筆「已產出且有實體檔案」的紀錄，收件人是測試身分（admin）。</summary>
        private async Task<(int DefinitionId, long RunId)> SeedRunAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IReportFileStore>();

            var adminId = (await db.Users.AsNoTracking()
                .FirstAsync(u => u.UserName == "admin")).Id;

            // 檔案要真的存在：Download 讀不到內容時走的是「過了保留期」那條路（302），
            // 不是 200，會讓步驟 ① 以錯的理由失敗。
            var path = await files.SaveAsync(
                System.Text.Encoding.UTF8.GetBytes("h1,h2\n1,2\n"), ".csv");

            var def = new ReportDefinition
            {
                Name = NamePrefix + Guid.NewGuid().ToString("N")[..8],
                OwnerUserId = adminId,
                OwnerRoleId = RoleId,
                CreatedByRoleId = RoleId,
                ToolKey = TargetTool,
                ParamsJson = "{}",
                OutputFormat = "Csv",
                ScheduleKind = null,          // 只存查詢，不排程——不影響下載路徑
                CreatedAt = DateTime.UtcNow,
            };
            db.ReportDefinitions.Add(def);
            await db.SaveChangesAsync();

            db.ReportRecipients.Add(new ReportRecipient { DefinitionId = def.Id, UserId = adminId });

            var run = new ReportRun
            {
                DefinitionId = def.Id,
                RunAt = DateTime.UtcNow,
                TriggerType = ReportTriggerType.Manual,
                Status = ReportRunStatus.Success,
                RowCount = 1,
                FilePath = path,
            };
            db.ReportRuns.Add(run);
            await db.SaveChangesAsync();

            return (def.Id, run.RunId);
        }

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private Task GrantAllAsync() => SetManyAsync(
            Pages.Select(p => p.MenuKey).Append(TargetTool), canRead: true);

        private Task SetAsync(string menuKey, bool canRead) =>
            SetManyAsync(new[] { menuKey }, canRead);

        private Task SetToolAsync(bool canRead) =>
            SetManyAsync(new[] { TargetTool }, canRead);

        private async Task SetManyAsync(IEnumerable<string> menuKeys, bool canRead)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            foreach (var menuKey in menuKeys)
            {
                var perm = await db.MenuPermissions
                    .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == menuKey);

                if (perm == null)
                {
                    perm = new MenuPermission { RoleId = RoleId, MenuKey = menuKey };
                    db.MenuPermissions.Add(perm);
                }

                perm.CanRead = canRead;
            }

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 清掉本檔前幾輪的殘留（測試庫是重跑庫）。
        ///
        /// <para><b>產出檔要跟著紀錄一起刪。</b>只刪 DB 列會留下孤兒檔——那正是 R-9 在守的東西
        /// （紀錄刪了檔還在＝含資料的檔從此沒人清理）。測試自己製造孤兒檔的話，
        /// 開發機的 <c>App_Data/reports</c> 會隨著每一輪執行一直長大。</para>
        /// </summary>
        private async Task CleanupAsync(int? definitionId = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IReportFileStore>();

            var defs = definitionId is { } id
                ? await db.ReportDefinitions.Where(d => d.Id == id).ToListAsync()
                : await db.ReportDefinitions.Where(d => d.Name.StartsWith(NamePrefix)).ToListAsync();

            if (defs.Count == 0) return;

            var ids = defs.Select(d => d.Id).ToList();
            var runs = await db.ReportRuns.Where(r => ids.Contains(r.DefinitionId)).ToListAsync();

            foreach (var path in runs.Select(r => r.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)))
                files.Delete(path!);

            db.ReportRuns.RemoveRange(runs);
            db.ReportRecipients.RemoveRange(await db.ReportRecipients.Where(r => ids.Contains(r.DefinitionId)).ToListAsync());
            db.ReportDefinitions.RemoveRange(defs);
            await db.SaveChangesAsync();
        }
    }
}
