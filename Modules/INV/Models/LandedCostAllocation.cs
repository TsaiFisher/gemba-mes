using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Services;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>
    /// 採購分攤費用（Landed Cost）分攤單主檔（規格 §3.4）。
    /// 將國際運費／關稅／保險等外包費用，依金額/數量/重量比例分攤至選定的進貨明細，
    /// Confirm 時依策略 (a)「只調在庫餘量均價」回寫存貨成本，已出庫對應部分轉費用（PPV）。
    /// </summary>
    public class LandedCostAllocation : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        /// <summary>分攤單號（格式：LCA-{yyMM}-{00000}）</summary>
        [Key, MaxLength(30)]
        public string LandedCostAllocationId { get; set; } = string.Empty;

        /// <summary>單據日期</summary>
        public DateOnly DocDate { get; set; }

        /// <summary>分攤基準（金額／數量／重量）</summary>
        public LandedCostBasis Basis { get; set; } = LandedCostBasis.Amount;

        /// <summary>費用類別（運費/關稅/保險…；對應 CodeEntry category="landed_cost_type"，可空）</summary>
        [MaxLength(20)]
        public string? CostType { get; set; }

        /// <summary>待分攤外包費用總額（與明細同幣別）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalLandedCost { get; set; }

        /// <summary>幣別 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>單據狀態</summary>
        public DocStatus Status { get; set; } = DocStatus.Draft;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IPlantEntity
        public string? PlantId { get; set; }

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken）</summary>
        public uint xmin { get; set; }

        public ICollection<LandedCostAllocationLine> Lines { get; set; } = new List<LandedCostAllocationLine>();
    }
}
