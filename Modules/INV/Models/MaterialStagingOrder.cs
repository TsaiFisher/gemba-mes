using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>備料單主檔（舊 IM）— inventory.md §11</summary>
    public class MaterialStagingOrder : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string StagingId { get; set; } = string.Empty;

        public string? PlantId { get; set; }

        /// <summary>來源類型：WorkOrder | Dispatch | ProductionBatch | SalesOrder | Manual</summary>
        [MaxLength(30)]
        public string SourceType { get; set; } = "Manual";

        /// <summary>來源單號（WorkOrderId / DispatchId / ProductionBatchId / SalesOrderId）</summary>
        [MaxLength(40)]
        public string? SourceId { get; set; }

        /// <summary>備料目標倉庫</summary>
        [MaxLength(20)]
        public string? StagingWhCode { get; set; }

        /// <summary>備料目標儲位</summary>
        [MaxLength(20)]
        public string? StagingLocationCode { get; set; }

        /// <summary>Draft | Confirmed | Picking | Completed | Void</summary>
        [MaxLength(20)]
        public string Status { get; set; } = "Draft";

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [MaxLength(450)]
        public string? ApprovalRequestId { get; set; }

        [MaxLength(450)]
        public string? ConfirmedBy { get; set; }

        public DateTime? ConfirmedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary>揀料移入備料倉（Staged）的執行人；Status → Completed 時填入。</summary>
        [MaxLength(450)]
        public string? StagedBy { get; set; }

        /// <summary>揀料移入備料倉的時間（實際庫存移轉過帳時點）。</summary>
        public DateTime? StagedAt { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ICollection<MaterialStagingLine> Lines { get; set; } = new List<MaterialStagingLine>();
    }

    /// <summary>備料明細（舊 IM_D）</summary>
    public class MaterialStagingLine
    {
        [Required, MaxLength(30)]
        public string StagingId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal RequiredQty { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal PickedQty { get; set; }

        /// <summary>對應 InventoryReservation.Id（確認時建立）</summary>
        public int? ReservationId { get; set; }

        /// <summary>
        /// 本行料件是為哪張工單備的。生產批來源的備料單一張可橫跨多張工單，
        /// 預留轉移與領料消耗都必須逐行認工單，只看表頭會把整批鎖到同一張工單上。
        /// </summary>
        [MaxLength(40)]
        public string? SourceWorkOrderId { get; set; }

        /// <summary>本行料件對應的派工單（派工層級備料時填入，供備料畫面回頭對應用料需求）。</summary>
        [MaxLength(40)]
        public string? SourceDispatchId { get; set; }

        /// <summary>揀料來源倉庫（從哪個倉搬到備料倉；未填則由揀料時指定）。</summary>
        [MaxLength(20)]
        public string? FromWarehouseCode { get; set; }

        /// <summary>揀料來源儲位。</summary>
        [MaxLength(20)]
        public string? FromLocationCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [NotMapped]
        public decimal RemainingQty => RequiredQty - PickedQty;

        public MaterialStagingOrder? StagingOrder { get; set; }
        public InventoryReservation? Reservation { get; set; }
    }
}
