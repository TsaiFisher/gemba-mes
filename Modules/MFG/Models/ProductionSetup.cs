using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>Setup 狀態</summary>
    public enum ProductionSetupStatus
    {
        Open = 0,    // 準備中（SetupStartAt 有值，SetupEndAt 為空）
        Closed = 1   // 準備完成（SetupEndAt 有值）
    }

    /// <summary>
    /// 開工準備記錄 — 換模、換線等準備作業（mfg-domain.md §4.4 / §16.3）。
    /// 一張派工可有一筆 ProductionSetup（TM_SETUP > 0 時），
    /// 關閉時可選擇累計 ActualOverheadCost（§16.3）。
    /// </summary>
    public class ProductionSetup : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string SetupId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public ProductionSetupStatus Status { get; set; } = ProductionSetupStatus.Open;

        public DateTime SetupStartAt { get; set; }

        public DateTime? SetupEndAt { get; set; }

        /// <summary>操作員 ID（誰執行 Setup）</summary>
        [MaxLength(50)]
        public string? OperatorId { get; set; }

        /// <summary>使用模具代碼（可選）</summary>
        [MaxLength(30)]
        public string? ToolingCode { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Dispatch? Dispatch { get; set; }
    }
}
