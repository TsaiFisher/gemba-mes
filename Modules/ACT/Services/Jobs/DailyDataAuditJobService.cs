using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.ACT.Services.Jobs
{
    /// <summary>
    /// 每日資料稽核排程（business_data_flow_reconciliation.md §9 P-8，/new-job 規格）。
    /// 每天 02:00（伺服器當地時區）執行全部 §3 檢核並持久化結果，
    /// 供 RPT 稽核診斷畫面呈現，異常主動浮現而非等到月結擋關才發現。
    ///
    /// <para>執行時機由 <see cref="DailyJobServiceBase"/> 掌管：除了每日 02:00，
    /// 應用程式啟動後也會補跑一次。02:00 對「上班才開機」的部署環境等於永遠不會到，
    /// 而這支排程的價值全在「異常主動浮現」——跑不到就完全沒有價值。</para>
    /// </summary>
    public sealed class DailyDataAuditJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DailyDataAuditJobService> _logger;

        /// <summary>每日觸發時刻（伺服器當地時區的小時數）。</summary>
        internal const int RunAtHourValue = 2;

        public DailyDataAuditJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<DailyDataAuditJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "每日資料稽核";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>
        /// 今天（當地日期）若已有排程觸發的稽核紀錄就不再跑。
        ///
        /// <para>沒有這道判定，01:00 開機補跑後，一小時後的 02:00 定時會再寫一筆，
        /// 同一天出現兩筆 Scheduled 紀錄，讓儀表板的趨勢圖出現假的高低起伏。
        /// 只認 Scheduled——手動 RunNow 是使用者刻意觸發的，不該讓它抵銷當天的自動稽核。</para>
        /// </summary>
        protected override async Task<bool> ShouldRunAsync(DateTimeOffset now, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var (fromUtc, toUtc) = LocalDayRangeUtc(now);

            return !await db.DataAuditRuns
                .AsNoTracking()
                .AnyAsync(r => r.TriggerType == "Scheduled" &&
                               r.RunAt >= fromUtc && r.RunAt < toUtc, ct);
        }

        protected override Task ExecuteJobAsync(CancellationToken ct) => RunOnceAsync(ct);

        internal async Task RunOnceAsync(CancellationToken ct)
        {
            // BackgroundService 是 Singleton，DbContext/稽核服務是 Scoped → 每次跑開一個 scope。
            // 稽核服務所有查詢已 IgnoreQueryFilters + 明確 plantId，無使用者上下文亦可全廠稽核。
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IDataAuditRunService>();

            var run = await runner.RunAndPersistAsync(
                plantId: null, triggerType: "Scheduled", createdBy: "SYSTEM", ct);

            if (run.FailureCount > 0)
                _logger.LogWarning("每日資料稽核完成：{FailureCount} 筆異常（RunId={RunId}，{DurationMs} ms）",
                    run.FailureCount, run.RunId, run.DurationMs);
            else
                _logger.LogInformation("每日資料稽核完成：無異常（RunId={RunId}，{DurationMs} ms）",
                    run.RunId, run.DurationMs);
        }
    }
}
