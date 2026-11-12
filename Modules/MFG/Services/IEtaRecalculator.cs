using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// ETA 增量重算引擎（mfg-domain.md §7.5 / §10.12）。
    /// 觸發點：報工後（ProductionReport 寫入後）。
    /// 影響範圍（局部策略，預設）：
    ///   1. 本派工剩餘量 → 重算本派工 ScheduledEnd
    ///   2. 同機台後續派工 → 平移
    ///   3. 同工單後續工序派工 → 平移
    ///   4. 上階工單派工 → 平移
    /// 滾動視窗：未來 48 小時內（可系統參數調整）、節點數 K ≤ 50。
    /// </summary>
    public interface IEtaRecalculator
    {
        /// <summary>
        /// 報工後觸發局部 ETA 重算。
        /// <paramref name="dispatchId"/> 為本次報工的派工，
        /// <paramref name="now"/> 為重算基準時刻：**廠別當地時間**（與 ScheduledStart/End 同口徑、Kind=Utc 只是標籤），不是真 UTC。
        /// </summary>
        Task RecalculateAsync(
            string dispatchId,
            string plantId,
            DateTime now,
            CancellationToken ct = default);
    }

    /// <summary>ETA 重算的靜態輔助工具（純函式，供單元測試直接呼叫）。</summary>
    public static class EtaHelper
    {
        /// <summary>
        /// 從影響列表中篩選滾動視窗內的派工（mfg-domain.md §10.12）。
        /// 只保留 ScheduledStart ≤ horizon（48 小時後）且不超過 K 個節點。
        /// </summary>
        public static IReadOnlyList<Dispatch> FilterRollingWindow(
            IEnumerable<Dispatch> candidates,
            DateTime now,
            int horizonHours = 48,
            int maxNodes = 50)
        {
            var horizon = now.AddHours(horizonHours);
            return candidates
                .Where(d => d.ScheduledStart.HasValue
                         && d.ScheduledStart.Value >= now          // 只取未來排程
                         && d.ScheduledStart.Value <= horizon)     // 在滾動視窗內
                .OrderBy(d => d.ScheduledStart)
                .Take(maxNodes)
                .ToList();
        }

        /// <summary>
        /// 計算本派工剩餘工時（分鐘）。
        /// 依已完工比例縮減標準工時：remainingMin = stdMin × (remaining / total)。
        /// </summary>
        public static decimal CalcRemainingMinutes(
            decimal stdRequiredMinutes,
            decimal quantityDone,
            decimal quantityTotal)
        {
            if (quantityTotal <= 0) return 0;
            var remaining = quantityTotal - quantityDone;
            if (remaining <= 0) return 0;
            // 先乘後除：先算比例會在除不盡時留下 decimal 尾差（180 × (120/180) = 120.000…006），
            // 排程以整分鐘為粒度時會整整差一分鐘。
            return stdRequiredMinutes * remaining / quantityTotal;
        }

        /// <summary>
        /// 計算派工時段平移後的新時段。
        /// </summary>
        public static (DateTime NewStart, DateTime NewEnd) ShiftSlot(
            DateTime oldStart, DateTime oldEnd, TimeSpan delta)
        {
            return (oldStart + delta, oldEnd + delta);
        }
    }
}
