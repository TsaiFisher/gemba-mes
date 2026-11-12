using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>生產批狀態（mfg-domain.md §14.3，由子 WorkOrder 狀態衍生）</summary>
    public enum ProductionBatchStatus
    {
        Planned = 0,
        InProgress = 1,
        Completed = 2,
        Void = 3
    }

    /// <summary>
    /// 生產批 — 一個生產批對應一筆 MrpRunLine，下含多張 WorkOrder（成品 + 各階半成品）。
    /// 承擔批次層級狀態查詢、優先序管理、預計完工日（mfg-domain.md §14.3）。
    /// </summary>
    public class ProductionBatch : IPlantEntity, IDataAuthEntity
    {
        /// <summary>BatchId（= 原 ID_PLN）</summary>
        [Key, MaxLength(30)]
        public string BatchId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string MrpRunId { get; set; } = string.Empty;

        public int MrpRunLineSeq { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        /// <summary>優先序 CN_P（可調整，插單往前排）</summary>
        public int Priority { get; set; } = 100;

        /// <summary>MRP 倒推的最晚須完成日</summary>
        public DateTime PlannedEndDate { get; set; }

        /// <summary>排程後動態 ETA（定期更新）</summary>
        public DateTime? ExpectedEndDate { get; set; }

        public ProductionBatchStatus Status { get; set; } = ProductionBatchStatus.Planned;

        /// <summary>倒推防呆：DT_START &lt; TODAY 轉正推後仍超期時標記（mfg-domain.md §5.1）</summary>
        public bool IsPredictedLate { get; set; } = false;

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<WorkOrder> WorkOrders { get; set; } = new List<WorkOrder>();
    }
}
