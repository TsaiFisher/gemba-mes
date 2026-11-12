using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// 「立即執行」的整條鏈（人工驗收計畫 Wave R 的 R-4）。
    ///
    /// <para><b>為什麼補這一檔</b>：<c>grep RunNow</c> 在三個測試專案命中 <b>0</b>。
    /// R-4 的通過標準是「按下後執行紀錄新增一列、收件匣收得到檔案，**且檔案裡真的有資料列**」，
    /// 而這三段分屬三個元件（Controller → 派工器 → 收件匣），
    /// 單元測試各自測過自己那一段，接縫上沒有任何一支測試。</para>
    ///
    /// <para><b>接縫才是重點</b>：<c>RunNow</c> 不同步執行報表，它只把 <c>NextRunAt</c>
    /// 拉到現在讓派工器撈（避免在請求執行緒上跑大報表）。也就是說「按下去」與「跑出來」
    /// 之間隔著一個背景服務，**按鈕本身回 200 完全不代表報表產得出來**。</para>
    ///
    /// <para>R-F4 的形狀就在這條縫上：收得到檔案、格式正確、零筆。那次修補留下的
    /// <c>ReportToolExportRowsTests</c> 守的是 descriptor 回傳幾列，
    /// 沒有守「那些列真的寫進檔案」——從 <c>result.Rows</c> 到 <c>files.SaveAsync</c>
    /// 這一段仍然沒有測試。</para>
    ///
    /// <para>沿用 <see cref="AuthedScheduleFactory"/> 與既有 collection，理由見
    /// <c>CollectionLayoutGuardTests</c>：全專案共用同一個測試庫，不另開 collection。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReportRunNowIntegrationTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const string TargetTool = "ReturnRate";
        private const string MenuKey = "ReportSchedule";
        private const string NamePrefix = "ITEST-RPS-RUNNOW-";

        /// <summary>
        /// 本檔專用的廠別代碼。
        ///
        /// <para>⚠️ <b>報表要撈得到東西就得種業務單據，而測試庫是跨進程共用的。</b>
        /// 用專屬廠別把種下的出貨單與所有人隔開：<c>ReturnRateTool</c> 以
        /// <c>request.PlantId</c> 逐筆比對廠別，於是別人的資料進不了我的報表，
        /// 我的資料也不會混進別人的斷言。共用代碼的後果見
        /// <c>FlowTestPlantUniquenessTests</c>——撞到時紅的是先跑的那一支，
        /// 訊息裡完全沒有新測試的名字。</para>
        /// </summary>
        private const string PlantId = "ZZRUNNOW";

        /// <summary>種在專屬廠別裡的品號，用來驗「檔案內容真的是這份報表的資料」。</summary>
        private const string ProductCode = "ITEST-RUNNOW-P1";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly AuthedScheduleFactory _factory;

        public ReportRunNowIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            await GrantAsync(canUpdate: true);
        }

        public async Task DisposeAsync()
        {
            await GrantAsync(canUpdate: true);
            await CleanupAsync();
        }

        // ── R-4：按下去到收得到，整條鏈 ─────────────────────────────────────

        [Fact]
        public async Task R4_立即執行後_執行紀錄新增一列且收件匣下載得到那份檔案()
        {
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            var res = await PostRunNowAsync(client, definitionId);

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("\"success\":true", await res.Content.ReadAsStringAsync());

            // 按鈕只負責把 NextRunAt 拉到現在。這一步單獨斷言，是為了讓
            // 「按鈕沒作用」與「派工器沒跑」在失敗訊息上分得開。
            Assert.True(await NextRunAtAsync(definitionId) <= DateTime.UtcNow,
                "RunNow 回了 200 但 NextRunAt 沒有被拉到現在——派工器永遠撈不到它");

            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);
            Assert.Equal(ReportRunStatus.Success, run.Status);
            Assert.False(string.IsNullOrWhiteSpace(run.FilePath),
                "執行紀錄有了，但沒有產出檔——收件匣會是一列點不開的東西");

            // 收件匣那一端真的拿得到。走的是 HTTP 而不是直接讀檔：
            // 檔案存在與「使用者下載得到」是兩件事（中間還有三道權限）。
            var download = await client.GetAsync($"/ReportInbox/Download?runId={run.RunId}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        [Fact]
        public async Task R4_產出的檔案裡真的要有資料列()
        {
            // R-F4 的形狀：收得到檔案、格式正確、零筆。**那次是業主發現的**，
            // 三十幾支全綠的特徵測試都沒看見，因為它們測的是畫面那條出口。
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);

            // ① 先確認 runner 認得到資料列。RowCount 是 result.Rows.Count，
            //    也就是 GetExportRowsAsync 這一層的答案。
            Assert.True(run.RowCount > 0,
                $"報表跑完但零筆——種在 {PlantId} 的出貨單沒有進到 GetExportRowsAsync");

            // ② 再確認那些列真的寫進了檔案。①②之間隔著 ToExportRequest 與
            //    exporter.ToCsv，那一段沒有任何測試——RowCount 對而檔案空
            //    在收件匣裡看起來與正常產出完全一樣。
            var bytes = await client.GetByteArrayAsync($"/ReportInbox/Download?runId={run.RunId}");
            var text = System.Text.Encoding.UTF8.GetString(bytes);

            Assert.Contains(ProductCode, text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task R4_立即執行產生的紀錄要標記為手動觸發()
        {
            // 執行紀錄是稽核軌跡（排程刪掉紀錄也刻意保留），
            // 那麼「這份報表是排程自己跑的，還是有人按了按鈕」就必須分得出來。
            //
            // ⚠️ RunNow 只把 NextRunAt 拉到現在，派工器**無從得知那是人按的**。
            // 少了這個標記，ReportRunHistory 畫面上的「手動」那個分支永遠顯示不出來。
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);
            Assert.Equal(ReportTriggerType.Manual, run.TriggerType);
        }

        /// <summary>
        /// 按下立即執行後系統時鐘回撥，這一輪掃描仍要跑（2026-10-28）。
        /// RunNow 把 NextRunAt 設成按下當時的時鐘；WSL 時鐘回撥數秒時「現在」比它早，
        /// 原本只看 NextRunAt ≤ now 就漏掉——R7 在全量跑時間歇出現「沒有任何執行紀錄」就是這個。
        /// 這裡直接把 NextRunAt 推到未來來模擬回撥。
        /// </summary>
        [Fact]
        public async Task R4_立即執行後時鐘回撥_仍在下一輪掃描執行()
        {
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.ReportDefinitions.Where(d => d.Id == definitionId)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.NextRunAt, DateTime.UtcNow.AddMinutes(1)));
            }
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);
            Assert.Equal(ReportTriggerType.Manual, run.TriggerType);
        }

        [Fact]
        public async Task R4_手動執行過的排程_下一輪要回到排程觸發()
        {
            // 旗標設得起來只做對一半——**清不掉的話那筆排程從此每一輪都標成手動**，
            // 而症狀是紀錄看起來很正常（狀態成功、筆數正確），只有觸發來源一直說謊。
            //
            // 這一支是突變驗證逼出來的：把 AdvanceAsync 裡清旗標那一行刪掉，
            // 上面三支全部照樣綠——因為沒有任何一支跑到第二輪。
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            // 第二輪不經過 RunNow：直接把 NextRunAt 拉到現在，模擬排程自己到期。
            await MakeDueAsync(definitionId);
            await ScanOnceAsync();

            var runs = await RunsAsync(definitionId);
            Assert.Equal(2, runs.Count);
            Assert.Equal(ReportTriggerType.Manual,    runs[0].TriggerType);
            Assert.Equal(ReportTriggerType.Scheduled, runs[1].TriggerType);
        }

        // ── 權限：回應不得洩漏排程存不存在 ──────────────────────────────────

        [Fact]
        public async Task RunNow_沒有更新權限時_排程存不存在都要回同一種結果()
        {
            // 同 controller 的 Index／Create／Edit／Delete 都是「先驗權限再查資料」，
            // 只有 RunNow 相反。順序顛倒的後果不是少擋了誰，而是**回應碼會說話**：
            // 存在的 id 回 403、不存在的 id 回 404，於是無權限的人可以用 id 逐個問
            // 「這筆排程在不在」。同模組的 ReportInbox.Download 為了同一件事
            // 刻意把「不是收件人」也回成 NotFound，立場已經很清楚。
            var definitionId = await SeedScheduleAsync();

            using var client = Client();
            var token = await TokenAsync(client);

            await GrantAsync(canUpdate: false);
            try
            {
                var existing = await PostRunNowAsync(client, definitionId, token);
                var missing  = await PostRunNowAsync(client, 999_000_000, token);

                Assert.Equal(existing.StatusCode, missing.StatusCode);
            }
            finally
            {
                await GrantAsync(canUpdate: true);
            }
        }

        // ── 前置 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 建一筆真的跑得出資料的排程：專屬廠別的一張已過帳出貨單 ＋ 指向它的排程定義。
        /// </summary>
        // ── 失敗那一半（R-4 判準的後半句：「失敗時訊息可讀，不是靜默」）──────────
        //
        // ⚠️ **正常操作做不出一次失敗**：表單的報表下拉只給有效選項，所以畫面上建不出
        // 一筆會失敗的排程。這一項的人工驗收因此天生需要人為注入壞資料——
        // 而那也正是這三支存在的理由：把「注入壞資料」這件事從人工挪到測試裡。
        //
        // 另一條非成功路徑（工具被下架）走的**不是**這裡：`CheckOwnerAsync` 會先答
        // 「報表工具 X 已不存在」，於是那筆排程被標成 SkippedNoPermission（黃色）**並停用**，
        // 到不了 Failed（紅色）。兩者在畫面上長得不一樣，不可混為一談。

        [Fact]
        public async Task R4_立即執行失敗時_執行紀錄留下可讀的原因而不是靜默()
        {
            var definitionId = await SeedScheduleAsync();
            await BreakParamsAsync(definitionId);

            using var client = Client();
            var res = await PostRunNowAsync(client, definitionId);

            // ⚠️ 按鈕照樣回 200——RunNow 只是把 NextRunAt 拉到現在，它不知道後來會失敗。
            // 「按下去成功」與「跑出來成功」之間隔著一個背景服務，這正是判準要分開驗的原因。
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            await ScanOnceAsync();
            var run = await SingleRunAsync(definitionId);

            Assert.Equal(ReportRunStatus.Failed, run.Status);

            var message = run.ErrorMessage ?? "";
            Assert.False(string.IsNullOrWhiteSpace(message), "失敗卻沒有原因＝靜默，正是本判準要防的。");

            // 「可讀」在這裡的操作化定義是兩件事：說得出**發生什麼**，也說得出**該怎麼辦**。
            Assert.Contains("參數已失效", message);
            // 派工器以收件人語系產生內容（ReportDispatcherService 切換 CurrentUICulture），種子收件人是 zh-TW，
            // 所以這裡比對中文是對的；不能用 PzAssert（那是依測試執行緒的文化取字）。
            Assert.Contains("請重新開啟該報表並重新儲存這筆定義", message);

            // 而且不得把內部結構倒給使用者看。訊息裡會夾一段 System.Text.Json 的英文位置資訊
            // （'t' is an invalid start of a value…），那是給工程師定位用的，可以接受；
            // 但型別名與堆疊不行——那會讓一句本來可操作的話變成一面技術牆。
            Assert.DoesNotContain("SmartFactory.MES", message);
            Assert.DoesNotContain("   at ", message);
        }

        [Fact]
        public async Task R4_立即執行失敗的那一列_也要標記為手動觸發()
        {
            // #170 補手動旗標時只驗了成功路徑。失敗那一列同樣要標手動——
            // 而且它比成功那一列更需要：使用者會來執行紀錄找的，正是「我剛才按的那次怎麼了」。
            // 派工器是在**執行之前**取 triggerType 的，所以這一支釘的是那個順序沒有被改掉。
            var definitionId = await SeedScheduleAsync();
            await BreakParamsAsync(definitionId);

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);

            Assert.Equal(ReportRunStatus.Failed, run.Status);
            Assert.Equal(ReportTriggerType.Manual, run.TriggerType);
        }

        [Fact]
        public async Task R4_失敗時不得留下半份可下載的檔案()
        {
            // 失敗卻留著檔案，畫面上會是「紅色的失敗，但下載得到東西」——
            // 那比單純失敗更糟：使用者會下載它，然後拿一份殘缺的資料去用。
            var definitionId = await SeedScheduleAsync();
            await BreakParamsAsync(definitionId);

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);

            Assert.Null(run.FilePath);
            Assert.Equal(0, run.RowCount);

            var download = await client.GetAsync($"/ReportInbox/Download?runId={run.RunId}");
            Assert.NotEqual(HttpStatusCode.OK, download.StatusCode);
        }

        /// <summary>
        /// 把參數 JSON 弄壞。**畫面上做不到**（下拉只給有效選項），但報表改版或欄位移除之後
        /// 真的會發生——<c>ReportParamsInvalidException</c> 的訊息就是為這個情境寫的。
        /// </summary>
        private async Task BreakParamsAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var def = await db.ReportDefinitions.FirstAsync(d => d.Id == definitionId);
            def.ParamsJson = "{ this is not json";
            await db.SaveChangesAsync();
        }

        // ══════════════════════════════════════════════════════════════════
        // R-7：收件人語系真的決定了**檔案內容**
        //
        // 🔴 2026-09-17 盤點出來的缺口。分組那一半原本就驗得紮實
        //    （ReportDispatcherServiceTests：中英混合分出兩筆 run、未設語系沿用 owner、
        //    被擋的收件人不參與分組），ReportExportHeaderTests 也逐鍵確認
        //    zh-TW／en-US／中性語系三格都查得到欄頭。
        //
        //    但**中間那一段沒有任何斷言**：派工器在匯出前設 CultureInfo.CurrentUICulture，
        //    而 `grep CurrentUICulture` 在派工器測試裡命中 0。實測突變——把那一行改成
        //    不套用語系——RPS 單元測試篩選 217 支全綠。也就是英文收件人收到
        //    中文標題的 Excel 時，沒有任何測試會出聲（數字全對，所以也沒人會回報這算 bug）。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 斷言挑<b>欄頭</b>而不是資料列：資料是數字與代碼，兩種語系下長得一模一樣，
        /// 只有欄頭看得出這份檔案是為誰產的。
        ///
        /// <para>⚠️ 同時斷言「另一種語系的字**不在**裡面」。只驗 Contains 的話，
        /// 一份把兩種語系都印出來的檔案（或 fallback 到中性語系剛好回中文鍵值）
        /// 照樣會通過。</para>
        /// </summary>
        [Theory]
        [InlineData("en-US", "Shipped Qty", "出貨量")]
        [InlineData("zh-TW", "出貨量", "Shipped Qty")]
        public async Task R7_收件人語系決定產出檔的欄頭(
            string language, string expected, string forbidden)
        {
            var definitionId = await SeedScheduleAsync(recipientLanguage: language);

            using var client = Client();
            await PostRunNowAsync(client, definitionId);
            await ScanOnceAsync();

            var run = await SingleRunAsync(definitionId);
            Assert.Equal(language, run.Language);   // 分組對了，才輪得到內容

            var bytes = await client.GetByteArrayAsync($"/ReportInbox/Download?runId={run.RunId}");
            var text  = System.Text.Encoding.UTF8.GetString(bytes);

            // 只看第一行。整份檔案裡搜字串的話，資料列裡剛好有同樣的字就會假綠。
            var header = text.Split('\n')[0];

            Assert.Contains(expected, header, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, header, StringComparison.Ordinal);
        }

        private async Task<int> SeedScheduleAsync(string? recipientLanguage = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var adminId = (await db.Users.AsNoTracking().FirstAsync(u => u.UserName == "admin")).Id;

            var shipmentId = NamePrefix + Guid.NewGuid().ToString("N")[..8];
            db.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = shipmentId,
                ShipDate        = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                TypeCode        = "GS",
                CompanyCode     = "ITEST-RUNNOW-C1",
                Status          = DocStatus.Posted,
                PlantId         = PlantId,
                CreatedBy       = adminId,
                CreatedByRoleId = RoleId,   // 少了它會被 IDataAuthEntity 的全域過濾隱藏，報表查到 0 筆
            });
            db.GoodsShipmentLines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = shipmentId,
                LineNo          = 1,
                ProductCode     = ProductCode,
                ShipQty         = 10,
            });

            // 期間一律用相對期間（§5.2.1：排程存絕對日期等於每次都寄同一份）。
            // Last30Days 而非 Today 是刻意的——「今天」由廠別時區解算，
            // 在 UTC 午夜前後跑會落到隔天，變成一支隨時間飄移的紅燈。
            var paramsJson = JsonSerializer.Serialize(
                new ReturnRateRequest { PlantId = PlantId, Period = RelativePeriod.Last30Days }, Json);

            var def = new ReportDefinition
            {
                Name            = NamePrefix + Guid.NewGuid().ToString("N")[..8],
                OwnerUserId     = adminId,
                OwnerRoleId     = RoleId,
                CreatedByRoleId = RoleId,
                PlantId         = PlantId,
                ToolKey         = TargetTool,
                ParamsJson      = paramsJson,
                OutputFormat    = "Csv",
                ScheduleKind    = ReportScheduleKind.Daily,
                ScheduleHour    = 3,
                ScheduleMinute  = 0,
                IsActive        = true,
                NextRunAt       = DateTime.UtcNow.AddDays(1),   // 還沒到期：只有 RunNow 才會讓它跑
                CreatedAt       = DateTime.UtcNow,
            };
            db.ReportDefinitions.Add(def);
            await db.SaveChangesAsync();

            // 語系存在**收件人那一列**上，不是使用者主檔——所以驗語系不必去動
            // 跨進程共用的 AspNetUsers（動了要記得還原，而還原失敗是無聲的）。
            db.ReportRecipients.Add(new ReportRecipient
            {
                DefinitionId = def.Id, UserId = adminId, Language = recipientLanguage,
            });
            await db.SaveChangesAsync();

            return def.Id;
        }

        /// <summary>
        /// 直接呼叫派工器掃一輪，不等背景迴圈。
        ///
        /// <para>掃描會撈**所有**到期的排程，所以本檔一律只斷言自己那一筆
        /// （<c>SingleRunAsync(definitionId)</c>）——別人的排程被順帶跑掉
        /// 不影響斷言，反過來若改成數全表筆數就會隨其他測試飄。</para>
        /// </summary>
        private async Task ScanOnceAsync()
        {
            var dispatcher = _factory.Services.GetServices<IHostedService>()
                .OfType<ReportDispatcherService>().Single();

            await dispatcher.ScanOnceAsync(CancellationToken.None);
        }

        private async Task<ReportRun> SingleRunAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var runs = await db.ReportRuns.AsNoTracking()
                .Where(r => r.DefinitionId == definitionId).ToListAsync();

            return Assert.Single(runs);
        }

        /// <summary>依執行時刻排序的全部紀錄。跨輪次的斷言不能用 <c>Assert.Single</c>。</summary>
        private async Task<List<ReportRun>> RunsAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await db.ReportRuns.AsNoTracking()
                .Where(r => r.DefinitionId == definitionId)
                .OrderBy(r => r.RunId)
                .ToListAsync();
        }

        /// <summary>讓排程到期，但**不經過 RunNow**——否則手動旗標又會被設起來。</summary>
        private async Task MakeDueAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var def = await db.ReportDefinitions.FirstAsync(d => d.Id == definitionId);
            def.NextRunAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        private async Task<DateTime?> NextRunAtAsync(int definitionId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return (await db.ReportDefinitions.AsNoTracking()
                .FirstAsync(d => d.Id == definitionId)).NextRunAt;
        }

        private async Task<HttpResponseMessage> PostRunNowAsync(
            HttpClient client, int definitionId, string? token = null)
        {
            token ??= await TokenAsync(client);

            return await client.PostAsync($"/ReportSchedule/RunNow?id={definitionId}",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                }));
        }

        private static async Task<string> TokenAsync(HttpClient client)
        {
            var html = await (await client.GetAsync("/ReportSchedule")).Content.ReadAsStringAsync();

            var m = Regex.Match(html,
                @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(m.Success, "報表排程畫面上找不到 AntiForgery token");
            return m.Groups[1].Value;
        }

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>本檔動到的是 CanUpdate（RunNow 查的那一個），讀取權一律保持開啟。</summary>
        private async Task GrantAsync(bool canUpdate)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            foreach (var key in new[] { MenuKey, "ReportInbox", TargetTool })
            {
                var perm = await db.MenuPermissions
                    .FirstOrDefaultAsync(p => p.RoleId == RoleId && p.MenuKey == key);

                if (perm == null)
                {
                    perm = new MenuPermission { RoleId = RoleId, MenuKey = key };
                    db.MenuPermissions.Add(perm);
                }

                perm.CanRead = true;
                if (key == MenuKey) perm.CanUpdate = canUpdate;
            }

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// 產出檔要跟著紀錄一起刪，否則每跑一輪就在 <c>App_Data/reports</c>
        /// 留一個沒人清理的孤兒檔——那正是 R-9 在守的東西。
        /// </summary>
        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var files = scope.ServiceProvider.GetRequiredService<IReportFileStore>();

            // ⚠️ 出貨單是 IPlantEntity ＋ IDataAuthEntity，**清理這一端也受全域過濾管轄**。
            // 不設上下文的話過濾條件會變成 PlantId IS NULL，查不到自己剛種在 ZZRUNNOW 的那幾筆，
            // 於是 RemoveRange 收到空集合、SaveChanges 成功、測試全綠——而資料留在共用測試庫裡。
            // 種資料那一端不受影響（新增不走過濾），所以這個缺口只在清理這側，且完全無聲。
            db.CurrentPlantId = PlantId;
            db.CurrentRoleId  = RoleId;

            var defs = await db.ReportDefinitions
                .Where(d => d.Name.StartsWith(NamePrefix)).ToListAsync();

            if (defs.Count > 0)
            {
                var ids = defs.Select(d => d.Id).ToList();
                var runs = await db.ReportRuns.Where(r => ids.Contains(r.DefinitionId)).ToListAsync();

                foreach (var path in runs.Select(r => r.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)))
                    files.Delete(path!);

                db.ReportRuns.RemoveRange(runs);
                db.ReportRecipients.RemoveRange(
                    await db.ReportRecipients.Where(r => ids.Contains(r.DefinitionId)).ToListAsync());
                db.ReportDefinitions.RemoveRange(defs);
            }

            var lines = await db.GoodsShipmentLines
                .Where(l => l.GoodsShipmentId.StartsWith(NamePrefix)).ToListAsync();
            var shipments = await db.GoodsShipments
                .Where(s => s.GoodsShipmentId.StartsWith(NamePrefix)).ToListAsync();

            db.GoodsShipmentLines.RemoveRange(lines);
            db.GoodsShipments.RemoveRange(shipments);

            await db.SaveChangesAsync();
        }
    }
}
