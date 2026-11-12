using SmartFactory.MES.Models.Jobs;

namespace SmartFactory.MES.Services.Jobs
{
    /// <summary>
    /// 每日固定時刻執行一次的背景排程基底。
    ///
    /// <para><b>為什麼需要這個基底</b>：原本六支每日排程（資料稽核 02:00、AuditLog 清理 03:00、
    /// PasswordHistory 04:00、報表清理 04:00、簽核歷程 05:00、MRP 展開單 06:00）各自複製同一段
    /// 「算出下一個 RunAtHour → <c>Task.Delay</c> → 執行」的迴圈。那個寫法有<b>兩種</b>互相獨立
    /// 的失效方式，兩種都完全無聲——沒有例外、沒有警告，只是靜靜地在一個等不到的時刻上等待。</para>
    ///
    /// <para><b>失效一：非 24 小時開機</b>。早上 08:00 開機算出的下一次是隔天 02:00，傍晚關機時
    /// 進程結束，隔天開機再重算又是隔天 02:00——一次都跑不到。實地驗證（2026-08-21）：連續八天
    /// 的應用程式日誌沒有任何一天跨過凌晨，<c>DataAuditRuns</c> 因此一筆都沒有。
    /// <b>解法</b>：進入等待迴圈前先補跑一次（<see cref="StartupCatchUpConfigKey"/>）。</para>
    ///
    /// <para><b>失效二：機器睡眠</b>。<c>Task.Delay</c> 底層是計時器，而計時器在 Windows 睡眠
    /// 期間<b>不前進</b>——它等的是「清醒時間」而不是牆上時鐘。喚醒後它既不補觸發、也不提早，
    /// 只是繼續等剩下的清醒時間。實地驗證（2026-09-26 筆電 IIS）：w3wp 進程連續存活 45 小時
    /// 未回收，但累計清醒僅 2 小時 21 分，於是一個 7 小時 43 分的 Delay 一次都沒有回來，
    /// 當天兩支固定時刻排程都是零次。啟動補跑救不了這種——應用程式集區從頭到尾沒有重啟過。
    /// <b>解法</b>：不再用一個長 Delay 睡到觸發時刻，改成每次最多睡
    /// <see cref="PollInterval"/>，醒來重讀<b>牆上時鐘</b>判斷該不該跑。睡眠會讓某一輪睡超過
    /// 五分鐘，但下一輪一定會看到「已經過了觸發時刻」而補跑。</para>
    ///
    /// <para><b>重複執行的防護</b>分兩層。外層是本基底自己記的「最近一格觸發時刻是否已服務」
    /// （<c>servedSlot</c>），它讓輪詢迴圈每天只在觸發時刻後觸發一次，而不是此後每五分鐘一次；
    /// 內層是 <see cref="ShouldRunAsync"/>，讓子類別依自己的執行紀錄決定，且補跑與定時兩條路徑
    /// 共用同一道判定——否則 01:00 開機補跑後，一小時後的 02:00 定時又會再跑一次。
    /// 清理類工作是冪等刪除，預設不設限；會留下執行紀錄的工作（如資料稽核）應覆寫成
    /// 「今天尚未跑過才跑」。</para>
    /// </summary>
    public abstract class DailyJobServiceBase : BackgroundService
    {
        /// <summary>啟動補跑的組態鍵。設為 false 可關閉補跑（僅保留定時），供測試環境使用。</summary>
        public const string StartupCatchUpConfigKey = "Jobs:RunAtStartup";

        /// <summary>
        /// 排程時區的組態鍵，值為 IANA 時區 ID（例如 <c>Asia/Taipei</c>）。
        ///
        /// <para><b>為什麼需要明講</b>：原本這個基底用 <c>DateTimeOffset.Now</c> 判斷「幾點了」，
        /// 也就是隱含地採用<b>伺服器時區</b>。伺服器時區會隨部署環境改變——本專案的開發與 CI
        /// 就刻意跑 <c>TZ=UTC</c>（見 DEVELOPMENT.md），於是 <c>RunAtHour = 2</c>（原意是離峰的
        /// 凌晨兩點）實際會落在當地上午十點，正好是系統最忙的時候。而且這件事<b>完全無聲</b>：
        /// 沒有任何錯誤，日誌上也看不出現在採用的是哪一個時區。</para>
        ///
        /// <para>離峰時段是<b>營運地點</b>的概念，不是伺服器機箱所在時區的概念，所以要由組態明講。
        /// 六支排程都是全系統性的（清理與稽核），沒有分廠的語意，故這裡是單一時區而非逐廠。</para>
        /// </summary>
        public const string TimeZoneConfigKey = "Jobs:TimeZone";

