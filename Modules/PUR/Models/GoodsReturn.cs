using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>進貨退出主檔（對應舊 PF 表）</summary>
    public class GoodsReturn : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        /// <summary>退貨單號，由 DocNumberService 產生（格式：PF{yyyy}{00000}）</summary>
        [Key, MaxLength(30)]
        public string GoodsReturnId { get; set; } = string.Empty;

        /// <summary>退貨日期</summary>
        public DateOnly ReturnDate { get; set; }

        /// <summary>建單人 UserId</summary>
        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>供應商代號 → FK to Company（IsSupplier = true）</summary>
        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>異動類別代碼（預設 PF，委外為 OT）→ FK to InventoryTransactionType</summary>
        [MaxLength(20)]
        public string TypeCode { get; set; } = "PF";

        /// <summary>異動大類（冗餘，提升查詢效率）</summary>
        [MaxLength(20)]
        public string? TypeCategory { get; set; } = "Purchase";

        /// <summary>單據狀態</summary>
        public DocStatus Status { get; set; } = DocStatus.Draft;

        /// <summary>本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

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
        public ICollection<GoodsReturnLine> Lines { get; set; } = new List<GoodsReturnLine>();
    }
}
