using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 同廠調撥單主檔。
    /// 確認時同時產生 WP（撥出）和 WI（撥入）兩筆 StockMovement。
    /// </summary>
    public class StockTransfer : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string StockTransferId { get; set; } = string.Empty;

        public DateOnly TransferDate { get; set; }

        /// <summary>固定為 WT（調撥），確認時分別產生 WP / WI 兩筆 StockMovement</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "WT";

        /// <summary>來源倉庫</summary>
        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        /// <summary>目的倉庫</summary>
        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [MaxLength(100)]
        public string? ApprovalRequestId { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// INV 單據表頭補 xmin（見 reference-concurrency-catch-audit）：確定性擋下同單並發過帳雙重扣帳。</summary>
        public uint xmin { get; set; }

        public ICollection<StockTransferLine> Lines { get; set; } = new List<StockTransferLine>();
    }
}
