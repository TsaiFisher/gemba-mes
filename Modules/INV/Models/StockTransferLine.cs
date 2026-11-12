using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.INV.Models
{
    public class StockTransferLine
    {
        [Required, MaxLength(30)]
        public string StockTransferId { get; set; } = string.Empty;

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

        /// <summary>來源倉庫（覆蓋 Header 的 FromWhCode）</summary>
        [MaxLength(20)]
        public string? FromWhCode { get; set; }

        /// <summary>來源儲位</summary>
        [MaxLength(20)]
        public string? FromLocationCode { get; set; }

        /// <summary>目的倉庫（覆蓋 Header 的 ToWhCode）</summary>
        [MaxLength(20)]
        public string? ToWhCode { get; set; }

        /// <summary>目的儲位</summary>
        [MaxLength(20)]
        public string? ToLocationCode { get; set; }

        /// <summary>批號（來源與目的相同）；空白＝不追蹤批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>撥入批號；null＝與撥出批號相同。換批號用於既有庫存裝箱／換箱（container-tracking.md §9.4）</summary>
        [MaxLength(50)]
        public string? ToLotNo { get; set; }

        /// <summary>已過帳數量（WP + WI 同步完成後設為 Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [MaxLength(30)]
        public string? SourceDocId { get; set; }
        public int? SourceLineNo { get; set; }

        /// <summary>已沖銷數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtySourceCleared { get; set; }

        [ForeignKey(nameof(StockTransferId))]
        public StockTransfer? StockTransfer { get; set; }
    }
}
