using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 途程工序等候時間建議值統計（mfg-domain.md §23.2-2）。結果寫入 <see cref="RoutingQueueTimeSuggestion"/>，
    /// <b>不改途程主檔</b>——規格定案「建議值＋一鍵套用，由工藝人員確認」。
    ///
    /// <para>樣本＝同一張工單相鄰兩道工序之間的實際等候：前一道派工最後一次下工 → 本道派工第一次上工，
    /// 以<b>工作時間</b>計（夜間、假日不算等候——排程把等候時間當工作時間走）。
    /// 樣本歸給<b>前一道</b>工序：排程與 MRP 把某工序的搬運＋等候時間放在<b>該工序之後</b>、下一道開工之前。
    /// 實測間隔含搬運，故建議等候＝間隔 − 該工序搬運時間（下限 0）。</para>
    ///
    /// <para>取代原本的自動回寫（從未生效：背景撈 0 筆），原作法另有四個錯誤：首道工序以工單建立時間當
    /// 到達時間（量到的是 MRP 前置期）、樣本記到後一道工序（與排程語意差一道）、搬運時間重複計入、
    /// 夜間假日算成等候。</para>
    /// </summary>
    public interface IQueueTimeSuggestionService
    {
        /// <summary>重算全部建議值（整批覆蓋），回傳有建議的工序數。</summary>
        Task<int> RecalculateAsync(DateTime utcNow, CancellationToken ct = default);
    }

    public class QueueTimeSuggestionService : IQueueTimeSuggestionService
    {
        public const int MinSampleSize = 5;
        public const int PeriodDays = 90;

        private readonly ApplicationDbContext _db;

        public QueueTimeSuggestionService(ApplicationDbContext db) => _db = db;

        public async Task<int> RecalculateAsync(DateTime utcNow, CancellationToken ct = default)
        {
            var since = utcNow.AddDays(-PeriodDays);

            // 一律 IgnoreQueryFilters：背景服務沒有登入使用者，全域過濾會讓查詢撈到 0 筆（原實作即如此）。
            // 1. 近 N 天開工、已完工、有前一道工序的派工
            var firstStart = await _db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .GroupBy(s => s.DispatchId)
                .Select(g => new { DispatchId = g.Key, Start = g.Min(s => s.StartAt) })
                .Where(x => x.Start >= since)
                .ToDictionaryAsync(x => x.DispatchId, x => x.Start, ct);

            var startedIds = firstStart.Keys.ToList();
            var dispatches = await (from d in _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                                    join w in _db.WorkOrders.IgnoreQueryFilters().AsNoTracking() on d.WorkOrderId equals w.WorkOrderId
                                    where d.Status == DispatchStatus.Closed && !d.IsOutsource
                                          && d.PrevDispatchId != null && startedIds.Contains(d.DispatchId)
                                    select new { d.DispatchId, d.PrevDispatchId, d.MachineCode, d.PlantId, w.RoutingId, d.WorkOrderId, d.OperationSeq })
                                   .ToListAsync(ct);
            if (dispatches.Count == 0) return await ReplaceAsync(new List<RoutingQueueTimeSuggestion>(), ct);

            var prevIds = dispatches.Select(d => d.PrevDispatchId!).Distinct().ToList();
            var prevs = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => prevIds.Contains(d.DispatchId) && !d.IsOutsource)
                .Select(d => new { d.DispatchId, d.WorkOrderId, d.OperationSeq })
                .ToDictionaryAsync(d => d.DispatchId, ct);
            var lastEnd = await _db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => prevIds.Contains(s.DispatchId) && s.EndAt != null)
                .GroupBy(s => s.DispatchId)
                .Select(g => new { DispatchId = g.Key, End = g.Max(s => s.EndAt!.Value) })
                .ToDictionaryAsync(x => x.DispatchId, x => x.End, ct);

            // 2. 工作日曆（各廠）與時區：報工時刻是真實 UTC，日曆時段是當地壁鐘分鐘
            var plantIds = dispatches.Select(d => d.PlantId).Where(p => p != null).Distinct().ToList();
            var tzByPlant = (await _db.Plants.AsNoTracking().Where(p => plantIds.Contains(p.Id))
                    .Select(p => new { p.Id, p.DefaultTimeZone }).ToListAsync(ct))
                .ToDictionary(p => p.Id, p => ResolveTimeZone(p.DefaultTimeZone));
            var fromDate = DateOnly.FromDateTime(since).AddDays(-2);
            var toDate   = DateOnly.FromDateTime(utcNow).AddDays(1);
            var segments = await _db.WorkCalendarSegments.IgnoreQueryFilters().AsNoTracking()
                .Where(s => plantIds.Contains(s.PlantId) && s.Date >= fromDate && s.Date <= toDate)
                .Select(s => new { s.PlantId, s.MachineCode, s.WorkCenterId, s.Date, s.StartMinute, s.EndMinute })
                .ToListAsync(ct);
            var segMap = segments
                .GroupBy(s => (s.PlantId, s.MachineCode, s.WorkCenterId, s.Date))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<(int Start, int End)>)g.Select(s => (s.StartMinute, s.EndMinute)).ToList());

            // 3. 途程工序（搬運時間、時間單位）與工單工序（取工作中心以找日曆）
            var routingIds = dispatches.Select(d => d.RoutingId).Distinct().ToList();
            var routingOps = await _db.Set<RoutingOperation>().AsNoTracking()
                .Where(ro => routingIds.Contains(ro.RoutingId))
                .ToDictionaryAsync(ro => (ro.RoutingId, ro.OperationSeq), ct);
            var woIds = dispatches.Select(d => d.WorkOrderId).Distinct().ToList();
            var woOpWc = await _db.WorkOrderOperations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => woIds.Contains(o.WorkOrderId))
                .Select(o => new { o.WorkOrderId, o.OperationSeq, o.WorkCenterId })
                .ToListAsync(ct);

            // 4. 樣本
            var samples = new Dictionary<(string RoutingId, int Seq), List<decimal>>();
            var gaps    = new Dictionary<(string RoutingId, int Seq), List<decimal>>();
            foreach (var d in dispatches)
            {
                if (d.PlantId == null
                    || !prevs.TryGetValue(d.PrevDispatchId!, out var prev)
                    || prev.WorkOrderId != d.WorkOrderId                       // 只量同工單相鄰工序
                    || !lastEnd.TryGetValue(prev.DispatchId, out var prevEndUtc)
                    || !firstStart.TryGetValue(d.DispatchId, out var startUtc)
                    || !routingOps.TryGetValue((d.RoutingId, prev.OperationSeq), out var prevRouteOp))
                    continue;

                var tz = tzByPlant.TryGetValue(d.PlantId, out var z) ? z : ResolveTimeZone(null);
                var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(prevEndUtc, DateTimeKind.Utc), tz);
                var toLocal   = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);

                // 等的是「本道」開工，用本道的機台／工作中心日曆（機台 > 工作中心 > 全廠）
                var wc = woOpWc.FirstOrDefault(o => o.WorkOrderId == d.WorkOrderId
                                                 && o.OperationSeq == d.OperationSeq)?.WorkCenterId;
                IReadOnlyList<(int Start, int End)> Segs(DateOnly date) =>
                    (d.MachineCode != null && segMap.TryGetValue((d.PlantId, d.MachineCode, wc, date), out var a)) ? a
                    : (wc != null && segMap.TryGetValue((d.PlantId, null, wc, date), out var b)) ? b
                    : segMap.TryGetValue((d.PlantId, null, null, date), out var c) ? c
                    : Array.Empty<(int, int)>();

                var gap = WorkingMinutesBetween(fromLocal, toLocal, Segs);
                var moveMin = OperationTimeUnitExtensions.ToMinutes(prevRouteOp.MoveTime, prevRouteOp.TimeUnit);

                var key = (d.RoutingId, prev.OperationSeq);
                (gaps.TryGetValue(key, out var gl) ? gl : gaps[key] = new List<decimal>()).Add(gap);
                (samples.TryGetValue(key, out var sl) ? sl : samples[key] = new List<decimal>()).Add(Math.Max(0m, gap - moveMin));
            }

            var rows = samples
                .Where(kv => kv.Value.Count >= MinSampleSize)
                .Select(kv => new RoutingQueueTimeSuggestion
                {
                    RoutingId = kv.Key.RoutingId,
                    OperationSeq = kv.Key.Seq,
                    SuggestedQueueMinutes = Math.Round(Median(kv.Value), 2, MidpointRounding.AwayFromZero),
                    MedianGapMinutes = Math.Round(Median(gaps[kv.Key]), 2, MidpointRounding.AwayFromZero),
                    SampleCount = kv.Value.Count,
                    PeriodDays = PeriodDays,
                    CalculatedAt = utcNow
                })
                .ToList();

            return await ReplaceAsync(rows, ct);
        }

        private async Task<int> ReplaceAsync(List<RoutingQueueTimeSuggestion> rows, CancellationToken ct)
        {
            _db.RoutingQueueTimeSuggestions.RemoveRange(await _db.RoutingQueueTimeSuggestions.ToListAsync(ct));
            _db.RoutingQueueTimeSuggestions.AddRange(rows);
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }

        /// <summary>
        /// 兩個當地時刻之間落在可用時段內的分鐘數（夜間、假日、休息不算）。
        /// <paramref name="to"/> 早於 <paramref name="from"/>（工序重疊：下一道在前一道收完前就開工）回 0。
        /// 沒有日曆設定的日期整天不計——寧可少算也不要把沒排班的日子算成等候。
        /// </summary>
        public static decimal WorkingMinutesBetween(
            DateTime from, DateTime to, Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments)
        {
            if (to <= from) return 0m;
            decimal total = 0;
            for (var date = DateOnly.FromDateTime(from); date <= DateOnly.FromDateTime(to); date = date.AddDays(1))
            {
                var dayStart = date.ToDateTime(TimeOnly.MinValue);
                var winFrom = Math.Max(0, (int)Math.Floor((from - dayStart).TotalMinutes));
                var winTo   = Math.Min(1440, (int)Math.Ceiling((to - dayStart).TotalMinutes));
                foreach (var (s, e) in getSegments(date))
                {
                    var a = Math.Max(s, winFrom);
                    var b = Math.Min(e, winTo);
                    if (b > a) total += b - a;
                }
            }
            return total;
        }

        public static decimal Median(IReadOnlyList<decimal> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2m : sorted[mid];
        }

        internal static TimeZoneInfo ResolveTimeZone(string? id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "Asia/Taipei" : id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }
        }
    }
}
