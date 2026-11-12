using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Services
{
    public interface IDocNumberService
    {
        /// <summary>
        /// 產生下一個單號（原子遞增，並發安全）。
        /// </summary>
        /// <param name="docType">單據類型代號，e.g. "GoodsReceipt"</param>
        /// <param name="plantId">廠別（流水碼各廠獨立）</param>
        /// <param name="businessDate">業務日期（null = UTC 今日）；傳入廠別本地日期以確保跨時區正確</param>
        Task<string> NextAsync(string docType, string plantId,
            DateOnly? businessDate = null, CancellationToken ct = default);

        /// <summary>
        /// 產生預覽單號（不佔用流水碼）。
        /// </summary>
        string Preview(DocNumberConfig config, string plantId, DateOnly? date = null, int sampleSeq = 1);

        /// <summary>
        /// 重置週期變更後的序號校準。變更週期會換掉序號的期別鍵（如 "2026"→"202607"），
        /// 新期別從 1 重新起跳，但單號印出的日期段（如 YYMM "2607"）未必跟著變，
        /// 導致重發已用過的號碼、寫入時撞單據表 PK（23505）。
        /// 將該設定 scope（plantScope；""=預設規則）「當前期別」的序號墊高到該 scope
        /// 歷史最大序號，讓號碼接續不重發。
        /// 註：<see cref="NextAsync"/> 取號時另有同日期段兄弟期別的自我校準（含回填舊日期），
        /// 本方法為設定變更當下的先行校準，兩者互為保險。
        /// </summary>
        Task RealignSequencesAsync(string docType, string plantScope, DocSeqReset newCycle, CancellationToken ct = default);

        /// <summary>
        /// 廠別解析，供畫面預覽使用：專屬規則優先、回退預設（""）；皆無則回傳 null（不拋例外）。
        /// 與 <see cref="NextAsync"/> 內部共用同一套「PlantId 清單是否包含」比對邏輯。
        /// </summary>
        Task<DocNumberConfig?> ResolveForPreviewAsync(string docType, string plantId, CancellationToken ct = default);
    }

    /// <summary>
    /// 指定單據類型（廠別）查無編號規則——既無專屬規則、亦無預設規則。
    /// 由全站 <see cref="Filters.DbUniqueViolationExceptionFilter"/> 轉譯為友善訊息，
    /// 避免以 500 呈現；系統絕不自動代編臨時號（代編會產生失控、無法追溯的單號）。
    /// </summary>
    public class DocNumberConfigNotFoundException : InvalidOperationException
    {
        public string DocType { get; }
        public string PlantId { get; }

        public DocNumberConfigNotFoundException(string docType, string plantId)
            : base(PzText.Get("Bas_DocNumberConfigNotFoundFmt", docType, plantId))
        {
            DocType = docType;
            PlantId = plantId;
        }
    }

    public class DocNumberService : IDocNumberService
    {
        private readonly Data.ApplicationDbContext _context;

        public DocNumberService(Data.ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<string> NextAsync(string docType, string plantId,
            DateOnly? businessDate = null, CancellationToken ct = default)
        {
            var config = await ResolveConfigAsync(docType, plantId, ct);

            var date = businessDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var period = BuildPeriod(config.ResetCycle, date);

            // 使用 PostgreSQL UPSERT + RETURNING 確保原子遞增（並發安全）
            // 透過 EF 的 OpenConnectionAsync/CloseConnectionAsync（參照計數）借用連線，
            // 而非直接對底層連線呼叫 Open/Close：外層若已在顯式 transaction 中
            // （BeginTransactionAsync 已開啟連線），或同一 scope 內連續呼叫本方法，
            // 參照計數可避免「Connection already open」或誤關外層連線。
            await _context.Database.OpenConnectionAsync(ct);
            int nextSeq;
            try
            {
                var conn = _context.Database.GetDbConnection();
                await using var cmd = conn.CreateCommand();
                // 取號同時自我校準（防重發）：起跳值取「同一業務日期下各種重置週期期別鍵」的歷史最大序號。
                // 重置週期事後被改過（年↔月）時，新期別鍵會從 1 重新起跳，但單號日期段（如 YYMM 2607）
                // 不變 → 重發已用號碼、撞單據 PK 或過帳冪等檢查。改由取號當下比對同日期段的兄弟期別，
                // 不論週期是何時、由誰（畫面 Edit、直接改 DB、還原備份）改的都自動接續，不再依賴
                // RealignSequencesAsync 恰好被觸發（後者保留，於設定變更時先行校準）。
                // 兄弟期別只取「同一業務日期」推得的鍵（年/月/永不），故正常的跨年、跨月重置不受影響
                // （2027 年的鍵不會看到 2026 的序號），僅在期別鍵換過的情況下墊高＝跳號不重號。
                cmd.CommandText = """
                    WITH sib AS (
                        SELECT COALESCE(MAX("LastSeq"), 0) AS m
                        FROM "DocNumberSequences"
                        WHERE "DocType" = @docType AND "PlantId" = @plantId AND "Period" = ANY(@periods)
                    )
                    INSERT INTO "DocNumberSequences" ("DocType", "PlantId", "Period", "LastSeq")
                    SELECT @docType, @plantId, @period, (SELECT m FROM sib) + 1
                    ON CONFLICT ("DocType", "PlantId", "Period")
                    DO UPDATE SET "LastSeq" = GREATEST("DocNumberSequences"."LastSeq" + 1, EXCLUDED."LastSeq")
                    RETURNING "LastSeq"
                    """;
                AddParam(cmd, "@docType", docType);
                // 序號 scope = 解析到的設定之 ScopeKey（不變鍵，與可編輯的 PlantId 解耦）：
                // 共用預設規則（""）的廠別共用同一條序號（連續編、跨廠不重號）；
                // 有專屬規則的廠別各自獨立起算。增減適用廠別不會換掉此鍵，故不重發舊號。
                AddParam(cmd, "@plantId", ScopeOf(config));
                AddParam(cmd, "@period",  period);
                AddParam(cmd, "@periods", SiblingPeriods(date));
                var result = await cmd.ExecuteScalarAsync(ct);
                nextSeq = Convert.ToInt32(result);
            }
            finally
            {
                await _context.Database.CloseConnectionAsync();
            }

            return Build(config, date, nextSeq);
        }

        public string Preview(DocNumberConfig config, string plantId,
            DateOnly? date = null, int sampleSeq = 1)
            => Build(config, date ?? DateOnly.FromDateTime(DateTime.UtcNow), sampleSeq);

        public async Task RealignSequencesAsync(string docType, string plantScope, DocSeqReset newCycle, CancellationToken ct = default)
        {
            var period = BuildPeriod(newCycle, DateOnly.FromDateTime(DateTime.UtcNow));
            var seqs = await _context.DocNumberSequences
                .Where(s => s.DocType == docType && s.PlantId == plantScope)
                .ToListAsync(ct);
            if (seqs.Count == 0) return;   // 尚未編過號，無回頭風險

            var maxSeq = seqs.Max(s => s.LastSeq);
            var row = seqs.FirstOrDefault(s => s.Period == period);
            if (row == null)
            {
                _context.DocNumberSequences.Add(new DocNumberSequence
                {
                    DocType = docType,
                    PlantId = plantScope,
                    Period  = period,
                    LastSeq = maxSeq,
                });
            }
            else if (row.LastSeq < maxSeq)
            {
                row.LastSeq = maxSeq;
            }
            await _context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// 廠別解析：專屬規則優先（停用的專屬規則視同未設定），回退預設（""）；
        /// 兩者皆無 → 明確錯誤告知該廠別未設定編號規則。
        /// 專屬規則的 PlantId 可能是逗號分隔多廠別清單（多廠共用同一規則），故不能用 PK
        /// 精確比對，改為載入該 DocType 的專屬規則後以「清單是否包含」比對。
        /// </summary>
        private async Task<DocNumberConfig> ResolveConfigAsync(string docType, string plantId, CancellationToken ct)
        {
            var config = await TryResolveOwnRuleAsync(docType, plantId, ct);
            return config
                ?? await _context.DocNumberConfigs.FindAsync([docType, ""], ct)
                ?? throw new DocNumberConfigNotFoundException(docType, plantId);
        }

        /// <summary>找出 plantId 專屬（非預設）的有效規則；找不到回傳 null（不含預設規則 fallback）。</summary>
        private async Task<DocNumberConfig?> TryResolveOwnRuleAsync(string docType, string plantId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(plantId)) return null;
            var candidates = await _context.DocNumberConfigs
                .Where(c => c.DocType == docType && c.PlantId != "" && c.IsActive)
                .ToListAsync(ct);
            return candidates.FirstOrDefault(c => Applies(c, plantId));
        }

        /// <summary>此設定（PlantId 可能是逗號分隔多廠別清單）是否適用於指定廠別。</summary>
        public static bool Applies(DocNumberConfig config, string plantId) =>
            config.PlantIdList.Contains(plantId);

        /// <summary>
        /// 取得此設定的序號 scope 鍵。優先使用不變的 ScopeKey；ScopeKey 未設定（如舊資料或
        /// 直接建構的測試物件）時退回 PlantId，維持向後相容。app 建立路徑必設 ScopeKey，
        /// 故一般情形永遠使用 ScopeKey，與可編輯的 PlantId 解耦。
        /// </summary>
        public static string ScopeOf(DocNumberConfig config) =>
            string.IsNullOrEmpty(config.ScopeKey) ? config.PlantId : config.ScopeKey;

        public async Task<DocNumberConfig?> ResolveForPreviewAsync(string docType, string plantId, CancellationToken ct = default) =>
            await TryResolveOwnRuleAsync(docType, plantId, ct)
            ?? await _context.DocNumberConfigs.FindAsync([docType, ""], ct);

        /// <summary>
        /// 兩設定的編號規則指紋（前置碼＋分隔符＋日期格式）是否相同。
        /// 相同指紋的兩個獨立序號 scope 會產出相同單號字串（SeqLength 不同也可能在
        /// 位數溢位時重疊），故同一 DocType 的各適用廠別設定必須指紋互異。
        /// </summary>
        public static bool HasSameStem(DocNumberConfig a, DocNumberConfig b) =>
            a.Prefix == b.Prefix
            && (a.Separator ?? "") == (b.Separator ?? "")
            && a.DateFormat == b.DateFormat;

        /// <summary>
        /// 重置週期與日期格式的組合是否不會重發單號：
        /// 序號重置後，單號的日期段必須足以區分前後期別，否則同號必然重現。
        /// 月重置需含年月（YYMM/YYYYMMDD）；年重置需含年度（YY 以上）；永不重置皆可。
        /// </summary>
        public static bool IsReplaySafe(DocDateFormat dateFormat, DocSeqReset resetCycle) => resetCycle switch
        {
            DocSeqReset.Monthly => dateFormat is DocDateFormat.YYMM or DocDateFormat.YYYYMMDD,
            DocSeqReset.Yearly  => dateFormat != DocDateFormat.None,
            _                   => true,
        };

        // ── helpers ─────────────────────────────────────────────────────────

        private static string Build(DocNumberConfig config, DateOnly date, int seq)
        {
            var datePart = config.DateFormat switch
            {
                DocDateFormat.YY       => date.ToString("yy"),
                DocDateFormat.YYMM     => date.ToString("yyMM"),
                DocDateFormat.YYYYMMDD => date.ToString("yyyyMMdd"),
                _                      => ""
            };
            var seqPart = seq.ToString().PadLeft(config.SeqLength, '0');
            var sep = config.Separator;

            return config.DateFormat == DocDateFormat.None
                ? $"{config.Prefix}{sep}{seqPart}"
                : $"{config.Prefix}{sep}{datePart}{sep}{seqPart}";
        }

        private static string BuildPeriod(DocSeqReset reset, DateOnly date) => reset switch
        {
            DocSeqReset.Monthly => date.ToString("yyyyMM"),
            DocSeqReset.Yearly  => date.ToString("yyyy"),
            _                   => "----"
        };

        /// <summary>
        /// 同一業務日期在各重置週期下的期別鍵：這些鍵發出的單號共用同一段日期字串，
        /// 期別鍵換過時彼此會重號，故取號起跳值須取其最大者。
        /// 不含其他年月的鍵，正常的跨期重置因此不受影響。
        /// </summary>
        private static string[] SiblingPeriods(DateOnly date) =>
        [
            BuildPeriod(DocSeqReset.Yearly,  date),
            BuildPeriod(DocSeqReset.Monthly, date),
            BuildPeriod(DocSeqReset.Never,   date),
        ];

        private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }
}
