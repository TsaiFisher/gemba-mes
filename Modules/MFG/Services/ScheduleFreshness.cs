using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>排程的觸發方式（記錄於 <see cref="ScheduleRunState.LastTrigger"/>）。</summary>
    public enum ScheduleTrigger
    {
        Manual,
        OnChange,
        BeforeDispatch,
        Daily
    }

    /// <summary>
    /// 排程是否已過時。<paramref name="FirstChangeAt"/>／<paramref name="LastChangeAt"/> 是上次排程之後
    /// 第一筆／最後一筆異動的時刻（UTC）；只有日曆或計劃停機變了（無時間可查）時兩者為 null、
    /// <paramref name="CalendarChanged"/> 為 true。
    /// </summary>
    public record ScheduleFreshnessInfo(
        DateTime? LastRunAt,
        string? LastTrigger,
        bool IsStale,
        DateTime? FirstChangeAt,
        DateTime? LastChangeAt,
        bool CalendarChanged,
        int PendingCount,
        int OnHoldCount);

    /// <summary>
    /// 由資料推導「排程是否已過時」，不靠各作業點逐一發事件——任何路徑（桌機、手機、背景、匯入）
    /// 改了資料都看得到，重開機也不會遺失。
    ///
    /// <para>以上次排程的 <see cref="ScheduleRunState.DataAsOf"/>（開始讀資料的時刻）為基準，下列任一成立即過時：</para>
    /// <list type="bullet">
    /// <item>派工在那之後變成待排（投產、拆分、換機、機台停用退回）、完工或取消（產能釋出）</item>
    /// <item>工單在那之後投產、作廢、完工或結案</item>
    /// <item>生產批在那之後有異動（優先序）</item>
    /// <item>機台狀態異動單在那之後建立</item>
    /// <item>工作中心在那之後有異動（新增機台等）</item>
    /// <item>工作日曆或計劃停機的指紋與上次不同（這兩張表沒有異動時間、停機還可能被刪除）</item>
    /// </list>
    /// 排程引擎自己的寫入不會觸發：它只把派工改為已排、或把排不進的改回待排而不動異動時間；
    /// 凍結派工重估雖改異動時間，但狀態不在上列。
    ///
    /// <para>所有查詢一律 IgnoreQueryFilters＋明確 plantId：背景服務沒有登入使用者，全域過濾會撈到 0 筆。</para>
    /// </summary>
    public static class ScheduleFreshness
    {
        /// <summary>排程視窗天數（與 SchedulingEngine 載入日曆的區間一致）。</summary>
        public const int WindowDays = 60;

        private static readonly DispatchStatus[] DispatchChangeStatuses =
            { DispatchStatus.Pending, DispatchStatus.Closed, DispatchStatus.Cancelled };

        private static readonly WorkOrderStatus[] WorkOrderChangeStatuses =
            { WorkOrderStatus.Released, WorkOrderStatus.Void, WorkOrderStatus.Completed, WorkOrderStatus.Closed };

        public static async Task<ScheduleFreshnessInfo> GetAsync(
            ApplicationDbContext db, string plantId, CancellationToken ct = default)
        {
            var state = await db.ScheduleRunStates.AsNoTracking().FirstOrDefaultAsync(s => s.PlantId == plantId, ct);

            var openWo = db.WorkOrders.IgnoreQueryFilters()
                .Where(w => w.PlantId == plantId
                         && (w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress))
                .Select(w => w.WorkOrderId);
            var pending = await db.Dispatches.IgnoreQueryFilters()
                .CountAsync(d => d.PlantId == plantId && d.Status == DispatchStatus.Pending && openWo.Contains(d.WorkOrderId), ct);
            var onHold = await db.Dispatches.IgnoreQueryFilters()
                .CountAsync(d => d.PlantId == plantId && d.Status == DispatchStatus.OnHold && openWo.Contains(d.WorkOrderId), ct);

            if (state == null)
            {
                // 從未排過：有待排就算過時（時間未知）
                return new ScheduleFreshnessInfo(null, null, pending > 0, null, null, false, pending, onHold);
            }

            var since = state.DataAsOf;
            var stamps = new List<DateTime>();
            async Task Collect(IQueryable<DateTime> q)
            {
                var min = await q.Select(t => (DateTime?)t).MinAsync(ct);
                if (min == null) return;
                stamps.Add(min.Value);
                stamps.Add((await q.Select(t => (DateTime?)t).MaxAsync(ct))!.Value);
            }

            foreach (var q in ChangeStamps(db, plantId, since))
                await Collect(q);

            var calendarChanged = false;
            if (state.FingerprintFrom is { } from && state.FingerprintTo is { } to)
                calendarChanged = await ComputeFingerprintAsync(db, plantId, from, to, ct) != state.CalendarFingerprint;

            var stale = stamps.Count > 0 || calendarChanged;
            return new ScheduleFreshnessInfo(
                state.LastRunAt, state.LastTrigger, stale,
                stamps.Count > 0 ? stamps.Min() : null,
                stamps.Count > 0 ? stamps.Max() : null,
                calendarChanged, pending, onHold);
        }

        /// <summary>
        /// 本廠目前最新一筆「會讓排程過時」的異動時間（與 <see cref="GetAsync"/> 同五類來源）；沒有任何資料時為 null。
        /// 排程引擎用它校正 <c>DataAsOf</c>：系統時鐘回撥（WSL 實測一輪數次、每次數秒）時，
        /// 已經存在、這次排程也讀得到的異動，時間戳可能比「現在」還晚——不校正的話剛排完就被判定過時，
        /// 「派工前自動重排」會一直重排、排程頁一直掛著過時提示。
        /// </summary>
        public static async Task<DateTime?> LatestChangeAtAsync(
            ApplicationDbContext db, string plantId, CancellationToken ct = default)
        {
            DateTime? latest = null;
            foreach (var q in ChangeStamps(db, plantId, DateTime.MinValue))
            {
                var max = await q.Select(t => (DateTime?)t).MaxAsync(ct);
                if (max != null && (latest == null || max > latest)) latest = max;
            }
            return latest;
        }

        /// <summary>判斷過時的五類異動時間來源（<paramref name="since"/> 之後的）。</summary>
        private static IEnumerable<IQueryable<DateTime>> ChangeStamps(ApplicationDbContext db, string plantId, DateTime since)
        {
            yield return db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.PlantId == plantId && d.UpdatedAt > since && DispatchChangeStatuses.Contains(d.Status))
                .Select(d => d.UpdatedAt);
            yield return db.WorkOrders.IgnoreQueryFilters()
                .Where(w => w.PlantId == plantId && w.UpdatedAt > since && WorkOrderChangeStatuses.Contains(w.Status))
                .Select(w => w.UpdatedAt);
            yield return db.ProductionBatches.IgnoreQueryFilters()
                .Where(b => b.PlantId == plantId && b.UpdatedAt > since)
                .Select(b => b.UpdatedAt);
            yield return db.MachineStatusOrders.IgnoreQueryFilters()
                .Where(o => o.PlantId == plantId && o.CreatedAt > since)
                .Select(o => o.CreatedAt);
            yield return db.WorkCenters.IgnoreQueryFilters()
                .Where(w => w.PlantId == plantId && w.UpdatedAt > since)
                .Select(w => w.UpdatedAt);
        }

        /// <summary>
        /// 工作日曆時段＋計劃停機（區間內）的指紋。內容排序後以 FNV-1a 64 位元雜湊——
        /// 只用來判斷「與上次是否相同」，不需抗碰撞。
        /// </summary>
        public static async Task<long> ComputeFingerprintAsync(
            ApplicationDbContext db, string plantId, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            var segs = await db.WorkCalendarSegments.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PlantId == plantId && s.Date >= from && s.Date <= to)
                .Select(s => new { s.Date, s.MachineCode, s.WorkCenterId, s.Seq, s.StartMinute, s.EndMinute })
                .ToListAsync(ct);

            var fromDt = from.ToDateTime(TimeOnly.MinValue);
            var toDt   = to.ToDateTime(new TimeOnly(23, 59));
            var downs = await db.PlannedDowntimes.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.PlantId == plantId && d.IsActive && d.EndTime >= fromDt && d.StartTime <= toDt)
                .Select(d => new { d.MachineCode, d.WorkCenterId, d.StartTime, d.EndTime })
                .ToListAsync(ct);

            var lines = segs
                .Select(s => string.Create(CultureInfo.InvariantCulture,
                    $"S|{s.Date:yyyyMMdd}|{s.MachineCode}|{s.WorkCenterId}|{s.Seq}|{s.StartMinute}|{s.EndMinute}"))
                .Concat(downs.Select(d => string.Create(CultureInfo.InvariantCulture,
                    $"D|{d.MachineCode}|{d.WorkCenterId}|{d.StartTime:O}|{d.EndTime:O}")))
                .OrderBy(x => x, StringComparer.Ordinal);

            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var line in lines)
                {
                    foreach (var ch in line) { hash ^= ch; hash *= 1099511628211UL; }
                    hash ^= '\n'; hash *= 1099511628211UL;
                }
                return (long)hash;
            }
        }
    }

    /// <summary>
    /// 某廠的自動重排設定（全域預設＋廠別覆寫，經 <see cref="IPlantParamService"/>）。
    /// <paramref name="Rule"/>＝自動重排的排序規則（未設定＝Priority）；
    /// <paramref name="FreezeHours"/>＝凍結時界小時數（未設定或 0＝不使用，為 null）。
    /// </summary>
    public record AutoScheduleSettings(
        bool OnChange, bool BeforeDispatch, TimeOnly? DailyTime,
        ScheduleRule Rule = ScheduleRule.Priority, decimal? FreezeHours = null)
    {
        /// <summary>凍結時界過大等於不重排；上限一週。</summary>
        public const decimal MaxFreezeHours = 168;

        public bool AnyEnabled => OnChange || BeforeDispatch || DailyTime != null;

        /// <summary>傳給排程引擎的凍結時界。</summary>
        public TimeSpan? FreezeWindow => FreezeHours is { } h ? TimeSpan.FromHours((double)h) : null;

        public static async Task<AutoScheduleSettings> LoadAsync(
            IPlantParamService plantParams, string plantId, CancellationToken ct = default)
        {
            static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            return new AutoScheduleSettings(
                IsTrue(await plantParams.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleOnChange, plantId, ct)),
                IsTrue(await plantParams.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch, plantId, ct)),
                ParseDailyTime(await plantParams.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleDailyTime, plantId, ct)),
                ParseRule(await plantParams.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleRule, plantId, ct)),
                ParseFreezeHours(await plantParams.GetAsync(ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours, plantId, ct)));
        }

        /// <summary>規則名稱（不分大小寫）；空白或不認得回 Priority＝原行為。</summary>
        public static ScheduleRule ParseRule(string? raw) =>
            Enum.TryParse<ScheduleRule>(raw?.Trim(), ignoreCase: true, out var r) && Enum.IsDefined(r)
                ? r : ScheduleRule.Priority;

        /// <summary>正數小時（上限 <see cref="MaxFreezeHours"/>）；空白、0、負數或格式不符回 null＝不使用。</summary>
        public static decimal? ParseFreezeHours(string? raw) =>
            decimal.TryParse(raw?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h > 0
                ? Math.Min(h, MaxFreezeHours) : null;

        /// <summary>存檔用：合法規則轉標準名稱，其餘回 null（清除）。</summary>
        public static string? NormalizeRule(string? raw) =>
            Enum.TryParse<ScheduleRule>(raw?.Trim(), ignoreCase: true, out var r) && Enum.IsDefined(r) ? r.ToString() : null;

        /// <summary>存檔用：合法時界轉字串，其餘回 null（清除）。</summary>
        public static string? NormalizeFreezeHours(string? raw) =>
            ParseFreezeHours(raw)?.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>解析 HH:mm（也接受 H:mm）；空白或格式不符回 null＝不使用。</summary>
        public static TimeOnly? ParseDailyTime(string? raw) =>
            TimeOnly.TryParseExact(raw?.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t) ? t : null;

        /// <summary>存檔用：合法時刻轉成 HH:mm，其餘回 null（清除）。</summary>
        public static string? NormalizeDailyTime(string? raw) =>
            ParseDailyTime(raw)?.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
