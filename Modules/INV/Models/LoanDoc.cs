using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 借入借出單主檔。
    /// TypeCode：IB（借入）/ IL（借出）/ BR（借入退回）/ LR（借出退回）
    /// </summary>
    public class LoanDoc : IPlantEntity, IDataAuthEntity, IInventoryDocument, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string LoanDocId { get; set; } = string.Empty;

        public DateOnly LoanDate { get; set; }

        /// <summary>IB / IL / BR / LR</summary>
        [Required, MaxLength(20)]
        public string TypeCode { get; set; } = "IB";

        /// <summary>冗餘大類（Loan），加速過帳引擎查詢</summary>
        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Loan";

        /// <summary>關聯對象類型：Customer / Supplier / Department</summary>
        [Required, MaxLength(20)]
        public string EntityType { get; set; } = "Customer";

        /// <summary>關聯對象代號（客戶/供應商/部門）</summary>
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

        // IPlantEntity
        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// INV 單據表頭補 xmin（見 reference-concurrency-catch-audit）：確定性擋下同單並發過帳雙重扣帳。</summary>
        public uint xmin { get; set; }

        // Navigation
        public ICollection<LoanDocLine> Lines { get; set; } = new List<LoanDocLine>();

        // IInventoryDocument
        string IInventoryDocument.DocNo     => LoanDocId;
        DateOnly IInventoryDocument.DocDate => LoanDate;
        IReadOnlyList<IInventoryDocumentLine> IInventoryDocument.GetLines()
            => Lines.Cast<IInventoryDocumentLine>().ToList();
    }
}
