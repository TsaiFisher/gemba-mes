using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 跨廠調撥單主檔。
    /// 確認發出：XP (direction=-1) 在來源廠過帳，Status → InTransit。
    /// 目的廠收貨：XI (direction=+1) 在目的廠過帳，Status → Posted。
    /// </summary>
    public class CrossPlantTransfer : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        [Key, MaxLength(30)]
        public string CrossPlantTransferId { get; set; } = string.Empty;

        public DateOnly TransferDate { get; set; }

        /// <summary>來源廠倉庫（預設）</summary>
        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        /// <summary>目的廠倉庫（預設）</summary>
        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        /// <summary>來源廠 PlantId（實作 IPlantEntity，受全域 Query Filter 保護）</summary>
        public string? PlantId { get; set; }

        /// <summary>目的廠 PlantId</summary>
        [MaxLength(30)]
        public string? ToPlantId { get; set; }

        /// <summary>Draft → InTransit（發出）→ Posted（收貨）→ Void</summary>
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

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// INV 單據表頭補 xmin（見 reference-concurrency-catch-audit）：確定性擋下同單並發過帳雙重扣帳。</summary>
        public uint xmin { get; set; }

        public ICollection<CrossPlantTransferLine> Lines { get; set; } = new List<CrossPlantTransferLine>();
    }
}
