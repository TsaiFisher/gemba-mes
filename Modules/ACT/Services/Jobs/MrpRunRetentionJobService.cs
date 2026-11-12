using SmartFactory.MES.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.ACT.Services.Jobs
{
    /// <summary>
    /// MRP 展開單（MrpRuns + MrpRunLines）保留期清理排程。
    ///
    /// <para>MrpRunLines 是全庫成長最快的表之一：每次展開依 BOM 階層產生數百至數千列，
    /// 而「重跑」在日常作業中很頻繁（訂單或 PO 一異動就標 IsDirty 提示重跑），
    /// 於是絕大多數展開單是「試跑完就沒有下文」的中間產物，卻永久留存。
    /// 保留天數可經 <c>Maintenance:MrpRunRetentionDays</c> 覆寫，預設 180 天。</para>
    ///
    /// <para><b>只刪「無下游產出」的展開單</b>——這是本排程最關鍵的守門。DB 層只有
    /// <c>FK_MrpRunLines_MrpRuns_MrpRunId</c> 一條真外鍵（CASCADE，明細自動帶走）；
    /// 但下列四處都以「純字串欄位」記著 MrpRunId，**沒有任何外鍵保護**：</para>
    /// <list type="bullet">
    ///   <item><c>PurchaseRequest.SourceMrpRunId</c> / <c>PurchaseRequestLine.SourceMrpRunId</c>（MRP 轉請購的去重鍵）</item>
    ///   <item><c>ProductionBatch.MrpRunId</c></item>
    ///   <item><c>OutsourceDemand.MrpRunId</c></item>
    ///   <item><c>MrpPurchaseVarianceRecord.MrpRunId</c>（採購對帳差異項）</item>
    /// </list>
    /// <para>因為沒有外鍵，資料庫不會擋、刪除當下也不會報錯，但事後這些單據的來源追溯欄位
    /// 就變成指向不存在展開單的死指標（點不開、對帳查無來源）。所以刪除前逐一反查，
    /// 只要任一處仍引用就整張保留。代價是「有產出的展開單會長期留著」——這是刻意的取捨：
    /// 佔量的本來就是無產出的重複試跑，而可追溯性不該為了省空間犧牲。</para>
    ///
    /// <para>MrpRun 同時是 <c>IPlantEntity</c> 與 <c>IDataAuthEntity</c>，背景服務無 HttpContext
    /// （廠別與資料權限過濾會濾成 0 筆），故全程 <c>IgnoreQueryFilters()</c>。反查下游時同樣要
    /// 忽略過濾器——否則別廠的引用會被濾掉，讓一張「其實有產出」的展開單被誤判成可刪。</para>
    /// </summary>
    public sealed class MrpRunRetentionJobService : DailyJobServiceBase
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MrpRunRetentionJobService> _logger;

        /// <summary>每日觸發時刻；錯開每日稽核(02:00)、AuditLog(03:00)、PasswordHistory(04:00)、簽核歷程(05:00)。</summary>
        internal const int RunAtHourValue = 6;

        /// <summary>預設保留天數（半年）。</summary>
        internal const int DefaultRetentionDays = 180;

        /// <summary>單次處理的展開單上限，避免累積多年後首次執行一次撈進過大的候選集。</summary>
        internal const int MaxBatchSize = 500;

        public MrpRunRetentionJobService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<MrpRunRetentionJobService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>解析有效保留天數（設定 &lt;= 0 或未設 → 預設 180）。</summary>
        public int ResolveRetentionDays()
        {
            var configured = _configuration.GetValue<int?>("Maintenance:MrpRunRetentionDays");
            return configured is > 0 ? configured.Value : DefaultRetentionDays;
        }

        protected override int RunAtHour => RunAtHourValue;
        protected override string JobName => "MRP 展開單保留期清理";
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

            var candidates = await db.MrpRuns
                .IgnoreQueryFilters()
                .Where(r => r.CreatedAt < cutoff)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.MrpRunId)
                .Take(MaxBatchSize)
                .ToListAsync(ct);

            if (candidates.Count == 0) return 0;

            var referenced = await CollectReferencedRunIdsAsync(db, candidates, ct);

            var deletable = candidates.Where(id => !referenced.Contains(id)).ToList();
            if (deletable.Count == 0)
            {
                _logger.LogInformation(
                    "MRP 展開單保留期清理：{Candidates} 張逾期展開單全部仍有下游引用，未刪除",
                    candidates.Count);
                return 0;
            }

            // MrpRunLines 由 DB 層 CASCADE 連帶刪除。
            var deleted = await db.MrpRuns
                .IgnoreQueryFilters()
                .Where(r => deletable.Contains(r.MrpRunId))
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation(
                "MRP 展開單保留期清理：刪除 {Deleted} 張展開單及其明細（保留 {RetentionDays} 天，早於 {Cutoff:u}；"
                + "另有 {Kept} 張逾期但仍被下游引用而保留）",
                deleted, retentionDays, cutoff, candidates.Count - deletable.Count);

            return deleted;
        }

        /// <summary>
        /// 反查候選展開單中「仍被下游引用」者。任一來源命中即整張保留。
        /// 每張表都要 <c>IgnoreQueryFilters</c>：漏掉會讓別廠的引用被濾掉而誤判為可刪。
        /// </summary>
        private static async Task<HashSet<string>> CollectReferencedRunIdsAsync(
            ApplicationDbContext db, List<string> candidates, CancellationToken ct)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);

            referenced.UnionWith(await db.PurchaseRequests.IgnoreQueryFilters()
                .Where(p => p.SourceMrpRunId != null && candidates.Contains(p.SourceMrpRunId))
                .Select(p => p.SourceMrpRunId!).Distinct().ToListAsync(ct));

            referenced.UnionWith(await db.PurchaseRequestLines.IgnoreQueryFilters()
                .Where(l => l.SourceMrpRunId != null && candidates.Contains(l.SourceMrpRunId))
                .Select(l => l.SourceMrpRunId!).Distinct().ToListAsync(ct));

            referenced.UnionWith(await db.ProductionBatches.IgnoreQueryFilters()
                .Where(b => candidates.Contains(b.MrpRunId))
                .Select(b => b.MrpRunId).Distinct().ToListAsync(ct));

            referenced.UnionWith(await db.OutsourceDemands.IgnoreQueryFilters()
                .Where(d => candidates.Contains(d.MrpRunId))
                .Select(d => d.MrpRunId).Distinct().ToListAsync(ct));

            referenced.UnionWith(await db.MrpPurchaseVarianceRecords.IgnoreQueryFilters()
                .Where(v => candidates.Contains(v.MrpRunId))
                .Select(v => v.MrpRunId).Distinct().ToListAsync(ct));

            return referenced;
        }
    }
}
