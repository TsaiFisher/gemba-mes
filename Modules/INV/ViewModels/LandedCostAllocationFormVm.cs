using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;

namespace SmartFactory.MES.Modules.INV.ViewModels
{
    /// <summary>採購分攤費用（Landed Cost）分攤單表單 VM（規格 §3.4）。</summary>
    public class LandedCostAllocationFormVm
    {
        public string? LandedCostAllocationId { get; set; }

        [Required]
        public DateOnly DocDate { get; set; }

        [Required]
        public LandedCostBasis Basis { get; set; } = LandedCostBasis.Amount;

        [MaxLength(20)]
        public string? CostType { get; set; }

        public decimal TotalLandedCost { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public DocStatus Status { get; set; } = DocStatus.Draft;
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public string? ApprovalRequestId { get; set; }

        /// <summary>樂觀並發 token（對應 entity.xmin）。</summary>
        public uint RowVersion { get; set; }

        public List<LandedCostAllocationLineVm> Lines { get; set; } = new();
    }

    public class LandedCostAllocationLineVm
    {
        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string GoodsReceiptId { get; set; } = string.Empty;

        public int GoodsReceiptLineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        public decimal ReceiptQty { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal Weight { get; set; }
        public decimal OriginalUnitCost { get; set; }

        // 確認後回寫（唯讀顯示）
        public decimal AllocatedCost { get; set; }
        public decimal CapitalizedCost { get; set; }
        public decimal ExpensedCost { get; set; }
        public decimal NewUnitCost { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }
    }
}
