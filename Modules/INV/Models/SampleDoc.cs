using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 樣品/客供單主檔。
    /// TypeCode：CI（客供進貨）/ CR（客供退回）/ SO（樣品出貨）/ SR（樣品退回）
    /// </summary>
    public class SampleDoc : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string SampleDocId { get; set; } = string.Empty;

        public DateOnly SampleDate { get; set; }

        /// <summary>CI / CR / SO / SR</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "CI";

        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Sample";

        /// <summary>關聯對象類型：Customer / Supplier</summary>
        [Required, MaxLength(20)]
        public string EntityType { get; set; } = "Customer";

        [Required, MaxLength(100)]
        public string EntityCode { get; set; } = string.Empty;

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

        public ICollection<SampleDocLine> Lines { get; set; } = new List<SampleDocLine>();

        string IInventoryDocument.DocNo     => SampleDocId;
        DateOnly IInventoryDocument.DocDate => SampleDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
