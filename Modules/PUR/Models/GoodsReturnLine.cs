using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>進貨退出明細（對應舊 PF_D 表）</summary>
    public class GoodsReturnLine
    {
        [Required, MaxLength(30)]
        public string GoodsReturnId { get; set; } = string.Empty;

        /// <summary>明細序號（1, 2, 3...）</summary>
        public int LineNo { get; set; }

        /// <summary>來源進貨單號</summary>
        [MaxLength(30)]
        public string? GoodsReceiptId { get; set; }

        /// <summary>來源進貨明細序號</summary>
        public int? ReceiptLineNo { get; set; }

        /// <summary>品號 → FK to Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>退貨數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReturnQty { get; set; }

        /// <summary>退貨單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? UomCode { get; set; }

        /// <summary>庫存換算數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        /// <summary>庫存單位 → FK to Uom</summary>
        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        /// <summary>單價（採購幣別）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        /// <summary>採購幣別 → FK to Currency</summary>
        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>匯率</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        /// <summary>稅別 → FK to Tax</summary>
        [MaxLength(20)]
        public string? TaxCode { get; set; }

        /// <summary>稅率</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>未稅金額</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        /// <summary>稅額</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>含稅總額（採購幣，未換算本幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣含稅總額（TotalAmount × ExchangeRate）。混幣單據的表頭合計一律加總本欄。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>出庫倉庫代號</summary>
        [MaxLength(20)]
        public string? WhCode { get; set; }

        /// <summary>出庫儲位代號</summary>
        [MaxLength(20)]
        public string? LocationCode { get; set; }

        /// <summary>批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>倉庫已處理量（= Σ LocationDetails.Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        // Navigation
        [ForeignKey(nameof(GoodsReturnId))]
        public GoodsReturn? GoodsReturn { get; set; }

        public ICollection<GoodsReturnLocationDetail> LocationDetails { get; set; } = new List<GoodsReturnLocationDetail>();
    }
}
