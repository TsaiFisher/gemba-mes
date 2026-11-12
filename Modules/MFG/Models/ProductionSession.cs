using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>上工紀錄狀態（mfg-domain.md §4.5）</summary>
    public enum ProductionSessionStatus
    {
        Open = 0,    // 已上工（StartAt 有值，EndAt 為空）
        Closed = 1   // 已下工（EndAt 有值）
    }

    /// <summary>
    /// 上工紀錄 — 誰、機台、模具、起訖時間（mfg-domain.md §4.5 / §5.3 / §16.3）。
    /// 一張派工可有多次上工（換班、中斷）。關閉時累計 WorkOrder.ActualLaborCost / ActualOverheadCost。
    /// </summary>
    public class ProductionSession : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string SessionId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        /// <summary>冗餘存工單號，方便成本回寫查詢（避免 JOIN）</summary>
        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public ProductionSessionStatus Status { get; set; } = ProductionSessionStatus.Open;

        public DateTime StartAt { get; set; }

        public DateTime? EndAt { get; set; }

        /// <summary>操作員 ID</summary>
        [MaxLength(50)]
        public string? OperatorId { get; set; }

        /// <summary>使用機台代碼（委外時為 null）</summary>
        [MaxLength(20)]
        public string? MachineCode { get; set; }

        /// <summary>使用模具代碼（可選）</summary>
        [MaxLength(30)]
        public string? ToolingCode { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Dispatch? Dispatch { get; set; }
        public ICollection<ProductionReport> Reports { get; set; } = new List<ProductionReport>();
    }
}
