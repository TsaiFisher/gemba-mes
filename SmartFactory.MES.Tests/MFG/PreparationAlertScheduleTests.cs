using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Models.Jobs;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services.Jobs;
using Xunit;

namespace SmartFactory.MES.Tests.MFG
{
    /// <summary>
    /// 生產準備提醒的「什麼時候該送」。
    ///
    /// <para>這支排程是三支 MFG 背景服務裡最後才改掛 <c>DailyJobServiceBase</c> 的，
    /// 因為它每執行一次就推播一次給全廠負責角色，而推播服務不落地已送紀錄——直接接上
    /// 啟動補跑會讓每次應用程式重啟都重推。這組測試釘的就是防止那件事的兩道閘：
    /// 共用執行紀錄的當日去重，以及「不在 17:00 之前提早送」。</para>
    /// </summary>
    public class PreparationAlertScheduleTests
    {
        private sealed class Recorder : IJobRunRecorder
        {
            public HashSet<(string, DateOnly)> Succeeded { get; } = new();
            public List<ScheduledJobRun> Records { get; } = new();

            public Task<bool> HasSucceededOnAsync(string jobName, DateOnly jobDate, CancellationToken ct = default)
                => Task.FromResult(Succeeded.Contains((jobName, jobDate)));

            public Task RecordAsync(ScheduledJobRun run, CancellationToken ct = default)
            {
                Records.Add(run);
                if (run.Succeeded) Succeeded.Add((run.JobName, run.JobDate));
                return Task.CompletedTask;
            }
        }

        /// <summary>只為了把 protected 的 ShouldRunAsync 拿出來斷言，不改任何行為。</summary>
        private sealed class Testable : PreparationAlertBackgroundService
        {
            public Testable(IServiceProvider sp, IConfiguration cfg)
                : base(sp, NullLogger<PreparationAlertBackgroundService>.Instance, cfg) { }

            public Task<bool> ShouldRun(DateTimeOffset now) => ShouldRunAsync(now, CancellationToken.None);
        }

        private const string JobName = "生產準備提醒";

        private static (Testable Job, Recorder Rec) Make()
        {
            var rec = new Recorder();
            var sp = new ServiceCollection()
                .AddSingleton<IJobRunRecorder>(rec)
                .BuildServiceProvider();
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DailyJobServiceBase.TimeZoneConfigKey] = "Asia/Taipei",
                })
                .Build();
            return (new Testable(sp, cfg), rec);
        }

        private static DateTimeOffset Taipei(int y, int mo, int d, int h)
            => new(y, mo, d, h, 0, 0, TimeSpan.FromHours(8));

        [Theory(DisplayName = "17:00 之前不送，即使當天還沒送過")]
        [InlineData(0)]
        [InlineData(9)]
        [InlineData(16)]
        public async Task Does_Not_Fire_Before_Run_Hour(int hour)
        {
            var (job, _) = Make();

            // 啟動補跑預設不管幾點都會跑一次。但這則提醒的內容是「明天的排程」，
            // 早上送出去用的是半天前的快照，而且去重之後會把當天唯一的機會用掉——
            // 真正該送的 17:00 那次反而不跑了。
            Assert.False(await job.ShouldRun(Taipei(2026, 9, 29, hour)));
        }

        [Theory(DisplayName = "17:00 之後且當天還沒送過就送")]
        [InlineData(17)]
        [InlineData(23)]
        public async Task Fires_At_Or_After_Run_Hour(int hour)
        {
            var (job, _) = Make();

            Assert.True(await job.ShouldRun(Taipei(2026, 9, 29, hour)));
        }

        [Fact(DisplayName = "當天已送過就不再送（重啟應用程式也不會重推）")]
        public async Task Does_Not_Fire_Twice_On_The_Same_Day()
        {
            var (job, rec) = Make();
            rec.Succeeded.Add((JobName, new DateOnly(2026, 9, 29)));

            Assert.False(await job.ShouldRun(Taipei(2026, 9, 29, 18)));
            // 隔天照送。
            Assert.True(await job.ShouldRun(Taipei(2026, 9, 30, 18)));
        }

        [Fact(DisplayName = "去重的日期是排程時區的當地日期，不是 UTC 日期")]
        public async Task Dedupe_Key_Is_Local_Date()
        {
            var (job, rec) = Make();
            // 台北 09/29 18:00 的 UTC 日期是 09/29 10:00，同一天；但台北 09/30 01:00
            // 的 UTC 日期是 09/29——若用 UTC 日期去重，跨午夜後會誤判成「今天已送過」。
            rec.Succeeded.Add((JobName, new DateOnly(2026, 9, 29)));

            Assert.False(await job.ShouldRun(Taipei(2026, 9, 29, 18)));
            // 台北 09/30 的 17:00（UTC 仍是 09/30 09:00，但重點是當地日期已換日）。
            Assert.True(await job.ShouldRun(Taipei(2026, 9, 30, 17)));
        }

        // ── 「隔日」的查詢區間（ScheduledStart 是 timestamptz）────────────────────────

        [Fact(DisplayName = "隔日區間是廠別當地的 00:00～24:00 換成 UTC，不是 UTC 午夜")]
        public void TomorrowWindow_Taipei_IsLocalMidnightToMidnight()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            // 台北 10/20 17:00 送提醒
            var (day, start, end) = PreparationAlertBackgroundService.TomorrowWindowUtc(
                new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), tz);

            Assert.Equal(new DateOnly(2026, 10, 21), day);
            // 原本是 10/21 00:00Z～23:59Z＝台北 08:00～隔天 07:59：凌晨 0～8 點開工的派工全漏
            Assert.Equal(new DateTime(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc), start);
            Assert.Equal(new DateTime(2026, 10, 21, 16, 0, 0, DateTimeKind.Utc), end);
            Assert.Equal(DateTimeKind.Utc, start.Kind);   // Npgsql 不收 Unspecified
        }

        [Fact(DisplayName = "隔日以廠別當地日期判斷：台北已過午夜、UTC 仍是前一天")]
        public void TomorrowWindow_UsesPlantLocalToday()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
            // UTC 10/20 16:30＝台北 10/21 00:30 → 隔日是 10/22
            var (day, _, _) = PreparationAlertBackgroundService.TomorrowWindowUtc(
                new DateTime(2026, 10, 20, 16, 30, 0, DateTimeKind.Utc), tz);
            Assert.Equal(new DateOnly(2026, 10, 22), day);
        }

        [Fact(DisplayName = "紐約廠日光節約開始那天只有 23 小時")]
        public void TomorrowWindow_NewYork_DstStartDay()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            // 紐約 2027-03-13（六）17:00 EST；隔日 3/14 02:00 起改 EDT
            var (day, start, end) = PreparationAlertBackgroundService.TomorrowWindowUtc(
                new DateTime(2027, 3, 13, 22, 0, 0, DateTimeKind.Utc), tz);

            Assert.Equal(new DateOnly(2027, 3, 14), day);
            Assert.Equal(new DateTime(2027, 3, 14, 5, 0, 0, DateTimeKind.Utc), start);
            Assert.Equal(new DateTime(2027, 3, 15, 4, 0, 0, DateTimeKind.Utc), end);
        }
    }
}
