using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    public class LoanDocLine : IInventoryDocumentLine
    {
        [Required, MaxLength(30)]
        public string LoanDocId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal? UnitPrice { get; set; }

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        /// <summary>倉庫已處理量（= Σ LocationDetails.Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [MaxLength(30)]
        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }

        /// <summary>已沖銷/還出數量 (用於來源單據追蹤餘量)</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtySourceCleared { get; set; }

        // Navigation
        [ForeignKey(nameof(LoanDocId))]
        public LoanDoc? LoanDoc { get; set; }

        public ICollection<LoanDocLocationDetail> LocationDetails { get; set; } = new List<LoanDocLocationDetail>();
    }
}
