using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 庫存調整單主檔。
    /// TypeCode：IA（庫存調整，每行可獨立指定正調整 +1 / 負調整 -1）
    /// </summary>
    public class StockAdjustment : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string StockAdjustmentId { get; set; } = string.Empty;

        public DateOnly AdjustDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "IA";

        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Adjustment";

        /// <summary>責任部門代號</summary>
        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        /// <summary>調整原因代號（建議對應 CodeEntry category = PZ.AdjustReason）</summary>
        [MaxLength(20)]
        public string? AdjustReasonCode { get; set; }

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

        public ICollection<StockAdjustmentLine> Lines { get; set; } = new List<StockAdjustmentLine>();

        string IInventoryDocument.DocNo     => StockAdjustmentId;
        DateOnly IInventoryDocument.DocDate => AdjustDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
