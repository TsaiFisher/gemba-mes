using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 重工發退料單主檔。
    /// TypeCode：RI（重工發料）、RR（重工退料）
    /// </summary>
    public class ReworkMaterialDoc : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string ReworkMaterialDocId { get; set; } = string.Empty;

        public DateOnly IssueDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "RI";

        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Rework";

        /// <summary>工作中心代號</summary>
        [Required, MaxLength(30)]
        public string WorkCenterCode { get; set; } = string.Empty;

        /// <summary>重工工單號（選填，未來與 MFG 整合）</summary>
        [MaxLength(30)]
        public string? WorkOrderCode { get; set; }

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

        public ICollection<ReworkMaterialDocLine> Lines { get; set; } = new List<ReworkMaterialDocLine>();

        string IInventoryDocument.DocNo     => ReworkMaterialDocId;
        DateOnly IInventoryDocument.DocDate => IssueDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
