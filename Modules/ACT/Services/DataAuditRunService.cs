using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <inheritdoc cref="IDataAuditRunService"/>
    public class DataAuditRunService : IDataAuditRunService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDataAuditService _audit;

        /// <summary>執行紀錄保留天數；逾期於下次執行時清除（Cascade 連同 Findings）。</summary>
        public const int RetentionDays = 90;

        public DataAuditRunService(ApplicationDbContext db, IDataAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        public async Task<DataAuditRun> RunAndPersistAsync(
            string? plantId, string triggerType, string createdBy, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = await _audit.RunAllAsync(plantId, ct);
            sw.Stop();

            var run = new DataAuditRun
            {
                RunAt = DateTime.UtcNow,
                TriggerType = triggerType,
                PlantId = plantId,
                FailureCount = result.Failures.Count,
                DurationMs = sw.ElapsedMilliseconds,
                CreatedBy = createdBy,
                Findings = result.Failures.Select(f => new DataAuditFinding
                {
                    AuditType = f.AuditType,
                    DocId = f.DocId,
                    ProductCode = f.ProductCode,
                    PlantId = f.PlantId,
                    DifferenceAmount = f.DifferenceAmount,
                    BlockingOverride = f.BlockingOverride,
                    // 描述含動態單號/數字，防禦性截斷避免超出欄位長度
                    ErrorDescription = f.ErrorDescription.Length > 1000
                        ? f.ErrorDescription[..1000] : f.ErrorDescription,
                    // 鍵＋參數才是可在地化的那一份；ErrorDescription 只是產生當下的中文快照。
                    MessageKey  = f.MessageKey,
                    MessageArgs = Truncate(AuditMessageTemplates.ArgsToJson(f.MessageArgs), 2000),
                }).ToList(),
            };
            _db.DataAuditRuns.Add(run);

            // 清除逾保留期的舊紀錄（Include Findings：InMemory 只級聯已追蹤子項，真 PG 亦有 DB 級 Cascade）
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var stale = await _db.DataAuditRuns.Include(r => r.Findings)
                .Where(r => r.RunAt < cutoff).ToListAsync(ct);
            if (stale.Count > 0) _db.DataAuditRuns.RemoveRange(stale);

            await _db.SaveChangesAsync(ct);
            return run;
        }

        /// <summary>
        /// 參數 JSON 超長時寧可**整個丟掉**也不截斷：截一半的 JSON 反序列化必失敗，
        /// 顯示端會退回 ErrorDescription，等於多存了一欄永遠用不到的壞資料。
        /// 丟掉之後顯示端一樣退回 ErrorDescription，結果相同但資料庫裡不留垃圾。
        /// </summary>
        private static string? Truncate(string json, int max)
            => json.Length <= max ? json : null;
    }
}
