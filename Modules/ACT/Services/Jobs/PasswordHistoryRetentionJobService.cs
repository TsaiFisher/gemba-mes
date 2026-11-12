using SmartFactory.MES.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Services.Jobs
{
    /// <summary>
    /// L4：PasswordHistory 保留清理排程。
    ///
    /// <para>密碼歷史（Core <c>Data/PasswordHistoryInterceptor.cs</c> 於每次改密 add-only 寫入）
    /// 本身無上限；列極小、功能正確性不依賴修剪（驗證器僅取最近 N 筆比對），故此排程純為
    /// 有界成長，比照 <see cref="AuditLogRetentionJobService"/>。</para>
    ///
    /// <para><b>刻意不採時間式清理</b>：若以「早於 X 天」刪除，會誤刪「低頻改密使用者」仍落在
    /// 「最近 N 次」視窗內的舊雜湊，削弱 <c>PasswordHistoryValidator</c> 的重用檢查。改採
    /// 「<b>每位使用者保留最近 K 筆、其餘刪除</b>」——K = max(<c>Security:PasswordHistoryCount</c>,
    /// <c>Security:PasswordHistoryRetentionKeep</c>[預設 24])，保證永不少於驗證器實際比對的 N，
    /// 零削弱風險，同時把每人上限壓到 K 列。</para>
    ///
    /// <para>排序（CreatedAt desc, Id desc）與驗證器 <c>OrderByDescending(CreatedAt).ThenByDescending(Id)</c>
    /// 一致，確保門檻切點與「最近 N 筆」定義相同。刪除以 <c>ExecuteDeleteAsync</c> 直接下 SQL
    /// （不經 SaveChanges → 不觸發 PasswordHistoryInterceptor / AuditInterceptor）。
    /// PasswordHistory 非 <c>IPlantEntity</c>，無全域過濾，毋須 <c>IgnoreQueryFilters</c>。</para>
    /// </summary>
    public sealed class PasswordHistoryRetentionJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PasswordHistoryRetentionJobService> _logger;

        /// <summary>每日觸發時刻（伺服器當地時區的小時數）；錯開每日稽核(02:00)與 AuditLog 清理(03:00)。</summary>
        internal const int RunAtHourValue = 4;

        /// <summary>預設每人保留筆數（給未來 N 調升的餘裕；每人上限 24 列可忽略）。</summary>
        internal const int DefaultKeep = 24;

        public PasswordHistoryRetentionJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<PasswordHistoryRetentionJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// 解析每人保留筆數：max(保留下限[預設 24], 驗證器實際比對的 N)。
        /// 保證清理後仍足以支撐「禁用最近 N 次」的重用檢查。
        /// </summary>
        public int ResolveKeepCount()
        {
            var configuredKeep = _configuration.GetValue<int?>("Security:PasswordHistoryRetentionKeep");
            var keep = configuredKeep is > 0 ? configuredKeep.Value : DefaultKeep;

            var historyCount = _configuration.GetValue<int?>("Security:PasswordHistoryCount") ?? 5;
            if (historyCount > keep) keep = historyCount;

            return keep;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "PasswordHistory 保留清理";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        /// <summary>清理是冪等刪除，補跑與定時各跑一次無害，故不覆寫 ShouldRunAsync。</summary>
        protected override async Task ExecuteJobAsync(CancellationToken ct) => await RunOnceAsync(ct);

        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            // BackgroundService 是 Singleton，DbContext 是 Scoped → 每次跑開一個 scope。
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var keepCount = ResolveKeepCount();
            if (keepCount <= 0) return 0; // 理論上不會（ResolveKeepCount 恆 > 0）

            // 只處理「筆數超過保留上限」的使用者，避免逐一掃描全表。
            var usersOver = await db.Set<PasswordHistory>()
                .GroupBy(h => h.UserId)
                .Where(g => g.Count() > keepCount)
                .Select(g => g.Key)
                .ToListAsync(ct);

            var totalDeleted = 0;
            foreach (var userId in usersOver)
            {
                // 該使用者「要保留的最舊一筆」（第 keepCount 新）作為門檻；早於它者刪除。
                var threshold = await db.Set<PasswordHistory>()
                    .Where(h => h.UserId == userId)
                    .OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
                    .Skip(keepCount - 1).Take(1)
                    .Select(h => new { h.CreatedAt, h.Id })
                    .FirstOrDefaultAsync(ct);
                if (threshold == null) continue;

                totalDeleted += await db.Set<PasswordHistory>()
                    .Where(h => h.UserId == userId &&
                        (h.CreatedAt < threshold.CreatedAt ||
                         (h.CreatedAt == threshold.CreatedAt && h.Id < threshold.Id)))
                    .ExecuteDeleteAsync(ct);
            }

            if (totalDeleted > 0)
                _logger.LogInformation(
                    "PasswordHistory 保留清理：刪除 {Deleted} 筆（每人保留最近 {KeepCount} 筆，跨 {Users} 位使用者）",
                    totalDeleted, keepCount, usersOver.Count);

            return totalDeleted;
        }
    }
}
