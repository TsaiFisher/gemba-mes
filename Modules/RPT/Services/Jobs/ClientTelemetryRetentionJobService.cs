using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.RPT.Services.Jobs
{
    /// <summary>
    /// 現場遙測保留期清理：每日刪除超過保留期的遙測事件。
    ///
    /// <para>遙測是「現在出了什麼事」的訊號，不是帳——量大、價值隨時間遞減，預設只留 30 天
    /// （設定 <c>Telemetry:RetentionDays</c> 覆寫；&lt;= 0 退回預設）。<c>ExecuteDeleteAsync</c> 直接下 SQL，
    /// 不經稽核攔截器（寫入本來就不經，見 <see cref="ClientTelemetryService"/>）。</para>
    /// </summary>
    public sealed class ClientTelemetryRetentionJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ClientTelemetryRetentionJobService> _logger;

        /// <summary>每日觸發時刻（伺服器當地小時）；錯開稽核清理（03:00）。</summary>
        internal const int RunAtHourValue = 4;

        internal const int DefaultRetentionDays = 30;

        public ClientTelemetryRetentionJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<ClientTelemetryRetentionJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public int ResolveRetentionDays()
        {
            var configured = _configuration.GetValue<int?>("Telemetry:RetentionDays");
            return configured is > 0 ? configured.Value : DefaultRetentionDays;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "現場遙測保留期清理";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>冪等刪除，補跑與定時各跑一次無害。</summary>
        protected override async Task ExecuteJobAsync(CancellationToken ct) => await RunOnceAsync(ct);

        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var retentionDays = ResolveRetentionDays();
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            var deleted = await db.ClientTelemetryEvents
                .IgnoreQueryFilters()
                .Where(e => e.ReceivedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation("現場遙測保留期清理：刪除 {Deleted} 筆（保留 {RetentionDays} 天，早於 {Cutoff:u}）",
                    deleted, retentionDays, cutoff);
            return deleted;
        }
    }
}
