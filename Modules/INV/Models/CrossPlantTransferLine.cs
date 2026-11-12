using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    public class CrossPlantTransferLine
    {
        [Required, MaxLength(30)]
        public string CrossPlantTransferId { get; set; } = string.Empty;

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

        /// <summary>來源倉庫（覆蓋 Header 的 FromWhCode）</summary>
        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        /// <summary>來源儲位（來源廠）</summary>
        [MaxLength(20)]
        public string? FromLocationCode { get; set; }

        /// <summary>目的倉庫（覆蓋 Header 的 ToWhCode）</summary>
        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        /// <summary>目的儲位（目的廠）</summary>
        [MaxLength(20)]
        public string? ToLocationCode { get; set; }

        /// <summary>批號（兩廠相同，跨廠不換批號）；null＝不追蹤批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>已發出數量（XP 過帳後設為 StockQty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyShipped { get; set; }

        /// <summary>已收貨數量（XI 過帳後設為 StockQty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyReceived { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [ForeignKey(nameof(CrossPlantTransferId))]
        public CrossPlantTransfer? CrossPlantTransfer { get; set; }
    }
}