        /// <summary>
        /// 等待長度算成零（正好站在觸發時刻上）時改睡這麼久。這是防空轉的保險，
        /// <b>不是</b>輪詢間隔的全域下限——設成下限會讓測試無法把輪詢調快。
        /// </summary>
        private static readonly TimeSpan ZeroWaitFallback = TimeSpan.FromSeconds(1);

        private TimeZoneInfo? _jobTimeZone;

        /// <summary>
        /// 判斷排程時刻所用的時區。未設定 <see cref="TimeZoneConfigKey"/> 時退回伺服器時區，
        /// 但會留下警告——退回本身可以接受，<b>悄悄地</b>退回不行。
        /// </summary>
        protected TimeZoneInfo JobTimeZone => _jobTimeZone ??= ResolveJobTimeZone();

        private TimeZoneInfo ResolveJobTimeZone()
        {
            var id = JobConfiguration[TimeZoneConfigKey];
            if (string.IsNullOrWhiteSpace(id))
            {
                JobLogger.LogWarning(
                    "{JobName}：未設定 {Key}，排程時刻改依伺服器時區 {Zone} 判斷。" +
                    "伺服器時區會隨部署環境改變，請在 appsettings 明確指定營運地點的時區。",
                    JobName, TimeZoneConfigKey, TimeZoneInfo.Local.Id);
                return TimeZoneInfo.Local;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // 打錯時區 ID 不該讓排程整個停擺，但也絕不能靜靜地跑在別的時區上。
                JobLogger.LogError(ex,
                    "{JobName}：{Key} 設為 \"{Id}\"，系統找不到這個時區，改依伺服器時區 {Zone} 判斷",
                    JobName, TimeZoneConfigKey, id, TimeZoneInfo.Local.Id);
                return TimeZoneInfo.Local;
            }
        }

        /// <summary>
        /// 排程時區的「現在」。所有時刻判斷都必須經過這裡，不可直接用 <c>DateTimeOffset.Now</c>。
        /// virtual 是為了讓測試能推進假時鐘——睡眠補跑的行為沒有別的方法驗得到。
        /// </summary>
        protected virtual DateTimeOffset JobNow() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, JobTimeZone);

        /// <summary>每日觸發時刻（<see cref="JobTimeZone"/> 的小時數）。</summary>
        protected abstract int RunAtHour { get; }

        /// <summary>排程名稱，只用於日誌訊息。</summary>
        protected abstract string JobName { get; }

        /// <summary>子類別的 logger。基底不自行取得，以免更動既有建構子簽章。</summary>
        protected abstract ILogger JobLogger { get; }

        /// <summary>組態來源，用於解析 <see cref="StartupCatchUpConfigKey"/>。</summary>
        protected abstract IConfiguration JobConfiguration { get; }

        /// <summary>
        /// 啟動後延遲多久才補跑。刻意留一段緩衝，避免與應用程式啟動（migration、
        /// 快取預熱、第一批使用者請求）爭用資料庫連線。
        /// </summary>
        protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(60);

        /// <summary>
        /// 等待迴圈每一輪最多睡多久。
        ///
        /// <para>這個值<b>不是</b>排程精度的旋鈕，而是「睡眠後最晚多久會發現時刻已過」的上限：
        /// 觸發時刻本身仍由牆上時鐘判斷，正常運轉時誤差最多一個間隔。調短只會增加空轉喚醒，
        /// 對耗電敏感的筆電部署反而不利；調長則會讓喚醒後的補跑更晚。五分鐘是兩者的折衷。</para>
        /// </summary>
        protected virtual TimeSpan PollInterval => TimeSpan.FromMinutes(5);

        /// <summary>實際工作內容。</summary>
        protected abstract Task ExecuteJobAsync(CancellationToken ct);

        /// <summary>
        /// 覆寫成非 null 即啟用共用的執行紀錄（<see cref="ScheduledJobRun"/>）：
        /// 每次成功或失敗都落地一列，且 <see cref="ShouldRunAsync"/> 的預設實作會據此去重。
        ///
        /// <para><b>為什麼是 opt-in 而不是一律啟用</b>：六支既有排程都是冪等的清理刪除，
        /// 一天多跑幾次無害，強制去重只是多一次資料庫往返。真正需要的是「很貴的」
        /// （ETA 全量校準）與「會對外送東西的」（生產準備提醒推播）那兩類。
        /// 日後要讓清理類也留紀錄（觀測用），各自覆寫這一個屬性即可，不必改基底。</para>
        ///
        /// <para>取 <c>IServiceScopeFactory</c> 而不是直接要 <c>IJobRunRecorder</c>，
        /// 是因為背景服務是 singleton 而記錄器是 scoped（它相依 DbContext）。</para>
        /// </summary>
        protected virtual IServiceScopeFactory? JobScopeFactory => null;

