using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Models.Jobs;
using SmartFactory.MES.Services.Jobs;
using Xunit;

namespace SmartFactory.MES.Tests.Services
{
    /// <summary>
    /// 每日排程基底的執行時機。
    ///
    /// <para>這組測試存在的原因：原本六支排程各自複製「算出下一個 RunAtHour → Task.Delay」的
    /// 迴圈，在非 24 小時開機的環境下一次都不會執行，而且沒有任何測試會發現——因為沒有任何
    /// 測試碰過 ExecuteAsync。所以這裡測的不是「幾點跑」，而是<b>「到底會不會跑」</b>。</para>
    /// </summary>
    public class DailyJobServiceBaseTests
    {
        private sealed class FakeJob : DailyJobServiceBase
        {
            private readonly IConfiguration _config;
            private int _runCount;

            private readonly object _clockLock = new();
            private DateTimeOffset? _fakeNow;

            public FakeJob(IConfiguration config) => _config = config;

            /// <summary>
            /// 假時鐘。睡眠漏跑的行為<b>只能</b>這樣驗——真實時鐘沒有辦法在測試裡跳過一天，
            /// 而那正是這個缺陷唯一的觸發條件。設 null 則沿用真實時鐘（既有測試就是這樣）。
            /// </summary>
            public DateTimeOffset? FakeNow
            {
                get { lock (_clockLock) return _fakeNow; }
                set { lock (_clockLock) _fakeNow = value; }
            }

            private int _nowCalls;

            /// <summary>
            /// JobNow 被讀過幾次。用來確定「等待迴圈已經跑過第一輪」——迴圈進場前會先讀一次
            /// 時鐘記下 servedSlot，測試若在那之前就把時鐘往前跳，起點會連同跳躍一起被記下去，
            /// 於是漏跑永遠測不出來（而且是時快時慢的假綠）。所以不用 sleep 等，用這個數。
            /// </summary>
            public int NowCalls => Volatile.Read(ref _nowCalls);

            protected override DateTimeOffset JobNow()
            {
                Interlocked.Increment(ref _nowCalls);
                return FakeNow ?? base.JobNow();
            }

            /// <summary>設為非 null 即啟用共用執行紀錄（等同衍生類別覆寫 JobScopeFactory）。</summary>
            public IServiceScopeFactory? ScopeFactoryOverride { get; set; }
            protected override IServiceScopeFactory? JobScopeFactory => ScopeFactoryOverride;

            /// <summary>把輪詢調快，測試才不必真的等五分鐘。</summary>
            public TimeSpan PollOverride { get; set; } = TimeSpan.FromMilliseconds(20);
            protected override TimeSpan PollInterval => PollOverride;

            public int RunCount => Volatile.Read(ref _runCount);

            /// <summary>
            /// 覆寫 ShouldRunAsync 的結果。**預設 null＝走基底的判定**——原本這裡寫死回傳
            /// 一個 bool，等於把基底的預設實作整個蓋掉，執行紀錄去重永遠測不到
            /// （寫完第一版測試時就是這樣，紅了才發現替身自己擋在中間）。
            /// </summary>
            public bool? ShouldRun { get; set; }

            public Exception? ThrowOnRun { get; set; }

            // 觸發時刻預設為「建立當下的十二小時後」，確保測試期間絕不會碰到定時路徑——
            // 這樣跑起來的每一次都只能來自啟動補跑。排程時刻的算式另有測試，會指定固定值。
            //
            // ⚠️ 必須第一次讀取時算一次就固定。原本是每次讀取都重算 (Hour + 1)，有兩個整點競態：
            //  · 舊的 Delay 迴圈：觸發時刻是「下一個整點」，CI 執行緒飢餓讓 Task.Delay(200)
            //    拖到 52 秒、跨過整點時，定時路徑真的觸發（10/1 #185／#193 在 00:59Z 實際紅過）。
            //  · 輪詢迴圈：每次都重算的 RunAtHour 會讓 SlotDateOf 在某個整點從「昨天」跳到
            //    「今天」，判成新的一格而觸發（+1 在 23:00、+12 在 12:00）。
            // 固定在十二小時後，兩種迴圈在測試期間都碰不到觸發時刻。
            public int? RunAtHourOverride { get; set; }
            private int? _defaultRunAtHour;
            protected override int RunAtHour
                => RunAtHourOverride ?? (_defaultRunAtHour ??= (JobNow().Hour + 12) % 24);
            protected override string JobName => "測試排程";
            protected override ILogger JobLogger => NullLogger.Instance;
            protected override IConfiguration JobConfiguration => _config;
            protected override TimeSpan StartupDelay => TimeSpan.Zero;

