using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>資料稽核診斷儀表板查詢服務（§9 P-8）。</summary>
    public interface IDataAuditReportService
    {
        /// <summary>組最近一次執行的儀表板資料；異常明細以廠別過濾（含無廠別維度的異常）。</summary>
        Task<DataAuditReportVm> BuildAsync(string plantId, string? auditType, CancellationToken ct = default);
    }

    /// <inheritdoc cref="IDataAuditReportService"/>
    public class DataAuditReportService : IDataAuditReportService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;

        /// <summary>趨勢圖取用的執行次數上限。</summary>
        internal const int TrendRunCount = 30;

        public DataAuditReportService(ApplicationDbContext db, IDateTimeService dt)
        {
            _db = db;
            _dt = dt;
        }

        public async Task<DataAuditReportVm> BuildAsync(string plantId, string? auditType, CancellationToken ct = default)
        {
            // RPT 慣例：IgnoreQueryFilters + 明確 plantId。
            // 稽核執行是全廠的（PlantId=null），異常明細各自帶廠別 → 只呈現本廠 + 無廠別維度的異常。
            var latest = await _db.DataAuditRuns.IgnoreQueryFilters().AsNoTracking()
                .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId)
                .FirstOrDefaultAsync(ct);
            if (latest == null) return new DataAuditReportVm();

            var findings = await _db.DataAuditFindings.IgnoreQueryFilters().AsNoTracking()
                .Where(f => f.RunId == latest.RunId && (f.PlantId == plantId || f.PlantId == null))
                .OrderBy(f => f.AuditType).ThenBy(f => f.DocId)
                .Select(f => new DataAuditFindingRow
                {
                    AuditType = f.AuditType,
                    DocId = f.DocId,
                    ProductCode = f.ProductCode,
                    PlantId = f.PlantId,
                    DifferenceAmount = f.DifferenceAmount,
                    ErrorDescription = f.ErrorDescription,
                    MessageKey       = f.MessageKey,
                    MessageArgs      = f.MessageArgs,
                    BlockingOverride = f.BlockingOverride,
                }).ToListAsync(ct);

            var auditTypes = findings.Select(f => f.AuditType).Distinct().OrderBy(t => t).ToList();

            // 趨勢：最近 N 次執行 × 廠別範圍內異常筆數（含 0 筆的執行）
            var recentRuns = await _db.DataAuditRuns.IgnoreQueryFilters().AsNoTracking()
                .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.RunId)
                .Take(TrendRunCount)
                .Select(r => new { r.RunId, r.RunAt })
                .ToListAsync(ct);
            var runIds = recentRuns.Select(r => r.RunId).ToList();
            var countByRun = await _db.DataAuditFindings.IgnoreQueryFilters().AsNoTracking()
                .Where(f => runIds.Contains(f.RunId) && (f.PlantId == plantId || f.PlantId == null))
                .GroupBy(f => f.RunId)
                .Select(g => new { RunId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RunId, x => x.Count, ct);

            return new DataAuditReportVm
            {
                LatestRunAtLocal = _dt.ToLocalTime(latest.RunAt),
                TriggerType = latest.TriggerType,
                FailureCount = findings.Count,
                AuditTypeCount = auditTypes.Count,
                DurationMs = latest.DurationMs,
                SelectedAuditType = auditType,
                AuditTypes = auditTypes,
                Findings = string.IsNullOrEmpty(auditType)
                    ? findings
                    : findings.Where(f => f.AuditType == auditType).ToList(),
                Trend = recentRuns
                    .OrderBy(r => r.RunAt)
                    .Select(r => new DataAuditTrendPoint
                    {
                        Label = _dt.ToLocalTime(r.RunAt).ToString("MM-dd HH:mm"),
                        Count = countByRun.GetValueOrDefault(r.RunId),
                    }).ToList(),
            };
        }
    }
}