        /// <summary>
        /// 是否該執行。補跑與定時共用此判定。
        ///
        /// <para>未啟用 <see cref="JobScopeFactory"/> 時恆為 true——清理類工作是冪等刪除，
        /// 一天多跑幾次無害。啟用後預設改為「今天（排程時區的當地日期）尚未<b>成功</b>
        /// 執行過才跑」。覆寫時請以當地日期為準，不要直接比對 UTC 日界。</para>
        /// </summary>
        protected virtual async Task<bool> ShouldRunAsync(DateTimeOffset now, CancellationToken ct)
        {
            var factory = JobScopeFactory;
            if (factory is null) return true;

            try
            {
                using var scope = factory.CreateScope();
                var recorder = scope.ServiceProvider.GetRequiredService<IJobRunRecorder>();
                return !await recorder.HasSucceededOnAsync(JobName, DateOnly.FromDateTime(now.Date), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 查不到紀錄時**照跑**（fail-open）。理由是這兩類工作的實際內容本來就要讀
                // 同一個資料庫：紀錄查不到通常代表資料庫不可用，工作接著也會失敗，不會
                // 真的重複做事。反過來 fail-closed 會讓一次短暫的連線抖動吃掉一整天。
                JobLogger.LogWarning(ex,
                    "{JobName}：查詢執行紀錄失敗，無法判斷今天是否已執行過，保守起見照跑", JobName);
                return true;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            if (JobConfiguration.GetValue<bool?>(StartupCatchUpConfigKey) ?? true)
                await TryRunAsync("啟動補跑", stoppingToken);

            // 啟動當下「最近一格已過的觸發時刻」視為已服務：17:00 的工作若在 18:00 才啟動，
            // 那一格屬於啟動補跑的責任範圍，不該由定時路徑再跑一次。這與改寫前的語意一致
            // （DelayUntilNextRun 在已過觸發時刻時算的就是明天），也讓 Jobs:RunAtStartup=false
            // 仍然真的代表「今天不補跑」。
            var servedSlot = SlotDateOf(JobNow());
            DateOnly? announced = null;

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = JobNow();
                var slot = SlotDateOf(now);

                if (slot > servedSlot)
                {
                    // 遲到多久是唯一能事後分辨「排程正常」與「機器睡過頭」的訊號，
                    // 而且必須在這裡記——事發當下沒有別的地方看得出來。
                    var lateBy = now - SlotInstant(slot);
                    if (lateBy > PollInterval + TimeSpan.FromMinutes(1))
                    {
                        JobLogger.LogWarning(
                            "{JobName}：{Slot:yyyy-MM-dd} {Hour:00}:00（{Zone}）的觸發時刻已過 {Late} 才被發現，" +
                            "現在補跑。常見原因是機器在該時刻睡眠或應用程式未啟動。",
                            JobName, slot.ToDateTime(TimeOnly.MinValue), RunAtHour, JobTimeZone.Id, lateBy);
                    }

                    servedSlot = slot;
                    announced = null;
                    await TryRunAsync("每日排程", stoppingToken);
                    continue;
                }

                var delay = DelayUntilNextRun(now);
                var next = now + delay;

                // 「下一次幾點跑」以前完全看不出來，時區一旦錯了也無從察覺。但輪詢是每五分鐘
                // 一輪，逐輪都記就是一天 288 行 × 六支排程的雜訊，故 Information 只在目標日
                // 改變時記一次（一天一行），逐輪那行降到 Debug——要診斷「迴圈還活著嗎」時才開。
                var nextDate = DateOnly.FromDateTime(next.Date);
                if (announced != nextDate)
                {
                    JobLogger.LogInformation("{JobName}：下一次執行 {Next:yyyy-MM-dd HH:mm}（{Zone}）",
                        JobName, next, JobTimeZone.Id);
                    announced = nextDate;
                }
                else
                {
                    JobLogger.LogDebug("{JobName}：等待中，下一次執行 {Next:yyyy-MM-dd HH:mm}（{Zone}）",
                        JobName, next, JobTimeZone.Id);
                }

                var wait = delay < PollInterval ? delay : PollInterval;
                if (wait <= TimeSpan.Zero) wait = ZeroWaitFallback;

                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// <paramref name="now"/> 當下「最近一格已經過去的觸發時刻」屬於哪一天。
        /// 還沒到今天的觸發時刻時，最近一格是<b>昨天</b>的。
        /// </summary>
        /// <remarks>
        /// 迴圈比對的是這個「格子」而不是「現在幾點」，睡眠補跑才會正確：機器在 16:00 睡著、
        /// 隔天 09:00 醒來，RunAtHour = 17 的工作此刻 09:00 尚未到今天的 17:00，但最近一格是
        /// <b>昨天</b> 17:00，比啟動時記下的前天那格新，於是判定為漏跑並立刻補上。
        /// 若改用「now.Hour &gt;= RunAtHour」判斷，這個跨日漏跑會整個看不到。
        /// </remarks>
        private DateOnly SlotDateOf(DateTimeOffset now)
            => DateOnly.FromDateTime(now.Hour >= RunAtHour ? now.Date : now.Date.AddDays(-1));

        /// <summary>某一格觸發時刻的實際時間點。位移取自目標時刻，夏令時間切換當天才不會差一小時。</summary>
        private DateTimeOffset SlotInstant(DateOnly slot)
        {
            var target = slot.ToDateTime(TimeOnly.MinValue).AddHours(RunAtHour);
            return new DateTimeOffset(target, JobTimeZone.GetUtcOffset(target));
        }

        /// <summary>
        /// 距離下一個 <see cref="RunAtHour"/> 還要等多久。等待迴圈拿它與
        /// <see cref="PollInterval"/> 取小值，所以它現在的角色是「還早的話最多可以睡到哪」，
        /// 而不是一口氣睡到觸發時刻。
        /// </summary>
        /// <remarks>
        /// 目標時刻<b>必須自己帶上排程時區的位移</b>。<c>DateTime</c> → <c>DateTimeOffset</c> 的
        /// 隱含轉換套用的是<b>伺服器</b>時區，排程時區與伺服器時區不同時（例如伺服器跑 UTC
        /// 而營運在 +08），算出來的等待長度會整整差一個時差，而且編譯器不會有任何抱怨。
        /// 位移取自目標時刻而非此刻，夏令時間切換當天才不會差一小時。
        /// </remarks>
        protected TimeSpan DelayUntilNextRun(DateTimeOffset now)
        {
            var target = now.Date.AddDays(now.Hour >= RunAtHour ? 1 : 0).AddHours(RunAtHour);
            var next = new DateTimeOffset(target, JobTimeZone.GetUtcOffset(target));
            var delay = next - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        private async Task TryRunAsync(string trigger, CancellationToken ct)
        {
            var now = JobNow();
            var startedUtc = DateTime.UtcNow;

            try
            {
                if (!await ShouldRunAsync(now, ct))
                {
                    JobLogger.LogDebug("{JobName}：{Trigger} 判定今日已執行過，略過", JobName, trigger);
                    return;
                }

                await ExecuteJobAsync(ct);
                await RecordRunAsync(now, trigger, startedUtc, succeeded: true, error: null, ct);
            }
            catch (OperationCanceledException) { /* 關機，忽略 */ }
            catch (Exception ex)
            {
                // 單次失敗不得中斷迴圈，否則一次暫時性的 DB 斷線會讓排程永久停擺，
                // 而那同樣是無聲的——直到有人發現清理已經好幾個月沒跑。
                JobLogger.LogError(ex, "{JobName} 執行失敗（{Trigger}）", JobName, trigger);

                // 失敗也要留紀錄，否則「今天到底試過沒」只剩日誌可查。去重只看成功的列，
                // 所以這一列不會擋住當天稍後的重試。
                await RecordRunAsync(now, trigger, startedUtc, succeeded: false, error: ex.Message, ct);
            }
        }

        private async Task RecordRunAsync(
            DateTimeOffset now, string trigger, DateTime startedUtc,
            bool succeeded, string? error, CancellationToken ct)
        {
            var factory = JobScopeFactory;
            if (factory is null) return;

            try
            {
                using var scope = factory.CreateScope();
                var recorder = scope.ServiceProvider.GetRequiredService<IJobRunRecorder>();
                await recorder.RecordAsync(new ScheduledJobRun
                {
                    JobName        = JobName,
                    JobDate        = DateOnly.FromDateTime(now.Date),
                    Trigger        = trigger,
                    StartedAtUtc   = startedUtc,
                    CompletedAtUtc = DateTime.UtcNow,
                    Succeeded      = succeeded,
                    // 訊息可能很長（EF 的例外常帶整段 SQL），欄位上限 1000。
                    Error          = error is { Length: > 1000 } ? error[..1000] : error,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 寫不進紀錄絕不能讓工作本身變成失敗——那會把「觀測用的附屬品」
                // 升級成「主要流程的單點故障」。
                JobLogger.LogWarning(ex, "{JobName}：執行紀錄寫入失敗（不影響工作本身）", JobName);
            }
        }

        /// <summary>
        /// 把「當地日期的今天」換算成 UTC 區間，供 <see cref="ShouldRunAsync"/> 比對存為 UTC
        /// 的執行紀錄時間戳。直接拿 UTC 的日界去比，在 +08 會把今天 08:00 之前的執行算成昨天。
        /// </summary>
        protected static (DateTime FromUtc, DateTime ToUtc) LocalDayRangeUtc(DateTimeOffset now)
        {
            var fromUtc = new DateTimeOffset(now.Date, now.Offset).UtcDateTime;
            return (fromUtc, fromUtc.AddDays(1));
        }
    }
}
