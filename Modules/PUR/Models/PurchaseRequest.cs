using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>請購單主檔（對應舊 PR 表）</summary>
    public class PurchaseRequest : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }
        /// <summary>請購單號，由 DocNumberService 產生（格式：PR{yyyy}{00000}）</summary>
        [Key, MaxLength(30)]
        public string PurchaseRequestId { get; set; } = string.Empty;

        /// <summary>請購日期</summary>
        public DateOnly RequestDate { get; set; }

        /// <summary>請購人（登入使用者 UserId）</summary>
        [Required, MaxLength(450)]
        public string RequestedBy { get; set; } = string.Empty;

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>來源 MRP 執行單號（由 MRP 採購需求自動建立時填入；手動建立為 null，用於重跑去重比對）</summary>
        [MaxLength(30)]
        public string? SourceMrpRunId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        // Navigation
        public ICollection<PurchaseRequestLine> Lines { get; set; } = new List<PurchaseRequestLine>();
    }
}
