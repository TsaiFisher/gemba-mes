using SmartFactory.MES.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.ACT.Services.Jobs
{
    /// <summary>
    /// 安全修正 M5：AuditLog 保留期清理排程。
    ///
    /// <para>現況 AuditLog 只進不出（無到期清除），長年累積既是儲存負擔、也讓含 PII 的
    /// 稽核明細無限期留存。本排程每日一次刪除超過保留期的紀錄，比照
    /// <see cref="DailyDataAuditJobService"/> 的 §P-8 90 天 prune 模式，但 AuditLog 建議
    /// 較長（預設 730 天／2 年，符合稽核留痕需求）。</para>
    ///
    /// <para>保留天數可經設定 <c>Security:AuditLogRetentionDays</c> 覆寫；設為 &lt;= 0 時
    /// 退回預設 730。刪除以 <c>IgnoreQueryFilters</c> 跨全廠、<c>ExecuteDeleteAsync</c> 直接
    /// 下 SQL（不經 AuditInterceptor，清理本身不再產生稽核）。</para>
    ///
    /// <para>備註：稽核明細的 PII 遮罩（Email/Phone 等）位於 Core <c>Data/AuditInterceptor.cs</c>
    /// 的遮罩清單，屬寫入端控制，須於 Core 擴充後同步，不在本 ProjectZ 排程範圍。</para>
    /// </summary>
    public sealed class AuditLogRetentionJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AuditLogRetentionJobService> _logger;

        /// <summary>每日觸發時刻（伺服器當地時區的小時數）；錯開每日稽核（02:00）。</summary>
        internal const int RunAtHourValue = 3;

        /// <summary>預設保留天數（2 年）。</summary>
        internal const int DefaultRetentionDays = 730;

        public AuditLogRetentionJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<AuditLogRetentionJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>解析有效保留天數（設定 &lt;= 0 或未設 → 預設 730）。</summary>
        public int ResolveRetentionDays()
        {
            var configured = _configuration.GetValue<int?>("Security:AuditLogRetentionDays");
            return configured is > 0 ? configured.Value : DefaultRetentionDays;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "AuditLog 保留期清理";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>清理是冪等刪除，補跑與定時各跑一次無害，故不覆寫 ShouldRunAsync。</summary>
        protected override async Task ExecuteJobAsync(CancellationToken ct) => await RunOnceAsync(ct);

        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            // BackgroundService 是 Singleton，DbContext 是 Scoped → 每次跑開一個 scope。
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var retentionDays = ResolveRetentionDays();
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            // IgnoreQueryFilters：無使用者上下文，跨全廠清理（AuditLog 為 IPlantEntity）。
            // ExecuteDeleteAsync：直接 SQL DELETE，不載入實體、不觸發 AuditInterceptor。
            var deleted = await db.AuditLogs
                .IgnoreQueryFilters()
                .Where(a => a.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation(
                    "AuditLog 保留期清理：刪除 {Deleted} 筆（保留 {RetentionDays} 天，早於 {Cutoff:u}）",
                    deleted, retentionDays, cutoff);

            return deleted;
        }
    }
}
