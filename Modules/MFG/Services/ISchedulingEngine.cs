using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>排程排序規則（mfg-domain.md §7.2）</summary>
    public enum ScheduleRule
    {
        Priority,   // 依 CN_P 數字小者優先（預設）
        EDD,        // Earliest Due Date
        SPT,        // Shortest Processing Time（最短工時）
        FIFO,       // 先進先出（依 MrpRun 建立時間）
        LIFO,       // 後進先出
    }

    /// <summary>
    /// 預載日曆查表（mfg-domain.md §7.1 批次預載）。
    /// 機台 > 工作中心 > 全廠三層 fallback；PlannedDowntime 一并攜帶以供排程相減。
    /// </summary>
    public sealed class CalendarContext
    {
        private readonly IReadOnlyDictionary<(string? Machine, string? WorkCenter, DateOnly Date),
                                              IReadOnlyList<WorkCalendarSegmentBuilder.Segment>> _map;

        public IReadOnlyList<PlannedDowntime> Downtimes { get; }

        public CalendarContext(
            IReadOnlyDictionary<(string?, string?, DateOnly), IReadOnlyList<WorkCalendarSegmentBuilder.Segment>> map,
            IReadOnlyList<PlannedDowntime> downtimes)
        {
            _map = map;
            Downtimes = downtimes;
        }

        /// <summary>
        /// 取某日可用時段（機台 → 工作中心 → 全廠三層 fallback）。
        /// </summary>
        public IReadOnlyList<WorkCalendarSegmentBuilder.Segment> GetSegments(
            string? machineCode, string? workCenterId, DateOnly date)
        {
            if (machineCode != null && _map.TryGetValue((machineCode, workCenterId, date), out var s1)) return s1;
            if (workCenterId != null && _map.TryGetValue((null, workCenterId, date), out var s2)) return s2;
            return _map.TryGetValue((null, null, date), out var s3) ? s3 : Array.Empty<WorkCalendarSegmentBuilder.Segment>();
        }

        /// <summary>
        /// 取計劃停機中與此機台/工作中心相關的絕對時間區段。
        /// </summary>
        public IReadOnlyList<(DateTime From, DateTime To)> GetDowntimeRanges(
            string? machineCode, string? workCenterId)
        {
            var result = new List<(DateTime, DateTime)>();
            foreach (var dt in Downtimes)
            {
                if (!dt.IsActive) continue;
                // 機台級：精確比對
                if (dt.MachineCode != null)
                {
                    if (dt.MachineCode == machineCode)
                        result.Add((dt.StartTime, dt.EndTime));
                }
                // 工作中心級：套用到該中心所有機台
                else if (dt.WorkCenterId != null)
                {
                    if (dt.WorkCenterId == workCenterId)
                        result.Add((dt.StartTime, dt.EndTime));
                }
            }
            return result;
        }
    }

    /// <summary>單張派工排程結果</summary>
    public record DispatchSlot(string? MachineCode, DateTime Start, DateTime End);

    /// <summary>
    /// 前工序脈絡（mfg-domain.md §7.3 b：工序重疊推算用）。
    ///
    /// 移轉方式（CD_BOR）與移轉批量（QT_BOR）取自**前工序**——它決定「怎麼把半成品交給下一站」，
    /// 不是本工序的屬性。前工序為委外時無工時概念，一律以 WholeOrder 處理（傳 null 即可）。
    ///
    /// 宣告為 record struct 而非 record：這是排程計算用的不可變值組合，不是會被繫結/驗證的
    /// 表單模型；R5b（欄位名需有 SharedResource 雙語 key）只掃類別，值型別自然不在其射程內。
    /// </summary>
    /// <param name="Start">前工序開始時刻（多張兄弟派工取最早）。</param>
    /// <param name="End">前工序完成時刻（多張兄弟派工取最晚，已含 Move/Queue 間隔）。</param>
    /// <param name="WorkMinutes">前工序總工時（分，多張兄弟派工加總）——與本工序比大小決定往前或往後推。</param>
    /// <param name="Mode">前工序移轉方式（連續／定量／整批）。</param>
    /// <param name="TransferQty">前工序移轉批量 QT_BOR（定量模式用）。</param>
    /// <param name="SetupMinutes">前工序準備工時（分）。</param>
    /// <param name="UnitMinutes">前工序單件工時（分）。</param>
    /// <param name="GapMinutes">前工序的 Move+Queue 間隔（分），已計入 <paramref name="End"/>。</param>
    public readonly record struct PrevOpContext(
        DateTime      Start,
        DateTime      End,
        decimal       WorkMinutes,
        OperationMode Mode,
        decimal       TransferQty,
        decimal       SetupMinutes,
        decimal       UnitMinutes,
        decimal       GapMinutes);

    /// <summary>
    /// 凍結派工（生產中家族）重估的輸入。取代原本就地讀 Dispatch 匿名型別的寫法，
    /// 讓重估成為可單元測試的純函式（不需 DbContext）。
    ///
    /// 與 <see cref="PrevOpContext"/> 同理宣告為 record struct：純計算用的不可變值組合，
    /// 不是會被繫結/驗證的表單模型，R5b 只掃類別故值型別不在其射程內。
    /// </summary>
    /// <param name="WorkOrderId">所屬工單——排先後時要靠它查 BOM 母子關係。</param>
    /// <param name="OperationSeq">工序序號——同深度時的次序。</param>
    /// <param name="BatchId">所屬生產批；先後接力只在同一批內成立（跨批之間沒有物料相依）。</param>
    public readonly record struct FrozenDispatchInput(
        string         DispatchId,
        string         WorkOrderId,
        int            OperationSeq,
        string?        BatchId,
        string?        MachineCode,
        DateTime?      ScheduledStart,
        DateTime?      ScheduledEnd,
        decimal        Quantity,
        decimal        QtyDone,
        DispatchStatus Status,
        bool           IsOutsource);

    /// <summary>
    /// 凍結派工重估結果。<paramref name="Recalculated"/> 為 false 代表沿用帳面時段
    /// （委外、或剩餘量已為 0、或查不到工序），呼叫端不寫回資料庫。
    /// </summary>
    public readonly record struct FrozenDispatchEstimate(
        DateTime Start,
        DateTime End,
        bool     Recalculated);

    /// <summary>排程執行方式。</summary>
    public enum ScheduleRunMode
    {
        /// <summary>全量排程：待排與已排的派工依排序規則重新配置機台與時段。</summary>
        Full,

        /// <summary>
        /// 校準（每晚 01:00 ETA 全量校準用）：與全量排程**同一套時間規則**（工序重疊、倒推錨點、工作日曆、
        /// 計劃停機、凍結派工依未完成量重估，可往後也可提前），但**維持每張已排派工目前的機台與先後**、
        /// 不排入待排派工——換機台、改順序、排新工作是重排排程的事，由各廠的自動重排設定決定。
        /// 不更新 ScheduleRunState（校準不代表已納入新異動）。
        /// </summary>
        PreserveAssignment
    }

    /// <summary>
    /// 全量排程執行結果。<paramref name="Busy"/>＝同一廠別已有另一次排程正在執行，本次未執行（什麼都沒改）。
    /// <paramref name="Fenced"/>＝因凍結時界保留原機台與時段的已排派工數。
    /// </summary>
    public record ScheduleRunResult(
        int Scheduled,
        int Skipped,
        IReadOnlyList<string> SkippedDispatchIds,
        bool Busy = false,
        int Fenced = 0)
    {
        public static ScheduleRunResult BusyResult { get; } = new(0, 0, Array.Empty<string>(), Busy: true);
    }

    public interface ISchedulingEngine
    {
        /// <summary>
        /// 批次預載 WKC + WorkCalendarSegment 至記憶體查表（mfg-domain.md §7.1）。
        /// 建議上限 60 天；排程迴圈中不得逐格呼叫 GetWorkHoursAsync。
        /// </summary>
        Task<CalendarContext> LoadCalendarContextAsync(
            string plantId, DateOnly dateFrom, DateOnly dateTo,
            CancellationToken ct = default);

        /// <summary>
        /// 全量排程（mfg-domain.md §7）：對指定廠別所有 Released 工單的 Pending 派工，
        /// 依排序規則分配機台與時段，寫入 Dispatch.ScheduledStart / ScheduledEnd。
        /// 同一廠別同時只會有一次排程在執行；另一次正在跑時立即回 <see cref="ScheduleRunResult.Busy"/>、不做任何異動。
        /// 完成後記錄於 <see cref="Models.ScheduleRunState"/>（<paramref name="runBy"/>＝執行者，背景執行為 null），
        /// 並寫一筆 <see cref="Models.ScheduleRunLog"/>（含異動明細；校準模式兩者皆不寫）。
        /// <paramref name="freezeWindow"/>＝凍結時界：已排且在 now＋時界內開工的派工保留原機台與時段
        /// （自動重排依廠別參數傳入；null 或 0＝不使用）。
        /// </summary>
        Task<ScheduleRunResult> RunAsync(
            string plantId, ScheduleRule rule, DateTime today,
            string? runBy = null, ScheduleTrigger trigger = ScheduleTrigger.Manual,
            CancellationToken ct = default, ScheduleRunMode mode = ScheduleRunMode.Full,
            TimeSpan? freezeWindow = null);
    }
}
