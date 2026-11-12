using SmartFactory.MES.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Services.Jobs
{
    /// <summary>
    /// 簽核歷程（ApprovalRequests + ApprovalLogs）保留期清理排程。
    ///
    /// <para>這兩張表原本只進不出：每送審一次寫一列 Request、每個簽核動作寫一列 Log，
    /// 上線後隨單量線性成長且永不回收。比照 <see cref="AuditLogRetentionJobService"/>
    /// 採 730 天（2 年），保留天數可經 <c>Security:ApprovalHistoryRetentionDays</c> 覆寫。</para>
    ///
    /// <para><b>為什麼刪的是 Request 而不是 Log</b>：DB 層 <c>FK_ApprovalLogs_ApprovalRequests_RequestId</c>
    /// 是 ON DELETE CASCADE。若直接對 ApprovalLogs 下時間條件，會出現「申請單還在、簽核歷程被砍掉一半」
    /// 的破碎狀態（歷程頁顯示不出誰簽的）。改成刪父層由 CASCADE 帶走子層，任一申請單的歷程
    /// 只會「整份在」或「整份不在」，不存在中間態。</para>
    ///
    /// <para><b>Pending 一律不刪</b>：不論多舊，只要還是 <see cref="ApprovalStatus.Pending"/>
    /// 就代表單子還卡在流程上（常見於離職未轉派、代理人未設定），刪掉會讓該單永遠無法結案。
    /// 只清終局狀態（Approved / Rejected / Withdrawn / ReturnedForRevision）。</para>
    ///
    /// <para>ApprovalRequest 同時是 <c>IPlantEntity</c> 與 <c>IDataAuthEntity</c>，而背景服務沒有
    /// HttpContext（無使用者上下文 → 廠別與資料權限過濾會把全部資料濾成 0 筆），因此必須
    /// <c>IgnoreQueryFilters()</c> 跨全廠清理。<c>ExecuteDeleteAsync</c> 直接下 SQL，不經
    /// SaveChanges → 不觸發 AuditInterceptor，清理本身不再產生稽核。</para>
    /// </summary>
    public sealed class ApprovalHistoryRetentionJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ApprovalHistoryRetentionJobService> _logger;

        /// <summary>每日觸發時刻；錯開每日稽核(02:00)、AuditLog(03:00)、PasswordHistory(04:00)。</summary>
        internal const int RunAtHourValue = 5;

        /// <summary>預設保留天數（2 年，與 AuditLog 一致）。</summary>
        internal const int DefaultRetentionDays = 730;

        public ApprovalHistoryRetentionJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<ApprovalHistoryRetentionJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>解析有效保留天數（設定 &lt;= 0 或未設 → 預設 730）。</summary>
        public int ResolveRetentionDays()
        {
            var configured = _configuration.GetValue<int?>("Security:ApprovalHistoryRetentionDays");
            return configured is > 0 ? configured.Value : DefaultRetentionDays;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "簽核歷程保留期清理";
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

            // ApprovalLogs 由 DB 層 CASCADE 連帶刪除，不必也不應在此另外下刪除。
            var deleted = await db.ApprovalRequests
                .IgnoreQueryFilters()
                .Where(r => r.CreatedAt < cutoff && r.Status != ApprovalStatus.Pending)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation(
                    "簽核歷程保留期清理：刪除 {Deleted} 張申請單及其歷程（保留 {RetentionDays} 天，早於 {Cutoff:u}；Pending 不清）",
                    deleted, retentionDays, cutoff);

            return deleted;
        }
    }
}
