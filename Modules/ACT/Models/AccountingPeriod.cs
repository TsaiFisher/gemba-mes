using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ACT.Models
{
    public enum PeriodStatus
    {
        Open      = 0,  // 可過帳
        Closing   = 1,  // 月結中（僅允許調整分錄）
        Closed    = 2,  // 已關帳
        Reopened  = 3   // 重開帳（需雙重核准）
    }

    /// <summary>
    /// 會計期間（Phase C，§4.1）。**依廠別分開**：主鍵是 (PeriodId, PlantId)，
    /// 見 <c>ApplicationDbContextExtensions</c>。
    ///
    /// 原本是全域單列，一個廠關帳等於把全公司的帳都關掉——甲廠走完月結清單，
    /// 乙廠就再也過不了帳，而乙廠自己的清單上一步都沒動。月結清單
    /// （<see cref="MonthEndChecklist"/>）本來就是每廠一份，期間卻是全域，
    /// 兩者對不起來。
    ///
    /// ⚠️ 服務層一律用 <c>IgnoreQueryFilters()</c> 加明確的 plantId 比對，不靠全域過濾器：
    /// 過帳要檢查的是**單據所屬廠別**，不是操作者當下切在哪個廠——跨廠調撥收貨時
    /// 兩者本來就不同（依 2026-08-17 裁決，跨廠以入庫端為準）。
    /// </summary>
    public class AccountingPeriod : IPlantEntity
    {
        /// <summary>格式：YYYYMM，如 "202601"。與 PlantId 合為複合主鍵。</summary>
        [MaxLength(6)]
        public string PeriodId { get; set; } = string.Empty;

        /// <summary>所屬廠別。複合主鍵的一部分，故不可為 null。</summary>
        [MaxLength(20)]
        public string? PlantId { get; set; }

        public int Year  { get; set; }
        public int Month { get; set; }

        public DateOnly StartDate { get; set; }
        public DateOnly EndDate   { get; set; }

        public PeriodStatus Status { get; set; } = PeriodStatus.Open;

        public DateTime? ClosedAt  { get; set; }
        [MaxLength(50)]
        public string? ClosedBy   { get; set; }

        public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;

        [MaxLength(500)]
        public string? Remarks { get; set; }
    }
}
