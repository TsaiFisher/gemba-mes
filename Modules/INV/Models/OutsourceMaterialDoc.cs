using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 委外發退料單主檔。
    /// TypeCode：OO（委外發料）、OR（委外退料）
    /// </summary>
    public class OutsourceMaterialDoc : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string OutsourceMaterialDocId { get; set; } = string.Empty;

        public DateOnly IssueDate { get; set; }

        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "OO";

        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Outsource";

        /// <summary>供應商代號</summary>
        [Required, MaxLength(30)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>委外工單號（選填，未來與 MFG 整合）</summary>
        [MaxLength(30)]
        public string? WorkOrderCode { get; set; }

        /// <summary>發退料對應的委外採購單（FK→OutsourcePurchaseOrder，pur-domain.md §4.3）</summary>
        [MaxLength(30)]
        public string? OutsourcePurchaseOrderId { get; set; }

        /// <summary>OO 發料對應的來源委外派工（FK→Dispatch，pur-domain.md §4.3）</summary>
        [MaxLength(40)]
        public string? DispatchId { get; set; }

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

        public ICollection<OutsourceMaterialDocLine> Lines { get; set; } = new List<OutsourceMaterialDocLine>();

        string IInventoryDocument.DocNo     => OutsourceMaterialDocId;
        DateOnly IInventoryDocument.DocDate => IssueDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
