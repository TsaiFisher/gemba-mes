using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>產品主檔（PROD）</summary>
    public class Product
    {
        [Key, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Required, MaxLength(200)]
        public string ProductName { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Spec { get; set; }

        /// <summary>生產途程代號（日後串接途程模組，暫以文字輸入）</summary>
        [MaxLength(30)]
        public string? RoutingId { get; set; }

        /// <summary>銷售單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? SalesUom { get; set; }

        /// <summary>採購單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? PurchaseUom { get; set; }

        /// <summary>庫存單位 → FK to Uom（建檔必填：為庫存/成本/換算的基準單位）。
        /// DB 欄位維持可為 null 以相容既有資料（見 ApplicationDbContextExtensions 的 IsRequired(false)）。</summary>
        [Required, MaxLength(20)]
        public string? StockUom { get; set; }

        /// <summary>庫存模式 → CodeEntry: stock_mode</summary>
        [MaxLength(20)]
        public string? StockMode { get; set; }

        /// <summary>安全存量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal SafetyStock { get; set; }

        /// <summary>標準單價</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardCost { get; set; }

        /// <summary>產銷模式 → CodeEntry: mrp_mode</summary>
        [MaxLength(20)]
        public string? MrpMode { get; set; }

        /// <summary>一般前置期（天）</summary>
        public int LeadTimeDays { get; set; }

        /// <summary>緊急前置期（天）</summary>
        public int UrgentLeadTimeDays { get; set; }

        /// <summary>委外量（件/天）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal OutsourceQtyPerDay { get; set; }

        /// <summary>有效期限（天）</summary>
        public int? ShelfLifeDays { get; set; }

        /// <summary>淨重</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? NetWeight { get; set; }

        /// <summary>毛重</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? GrossWeight { get; set; }

        /// <summary>單箱數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? BoxQty { get; set; }

        /// <summary>是否管控批號</summary>
        public bool IsLotControlled { get; set; }

        /// <summary>計數值驗證水準</summary>
        [MaxLength(20)]
        public string? InspectionLevelN { get; set; }

        /// <summary>計量值驗證水準</summary>
        [MaxLength(20)]
        public string? InspectionLevelR { get; set; }

        /// <summary>連續性驗證水準</summary>
        [MaxLength(20)]
        public string? InspectionLevelC { get; set; }

        /// <summary>
        /// 免驗（IQC）直放：進貨免進待驗倉，收貨上架可直接進正常倉、當下即認列應付。
        /// 未勾（預設）者一律先上待驗倉，待 IQC 合格放行後才進正常倉並認列。
        /// </summary>
        public bool IqcExempt { get; set; } = false;

        [MaxLength(1000)]
        public string? Remarks { get; set; }

        /// <summary>產品類別 → FK to ProductCategory（決定庫存管理預設值）</summary>
        [MaxLength(20)]
        public string? CategoryCode { get; set; }

        public bool IsActive { get; set; } = true;

        /// <summary>最小批量（MinMultiple 政策用）；0 或 null 代表不限（等同 1）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? MinLotSize { get; set; }

        /// <summary>批量倍數（MinMultiple 政策用）；0 或 null 代表不限（等同 1）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal? LotMultiple { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Approved;
        public string? ApprovalRequestId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        public ProductCategory? Category { get; set; }
        public ICollection<ProductCategoryTag> Tags { get; set; } = new List<ProductCategoryTag>();
    }
}
