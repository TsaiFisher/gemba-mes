using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// ETA 增量重算引擎實作（mfg-domain.md §7.5 / §10.12）。
    /// 報工後局部平移本派工及受影響的後續派工，滾動視窗 48h / K≤50。
    /// 使用 WorkingTimeWalker 尊重工作日曆（班別時段與計劃停機），確保 ETA 精度。
    /// </summary>
    /// <remarks>
    /// 所有查詢一律 IgnoreQueryFilters：本服務由 ReportEtaNotifier 在背景 scope 呼叫（報工請求已結束），
    /// 沒有登入使用者時全域過濾是 PlantId == null、CreatedByRoleId == null，原本第一個查詢就撈不到派工
    /// 而直接 return——報工後的 ETA 平移從來沒有生效過。派工以主鍵查、其餘以 plantId 或派工鏈限定範圍。
    /// </remarks>
    public class EtaRecalculator : IEtaRecalculator
    {
        private readonly ApplicationDbContext _db;
        private readonly IWorkCalendarService _calendarService;
        private const int HorizonHours = 48;
        private const int MaxNodes     = 50;

        public EtaRecalculator(ApplicationDbContext db, IWorkCalendarService calendarService)
        {
            _db = db;
            _calendarService = calendarService;
        }

        public async Task RecalculateAsync(
            string dispatchId, string plantId, DateTime now,
            CancellationToken ct = default)
        {
            // 載入本派工 + 對應工序（需工時、批量模式）
            var dispatch = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == dispatchId, ct);
            if (dispatch == null) return;

            // 只處理有排程時段的派工
            if (!dispatch.ScheduledStart.HasValue || !dispatch.ScheduledEnd.HasValue) return;

            var op = await _db.WorkOrderOperations.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId
                                       && o.OperationSeq == dispatch.OperationSeq, ct);
            if (op == null) return;

            // 計算剩餘工時
            var stdMinutes = SchedulingEngine.CalcRequiredMinutes(op, dispatch.Quantity);
            var remainingMin = EtaHelper.CalcRemainingMinutes(stdMinutes, dispatch.QtyDone, dispatch.Quantity);

            // 新估算結束時刻：優先走 WorkingTimeWalker（尊重工作日曆與計劃停機）。
            // 若日曆無時段設定（GetSegments 回傳空值），降級為 now + remainingMin 直接計算。
            var machineCode   = dispatch.MachineCode;
            var workCenterId  = op.WorkCenterId;
            var nowLocal      = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);

            // 快取閉包用的字典（避免 async 委派逐日 await 造成排程效能問題，以同步快取取代）
            var segmentCache = new Dictionary<DateOnly, IReadOnlyList<(int Start, int End)>>();

            IReadOnlyList<(int Start, int End)> GetSegmentsSync(DateOnly date)
            {
                if (segmentCache.TryGetValue(date, out var cached)) return cached;
                // 同步等待（Walker 為純記憶體計算，DB IO 已提前快取）
                var segs = _calendarService.GetSegmentsAsync(machineCode, workCenterId, plantId, date)
                               .GetAwaiter().GetResult();
                segmentCache[date] = segs;
                return segs;
            }

            DateTime newEnd;
            if (remainingMin > 0)
            {
                // 預先暖機快取：預載起始日起 3 天時段，減少 Walker 內逐日 DB IO
                var preloadDate = DateOnly.FromDateTime(nowLocal);
                for (int i = 0; i < 3; i++)
                {
                    var d = preloadDate.AddDays(i);
                    if (!segmentCache.ContainsKey(d))
                    {
                        segmentCache[d] = await _calendarService.GetSegmentsAsync(
                            machineCode, workCenterId, plantId, d);
                    }
                }

                // 判斷是否有有效日曆；若完全無設定則降級
                var firstDaySegs = GetSegmentsSync(preloadDate);
                if (firstDaySegs.Count > 0)
                {
                    newEnd = WorkingTimeWalker.AddWorkingMinutes(
                        nowLocal, remainingMin, GetSegmentsSync, downtimes: null, maxDays: 90);
                    newEnd = DateTime.SpecifyKind(newEnd, DateTimeKind.Utc);
                }
                else
                {
                    // 降級：日曆未設定，直接加分鐘（原始行為）
                    newEnd = now.AddMinutes((double)remainingMin);
                }
            }
            else
            {
                newEnd = now;
            }

            var oldEnd = dispatch.ScheduledEnd.Value;
            var delta  = newEnd - oldEnd;

            // 差異在 1 分鐘內視為無異動，避免無謂 DB 寫入
            if (Math.Abs(delta.TotalMinutes) < 1) return;

            // 收集受影響的後續派工（§10.12 局部策略）
            var affected = await CollectAffectedAsync(dispatch, plantId, now, ct);
            var inWindow = EtaHelper.FilterRollingWindow(affected, now, HorizonHours, MaxNodes);

            // 批次平移（含本派工自身——其 ScheduledEnd 直接設為新 ETA，
            // 否則甘特上「正在做的那條」長度永遠不反映實際進度）
            var allIds = inWindow.Select(d => d.DispatchId).ToList();
            allIds.Add(dispatch.DispatchId);

            var toUpdate = await _db.Dispatches.IgnoreQueryFilters()
                .Where(d => allIds.Contains(d.DispatchId))
                .ToListAsync(ct);

            foreach (var d in toUpdate)
            {
                if (!d.ScheduledStart.HasValue || !d.ScheduledEnd.HasValue) continue;

                if (d.DispatchId == dispatch.DispatchId)
                {
                    // 本派工：起點不動，終點 = 重算後 ETA
                    d.ScheduledEnd = DateTime.SpecifyKind(newEnd, DateTimeKind.Utc);
                }
                else
                {
                    var (ns, ne) = EtaHelper.ShiftSlot(d.ScheduledStart.Value, d.ScheduledEnd.Value, delta);
                    d.ScheduledStart = DateTime.SpecifyKind(ns, DateTimeKind.Utc);
                    d.ScheduledEnd   = DateTime.SpecifyKind(ne, DateTimeKind.Utc);
                }
                d.UpdatedAt = DateTime.UtcNow;
            }

            if (toUpdate.Count > 0)
                await _db.SaveChangesAsync(ct);
        }

        // ── 收集受影響的後續派工 ─────────────────────────────────────────────────

        private async Task<List<Dispatch>> CollectAffectedAsync(
            Dispatch current, string plantId, DateTime now, CancellationToken ct)
        {
            var affected = new HashSet<string>();

            // 1. 同機台後續派工（同廠、Scheduled、未來 48h 視窗）
            if (current.MachineCode != null)
            {
                var sameMachine = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PlantId == plantId
                             && d.MachineCode == current.MachineCode
                             && d.Status == DispatchStatus.Scheduled
                             && d.ScheduledStart > current.ScheduledEnd)
                    .Select(d => d.DispatchId)
                    .ToListAsync(ct);
                foreach (var id in sameMachine) affected.Add(id);
            }

            // 2. 同工單後續工序（依 PrevDispatchId 鏈）
            var woChain = await CollectWoChainAsync(current, ct);
            foreach (var id in woChain) affected.Add(id);

            // 3. 上階工單（ParentWorkOrderId）的 Scheduled 派工
            var wo = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkOrderId == current.WorkOrderId, ct);
            if (wo?.ParentWorkOrderId != null)
            {
                var parentDispatches = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.WorkOrderId == wo.ParentWorkOrderId
                             && d.Status == DispatchStatus.Scheduled
                             && d.ScheduledStart > current.ScheduledEnd)
                    .Select(d => d.DispatchId)
                    .ToListAsync(ct);
                foreach (var id in parentDispatches) affected.Add(id);
            }

            if (affected.Count == 0) return new List<Dispatch>();

            return await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => affected.Contains(d.DispatchId))
                .ToListAsync(ct);
        }

        /// <summary>沿 PrevDispatchId 鏈收集同工單的後續工序派工 ID（最多 20 跳避免無限迴圈）。</summary>
        private async Task<List<string>> CollectWoChainAsync(Dispatch current, CancellationToken ct)
        {
            var result = new List<string>();
            // 找以 current.DispatchId 為 PrevDispatchId 的下一張
            string? prevId = current.DispatchId;
            for (int i = 0; i < 20 && prevId != null; i++)
            {
                var next = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                    .Where(d => d.PrevDispatchId == prevId
                             && d.Status == DispatchStatus.Scheduled)
                    .Select(d => new { d.DispatchId })
                    .FirstOrDefaultAsync(ct);
                if (next == null) break;
                result.Add(next.DispatchId);
                prevId = next.DispatchId;
            }
            return result;
        }

        // 全量 ETA 校準（每晚背景執行，§7.5 層級 3）已移至 EtaCalibrationService。
    }
}
