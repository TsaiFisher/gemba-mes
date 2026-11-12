using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>一次全量校準的結果：時段有變的派工筆數，以及本次新轉為「預計延遲」的生產批。</summary>
    public sealed record EtaCalibrationResult(int Moved, IReadOnlyList<(string BatchId, DateTime PlannedEnd, DateTime Eta)> NewlyLate)
    {
        public static EtaCalibrationResult Skipped { get; } = new(0, Array.Empty<(string, DateTime, DateTime)>());
    }

    /// <summary>
    /// 每晚全量 ETA 校準（mfg-domain.md §7.5 層級 3／§10.12「全量」策略）。
    ///
    /// <para>報工即時重算只平移未來 48 小時、50 張以內的派工；超出的遠期派工靠這裡依實際進度重算，
    /// 並重新判定各生產批是否預計延遲。</para>
    ///
    /// <para><b>與排程同一套規則</b>：直接以排程引擎的校準模式（<see cref="ScheduleRunMode.PreserveAssignment"/>）
    /// 重算——工序重疊、倒推錨點、工作日曆、計劃停機、凍結派工依未完成量重估都相同，前面延誤就往後、
    /// 前面提早就跟著提前。差別只在不換機台、不改先後、不排入待排派工；那些屬於重排排程，
    /// 由各廠的自動重排設定（或手動執行排程）決定。</para>
    /// </summary>
    public interface IEtaCalibrationService
    {
        Task<EtaCalibrationResult> CalibratePlantAsync(string plantId, DateTime now, CancellationToken ct = default);
    }

    public class EtaCalibrationService : IEtaCalibrationService
    {
        private readonly ApplicationDbContext _db;
        private readonly ISchedulingEngine _engine;

        public EtaCalibrationService(ApplicationDbContext db, ISchedulingEngine engine)
        {
            _db = db;
            _engine = engine;
        }

        public async Task<EtaCalibrationResult> CalibratePlantAsync(string plantId, DateTime now, CancellationToken ct = default)
        {
            var before = await SlotsAsync(plantId, ct);

            // 引擎自己會拿「排程:廠別」鎖；有人正在排程就略過本廠（排程本身也會重估凍結派工）
            var run = await _engine.RunAsync(plantId, ScheduleRule.Priority, now, runBy: null,
                ScheduleTrigger.Manual, ct, ScheduleRunMode.PreserveAssignment);
            if (run.Busy) return EtaCalibrationResult.Skipped;

            _db.ChangeTracker.Clear();
            var after = await SlotsAsync(plantId, ct);
            var moved = after.Count(kv => before.TryGetValue(kv.Key, out var b) && b != kv.Value);

            var newlyLate = await RefreshPredictedLateAsync(plantId, ct);
            await _db.SaveChangesAsync(ct);
            return new EtaCalibrationResult(moved, newlyLate);
        }

        /// <summary>
        /// 本廠未結案工單的派工時段快照。一律 IgnoreQueryFilters＋明確 plantId：背景沒有登入使用者，
        /// 全域過濾是 PlantId == null，原實作因此每晚撈到 0 筆、從來沒有校準過任何東西。
        /// </summary>
        private async Task<Dictionary<string, (DateTime? Start, DateTime? End)>> SlotsAsync(string plantId, CancellationToken ct) =>
            await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == plantId && d.ScheduledStart != null
                         && _db.WorkOrders.Any(w => w.WorkOrderId == d.WorkOrderId
                                && (w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress)))
                .ToDictionaryAsync(d => d.DispatchId, d => (d.ScheduledStart, d.ScheduledEnd), ct);

        /// <summary>
        /// 依校準後的時段重判各生產批是否預計延遲（最晚未完成派工的計畫完工 &gt; 生產批計畫完工日）。
        /// 回傳由「否」轉「是」者；轉回「否」只更新旗標、不通知。不存檔。
        /// </summary>
        private async Task<IReadOnlyList<(string BatchId, DateTime PlannedEnd, DateTime Eta)>> RefreshPredictedLateAsync(
            string plantId, CancellationToken ct)
        {
            var batches = await _db.ProductionBatches.IgnoreQueryFilters()
                .Where(b => b.PlantId == plantId
                         && _db.WorkOrders.IgnoreQueryFilters().Any(w => w.ProductionBatchId == b.BatchId
                                && (w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress)))
                .ToListAsync(ct);
            if (batches.Count == 0) return Array.Empty<(string, DateTime, DateTime)>();

            var batchIds = batches.Select(b => b.BatchId).ToList();
            // 與報工即時路徑（ReportEtaNotifier）同口徑：批內所有未完工、未取消派工的最晚計畫完工
            // （引擎已存檔，這裡讀到的就是校準後的時段）。
            var ends = await (from d in _db.Dispatches.IgnoreQueryFilters()
                              join w in _db.WorkOrders.IgnoreQueryFilters() on d.WorkOrderId equals w.WorkOrderId
                              where batchIds.Contains(w.ProductionBatchId)
                                 && d.ScheduledEnd != null
                                 && d.Status != DispatchStatus.Closed && d.Status != DispatchStatus.Cancelled
                              select new { d.DispatchId, w.ProductionBatchId, d.ScheduledEnd })
                             .ToListAsync(ct);
            var lastEndByBatch = ends
                .GroupBy(x => x.ProductionBatchId)
                .ToDictionary(g => g.Key, g => g.Max(x => x.ScheduledEnd!.Value));

            var newlyLate = new List<(string, DateTime, DateTime)>();
            foreach (var b in batches)
            {
                if (!lastEndByBatch.TryGetValue(b.BatchId, out var lastEnd)) continue;
                var late = lastEnd > b.PlannedEndDate;
                if (late == b.IsPredictedLate) continue;
                b.IsPredictedLate = late;
                if (late) newlyLate.Add((b.BatchId, b.PlannedEndDate, lastEnd));
            }
            return newlyLate;
        }
    }
}
