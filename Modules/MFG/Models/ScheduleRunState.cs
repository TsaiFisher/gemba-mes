using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 各廠最近一次排程的紀錄（每廠一列）。排程頁據此顯示「上次排程時間」，
    /// 搭配待排／暫停筆數讓使用者看得出排程是否已過時。
    ///
    /// 刻意不實作 IPlantEntity：之後由背景服務自動排程時沒有登入使用者（CurrentPlantId 為 null），
    /// 全域廠別過濾會讓查詢靜默撈到 0 筆。一律以明確的 PlantId 讀寫。
    /// </summary>
    public class ScheduleRunState
    {
        [Key, MaxLength(20)]
        public string PlantId { get; set; } = string.Empty;

        /// <summary>最近一次排程完成時刻（UTC）。</summary>
        public DateTime LastRunAt { get; set; }

        /// <summary>執行者（使用者 ID）；背景自動排程為 null。</summary>
        [MaxLength(50)]
        public string? LastRunBy { get; set; }

        public int LastScheduled { get; set; }
        public int LastSkipped { get; set; }

        /// <summary>
        /// 這次排程讀取資料的起點（UTC，＝開始執行的時刻）。「排程是否已過時」以此為基準：
        /// 之後才發生的異動（含排程執行當中發生的）都算新異動。用完成時刻當基準會漏掉執行期間的異動。
        /// </summary>
        public DateTime DataAsOf { get; set; }

        /// <summary>觸發方式：Manual／OnChange／BeforeDispatch／Daily（<see cref="Services.ScheduleTrigger"/>）。</summary>
        [MaxLength(20)]
        public string? LastTrigger { get; set; }

        /// <summary>
        /// 排程當下工作日曆與計劃停機的指紋（排程視窗內）。這兩張表沒有異動時間欄位、計劃停機還可能被刪除，
        /// 無法用時間判斷有沒有改過，所以改為「重算指紋與上次不同＝有異動」。
        /// </summary>
        public long CalendarFingerprint { get; set; }

        /// <summary>指紋計算的日期區間（排程視窗）。比對時用同一區間重算，日期往前走不會誤判為異動。</summary>
        public DateOnly? FingerprintFrom { get; set; }
        public DateOnly? FingerprintTo { get; set; }

        /// <summary>每日定時重排最近一次執行的日期（當地日期），避免同一天重跑。</summary>
        public DateOnly? LastDailyRunDate { get; set; }
    }
}