            protected override async Task<bool> ShouldRunAsync(DateTimeOffset now, CancellationToken ct)
                => ShouldRun ?? await base.ShouldRunAsync(now, ct);

            protected override Task ExecuteJobAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref _runCount);
                if (ThrowOnRun != null) throw ThrowOnRun;
                return Task.CompletedTask;
            }

            public static (DateTime FromUtc, DateTime ToUtc) DayRange(DateTimeOffset now)
                => LocalDayRangeUtc(now);

            public TimeZoneInfo Zone => JobTimeZone;
            public TimeSpan DelayFrom(DateTimeOffset now) => DelayUntilNextRun(now);
        }

        private static FakeJob Make(bool? runAtStartup = null, string? timeZoneId = null)
        {
            var settings = new Dictionary<string, string?>();
            if (runAtStartup.HasValue)
                settings[DailyJobServiceBase.StartupCatchUpConfigKey] = runAtStartup.Value ? "true" : "false";
            if (timeZoneId != null)
                settings[DailyJobServiceBase.TimeZoneConfigKey] = timeZoneId;

            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            return new FakeJob(config);
        }

        /// <summary>台北時區、指定假時鐘起點、關閉啟動補跑的排程——用來單獨驗定時路徑。</summary>
        private static FakeJob MakeScheduled(DateTimeOffset startAt, int runAtHour, bool catchUp = false)
        {
            var job = Make(runAtStartup: catchUp, timeZoneId: "Asia/Taipei");
            job.FakeNow = startAt;
            job.RunAtHourOverride = runAtHour;
            return job;
        }

        private static DateTimeOffset Taipei(int y, int mo, int d, int h, int mi = 0)
            => new(y, mo, d, h, mi, 0, TimeSpan.FromHours(8));

        /// <summary>
        /// 不碰資料庫的執行紀錄替身。這張表的全部價值就是去重，而去重要驗得到，
        /// 不能只靠「跑起來看看」。
        /// </summary>
        private sealed class FakeRecorder : IJobRunRecorder
        {
            private readonly object _lock = new();
            private readonly List<ScheduledJobRun> _records = new();
            private readonly HashSet<(string Job, DateOnly Date)> _succeeded = new();

            public Exception? ThrowOnQuery { get; set; }
            public Exception? ThrowOnRecord { get; set; }

            public void MarkSucceeded(string jobName, DateOnly date)
            {
                lock (_lock) _succeeded.Add((jobName, date));
            }

            public ScheduledJobRun[] Snapshot()
            {
                lock (_lock) return _records.ToArray();
            }

            public Task<bool> HasSucceededOnAsync(string jobName, DateOnly jobDate, CancellationToken ct = default)
            {
                if (ThrowOnQuery != null) throw ThrowOnQuery;
                lock (_lock) return Task.FromResult(_succeeded.Contains((jobName, jobDate)));
            }

            public Task RecordAsync(ScheduledJobRun run, CancellationToken ct = default)
            {
                if (ThrowOnRecord != null) throw ThrowOnRecord;
                lock (_lock)
                {
                    _records.Add(run);
                    if (run.Succeeded) _succeeded.Add((run.JobName, run.JobDate));
                }
                return Task.CompletedTask;
            }
        }

        private static IServiceScopeFactory ScopeFactoryFor(IJobRunRecorder recorder)
            => new ServiceCollection()
                .AddSingleton(recorder)
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();

        /// <summary>台北時區、指定假時鐘、已啟用執行紀錄的排程。</summary>
        private static FakeJob MakeRecorded(FakeRecorder recorder, DateTimeOffset now)
        {
            var job = Make(timeZoneId: "Asia/Taipei");
            job.FakeNow = now;
            job.ScopeFactoryOverride = ScopeFactoryFor(recorder);
            return job;
        }

        /// <summary>啟動並等到等待迴圈確實跑過第一輪，之後撥動假時鐘才有意義。</summary>
        private static async Task StartAndEnterLoopAsync(FakeJob job)
        {
            var before = job.NowCalls;
            await job.StartAsync(CancellationToken.None);
            Assert.True(await WaitForAsync(() => job.NowCalls >= before + 2),
                "等待迴圈沒有進場");
        }

        /// <summary>輪詢等待，避免用固定 sleep 讓測試在慢機器上不穩。</summary>
        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }

        [Fact(DisplayName = "啟動後即補跑一次，不必等到 RunAtHour")]
        public async Task Runs_At_Startup_Without_Waiting_For_Scheduled_Hour()
        {
            var job = Make();

            await job.StartAsync(CancellationToken.None);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran, "啟動補跑沒有執行——這正是原本六支排程永遠跑不到的缺陷");
            Assert.Equal(1, job.RunCount);
        }

        [Fact(DisplayName = "ShouldRunAsync 為 false 時不執行（同日去重）")]
        public async Task Skips_When_ShouldRun_Is_False()
        {
            var job = Make();
            job.ShouldRun = false;

            await job.StartAsync(CancellationToken.None);
            await Task.Delay(200);
            await job.StopAsync(CancellationToken.None);

            Assert.Equal(0, job.RunCount);
        }

        [Fact(DisplayName = "組態 Jobs:RunAtStartup=false 可關閉補跑")]
        public async Task Startup_CatchUp_Can_Be_Disabled_By_Configuration()
        {
            var job = Make(runAtStartup: false);

            await job.StartAsync(CancellationToken.None);
            await Task.Delay(200);
            await job.StopAsync(CancellationToken.None);

            Assert.Equal(0, job.RunCount);
        }

        [Fact(DisplayName = "未設組態時預設補跑")]
        public async Task Startup_CatchUp_Defaults_To_Enabled()
        {
            var job = Make(runAtStartup: null);

            await job.StartAsync(CancellationToken.None);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran);
        }

        [Fact(DisplayName = "工作拋例外不會讓排程停擺")]
        public async Task Exception_Does_Not_Kill_The_Loop()
        {
            var job = Make();
            job.ThrowOnRun = new InvalidOperationException("DB 暫時斷線");

            await job.StartAsync(CancellationToken.None);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            // 例外被吞下後仍應進入等待迴圈，StopAsync 才能正常收工而不是回報 faulted。
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran);
        }

        [Theory(DisplayName = "當地日界換算成 UTC 區間（不可用 UTC 日界）")]
        [InlineData(8)]   // 台北
        [InlineData(0)]   // UTC
        [InlineData(-5)]  // 美東
        public void LocalDayRange_Uses_Local_Midnight_Not_Utc(int offsetHours)
        {
            var offset = TimeSpan.FromHours(offsetHours);
            // 當地時間 2026-08-21 07:00——在 +08 這個時刻的 UTC 日期還是 8/20，
            // 若直接拿 UTC 日界比對，當天早上的執行紀錄會被算成「昨天的」而重複補跑。
            var now = new DateTimeOffset(2026, 8, 21, 7, 0, 0, offset);

            var (fromUtc, toUtc) = FakeJob.DayRange(now);

            Assert.Equal(new DateTimeOffset(2026, 8, 21, 0, 0, 0, offset).UtcDateTime, fromUtc);
            Assert.Equal(TimeSpan.FromDays(1), toUtc - fromUtc);
            Assert.InRange(now.UtcDateTime, fromUtc, toUtc);
        }

        // ── 排程時區 ──────────────────────────────────────────────────────────
        //
        // 這組測的是「幾點跑」到底依誰的時鐘。原本用 DateTimeOffset.Now，等於隱含採用
        // 伺服器時區——本專案的開發與 CI 刻意跑 TZ=UTC，於是 RunAtHour=2（原意是離峰的
        // 凌晨兩點）實際落在當地上午十點，而且沒有任何徵兆。

        [Fact(DisplayName = "Jobs:TimeZone 有設定時，排程依該時區而非伺服器時區")]
        public void TimeZone_From_Configuration_Is_Used()
        {
            var job = Make(timeZoneId: "Asia/Taipei");
            Assert.Equal("Asia/Taipei", job.Zone.Id);
        }

        [Fact(DisplayName = "Jobs:TimeZone 未設定時退回伺服器時區")]
        public void TimeZone_Defaults_To_Server_Zone_When_Unset()
        {
            var job = Make();
            Assert.Equal(TimeZoneInfo.Local.Id, job.Zone.Id);
        }

        [Fact(DisplayName = "Jobs:TimeZone 打錯時退回伺服器時區，不讓排程整個停擺")]
        public void TimeZone_Falls_Back_When_Id_Is_Invalid()
        {
            var job = Make(timeZoneId: "Not/AReal_Zone");
            Assert.Equal(TimeZoneInfo.Local.Id, job.Zone.Id);
        }

        [Theory(DisplayName = "等待長度依排程時區計算，不受伺服器時區影響")]
        // 當天稍早 → 等到今天的 02:00
        [InlineData(2026, 9, 14, 0, 30, 1.5)]
        // 已過觸發時刻 → 等到明天的 02:00
        [InlineData(2026, 9, 14, 23, 30, 2.5)]
        [InlineData(2026, 9, 14, 2, 0, 24.0)]
        public void Delay_Is_Computed_In_Job_TimeZone(
            int y, int mo, int d, int h, int mi, double expectedHours)
        {
            var job = Make(timeZoneId: "Asia/Taipei");
            job.RunAtHourOverride = 2;

            // 台北時間的「現在」。若實作誤把目標時刻交給 DateTime → DateTimeOffset 的隱含
            // 轉換（那會套用伺服器時區），在 TZ=UTC 的機器上算出來會多整整 8 小時——
            // 這個斷言就是為了釘住那件事。
            var now = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(8));

            Assert.Equal(TimeSpan.FromHours(expectedHours), job.DelayFrom(now));
        }

        // ── 機器睡眠 ──────────────────────────────────────────────────────────
        //
        // Task.Delay 底層的計時器在 Windows 睡眠期間不前進——它等的是「清醒時間」而不是
        // 牆上時鐘，喚醒後既不補觸發也不提早，只是繼續等剩下的清醒時間。2026-09-26 筆電
        // IIS 實測：w3wp 連續存活 45 小時未回收，累計清醒僅 2 小時 21 分，於是一個 7 小時
        // 43 分的 Delay 一次都沒回來，當天兩支固定時刻排程都是零次。啟動補跑救不了這種,
        // 因為應用程式集區從頭到尾沒有重啟過。
        //
        // 下面這組測的就是「時鐘跳一大段之後會不會補上」。假時鐘是唯一驗得到的方法。

        [Fact(DisplayName = "睡眠跨日錯過觸發時刻，醒來後補跑（不必等到隔天）")]
        public async Task Missed_Slot_During_Sleep_Is_Caught_Up_On_Wake()
        {
            // 09/26 08:00 啟動，17:00 的工作當天還沒到；16:00 機器睡著，09/27 09:00 才醒。
            // 09/26 17:00 那一格整個被跳過，而醒來時 09:00 又還沒到當天的 17:00。
            var job = MakeScheduled(Taipei(2026, 9, 26, 8, 0), runAtHour: 17);

            await StartAndEnterLoopAsync(job);
            Assert.False(await WaitForAsync(() => job.RunCount >= 1, 200), "還沒到觸發時刻就跑了");

            job.FakeNow = Taipei(2026, 9, 27, 9, 0);   // 醒來
            var caughtUp = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(caughtUp,
                "醒來後沒有補跑——若判定只看『現在幾點 >= RunAtHour』，09:00 會被當成還沒到，" +
                "昨天 17:00 那一格就永遠遺失了");
            Assert.Equal(1, job.RunCount);
        }

        [Fact(DisplayName = "等待不會一口氣睡到觸發時刻（長 Delay 會被睡眠吃掉）")]
        public async Task Wait_Is_Bounded_So_Wall_Clock_Is_Re_Read()
        {
            // 00:30 啟動、02:00 觸發：改寫前這裡是一個 90 分鐘的 Task.Delay，時鐘跳到 02:00
            // 也不會讓它回來。時鐘一跳就執行，才代表等待長度有上限、且每一輪重讀牆上時鐘。
            var job = MakeScheduled(Taipei(2026, 9, 26, 0, 30), runAtHour: 2);

            await StartAndEnterLoopAsync(job);
            job.FakeNow = Taipei(2026, 9, 26, 2, 0);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran, "時鐘已到觸發時刻卻沒有執行——等待長度仍然沒有上限");
        }

        [Fact(DisplayName = "同一格觸發時刻只跑一次，不會每輪都跑")]
        public async Task Slot_Fires_Once_Not_Every_Poll()
        {
            var job = MakeScheduled(Taipei(2026, 9, 26, 16, 55), runAtHour: 17);

            await StartAndEnterLoopAsync(job);
            job.FakeNow = Taipei(2026, 9, 26, 17, 0);
            Assert.True(await WaitForAsync(() => job.RunCount >= 1));

            // 觸發時刻之後又過了好幾輪輪詢——輪詢化最容易踩的就是這裡變成每五分鐘一次。
            foreach (var minute in new[] { 5, 20, 59 })
            {
                job.FakeNow = Taipei(2026, 9, 26, 17, minute);
                await Task.Delay(120);
            }
            await job.StopAsync(CancellationToken.None);

            Assert.Equal(1, job.RunCount);
        }

        [Fact(DisplayName = "隔天同一時刻會再跑一次")]
        public async Task Next_Day_Slot_Fires_Again()
        {
            var job = MakeScheduled(Taipei(2026, 9, 26, 16, 55), runAtHour: 17);

            await StartAndEnterLoopAsync(job);
            job.FakeNow = Taipei(2026, 9, 26, 17, 0);
            Assert.True(await WaitForAsync(() => job.RunCount >= 1));

            job.FakeNow = Taipei(2026, 9, 27, 17, 0);
            var ranAgain = await WaitForAsync(() => job.RunCount >= 2);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ranAgain);
            Assert.Equal(2, job.RunCount);
        }

        [Fact(DisplayName = "啟動時已過當天觸發時刻，定時路徑不再補一次（補跑是啟動補跑的責任）")]
        public async Task Slot_Already_Passed_At_Startup_Is_Not_Re_Fired_By_Scheduler()
        {
            // 18:00 啟動、17:00 的工作。今天這一格歸啟動補跑管（此處刻意關掉），
            // 定時路徑不該把它當成「漏跑」再補一次——否則 Jobs:RunAtStartup=false 形同虛設。
            var job = MakeScheduled(Taipei(2026, 9, 26, 18, 0), runAtHour: 17);

            await StartAndEnterLoopAsync(job);
            job.FakeNow = Taipei(2026, 9, 26, 23, 30);
            Assert.False(await WaitForAsync(() => job.RunCount >= 1, 300), "當天那一格被重複補跑了");

            job.FakeNow = Taipei(2026, 9, 27, 17, 0);
            var ranNextDay = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ranNextDay, "隔天的觸發時刻反而不跑了");
            Assert.Equal(1, job.RunCount);
        }

        // ── 執行紀錄去重 ──────────────────────────────────────────────────────
        //
        // 啟動補跑讓排程在非 24 小時開機的機器上跑得到，代價是每次應用程式啟動都再跑
        // 一次。對冪等清理無所謂，但 ETA 全量校準很貴、生產準備提醒會推播給全廠角色
        // ——後者重複執行是實害。ScheduledJobRun 這張表就是為了讓補跑變免費。

        [Fact(DisplayName = "今天已成功執行過就不再跑（啟用執行紀錄時）")]
        public async Task Does_Not_Run_When_Already_Succeeded_Today()
        {
            var rec = new FakeRecorder();
            var job = MakeRecorded(rec, Taipei(2026, 9, 29, 10, 0));
            rec.MarkSucceeded("測試排程", new DateOnly(2026, 9, 29));

            await job.StartAsync(CancellationToken.None);
            await Task.Delay(200);
            await job.StopAsync(CancellationToken.None);

            Assert.Equal(0, job.RunCount);
        }

        [Fact(DisplayName = "執行成功會落地一列紀錄，日期取排程時區的當地日期")]
        public async Task Successful_Run_Is_Recorded_With_Local_Date()
        {
            var rec = new FakeRecorder();
            // 台北 2026-09-29 早上 07:00 —— 此刻的 UTC 日期還是 09/28。
            // 若落地時用 UTC 日期，隔天早上會判定「昨天跑的」而重複執行。
            var job = MakeRecorded(rec, Taipei(2026, 9, 29, 7, 0));

            await job.StartAsync(CancellationToken.None);
            Assert.True(await WaitForAsync(() => rec.Snapshot().Length >= 1));
            await job.StopAsync(CancellationToken.None);

            var run = Assert.Single(rec.Snapshot());
            Assert.Equal("測試排程", run.JobName);
            Assert.Equal(new DateOnly(2026, 9, 29), run.JobDate);
            Assert.Equal("啟動補跑", run.Trigger);
            Assert.True(run.Succeeded);
            Assert.Null(run.Error);
            Assert.True(run.CompletedAtUtc >= run.StartedAtUtc);
        }

        [Fact(DisplayName = "失敗也會落地，但不擋當天的重試（去重只看成功的紀錄）")]
        public async Task Failed_Run_Is_Recorded_But_Does_Not_Block_Retry()
        {
            var rec = new FakeRecorder();
            var failing = MakeRecorded(rec, Taipei(2026, 9, 29, 10, 0));
            failing.ThrowOnRun = new InvalidOperationException("DB 暫時斷線");

            await failing.StartAsync(CancellationToken.None);
            Assert.True(await WaitForAsync(() => rec.Snapshot().Length >= 1));
            await failing.StopAsync(CancellationToken.None);

            var failed = Assert.Single(rec.Snapshot());
            Assert.False(failed.Succeeded);
            Assert.Contains("DB 暫時斷線", failed.Error);

            // 同一天、同一支排程再啟動一次（模擬修好之後重啟應用程式）——必須跑得到。
            var retry = MakeRecorded(rec, Taipei(2026, 9, 29, 11, 0));
            await retry.StartAsync(CancellationToken.None);
            var ranAgain = await WaitForAsync(() => retry.RunCount >= 1);
            await retry.StopAsync(CancellationToken.None);

            Assert.True(ranAgain, "當天失敗過就再也補不回來了——去重誤把失敗的紀錄也算數");
        }

        [Fact(DisplayName = "查不到執行紀錄時照跑，不讓一次連線抖動吃掉一整天")]
        public async Task Recorder_Query_Failure_Fails_Open()
        {
            var rec = new FakeRecorder { ThrowOnQuery = new TimeoutException("連線逾時") };
            var job = MakeRecorded(rec, Taipei(2026, 9, 29, 10, 0));

            await job.StartAsync(CancellationToken.None);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran);
        }

        [Fact(DisplayName = "紀錄寫不進去不會讓工作本身變成失敗")]
        public async Task Recorder_Write_Failure_Does_Not_Fail_The_Job()
        {
            var rec = new FakeRecorder { ThrowOnRecord = new TimeoutException("連線逾時") };
            var job = MakeRecorded(rec, Taipei(2026, 9, 29, 10, 0));

            await job.StartAsync(CancellationToken.None);
            var ran = await WaitForAsync(() => job.RunCount >= 1);
            // 寫紀錄若把例外往外丟，會被當成工作失敗而記 Error，且 StopAsync 可能 faulted。
            await job.StopAsync(CancellationToken.None);

            Assert.True(ran);
            Assert.Equal(1, job.RunCount);
        }

        [Fact(DisplayName = "補跑失敗不會卡住，隔天照跑")]
        public async Task Failed_Slot_Does_Not_Block_The_Next_Day()
        {
            var job = MakeScheduled(Taipei(2026, 9, 26, 16, 55), runAtHour: 17);
            job.ThrowOnRun = new InvalidOperationException("DB 暫時斷線");

            await StartAndEnterLoopAsync(job);
            job.FakeNow = Taipei(2026, 9, 26, 17, 0);
            Assert.True(await WaitForAsync(() => job.RunCount >= 1));

            job.FakeNow = Taipei(2026, 9, 27, 17, 0);
            var ranAgain = await WaitForAsync(() => job.RunCount >= 2);
            await job.StopAsync(CancellationToken.None);

            Assert.True(ranAgain, "一次失敗之後排程就停擺了");
        }
    }
}
