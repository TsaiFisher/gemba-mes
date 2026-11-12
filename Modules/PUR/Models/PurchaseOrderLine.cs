using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.PUR.Models
{
    /// <summary>採購單明細（對應舊 PO_D 表）</summary>
    public class PurchaseOrderLine
    {
        [Required, MaxLength(30)]
        public string PurchaseOrderId { get; set; } = string.Empty;

        /// <summary>明細序號（1, 2, 3...）</summary>
        public int LineNo { get; set; }

        /// <summary>來源請購單號（可為空 = 直接建採購）</summary>
        [MaxLength(30)]
        public string? PurchaseRequestId { get; set; }

        /// <summary>來源請購明細序號</summary>
        public int? PrLineNo { get; set; }

        /// <summary>品號 → FK to Product</summary>
        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>預交日期</summary>
        public DateOnly? DeliveryDate { get; set; }

        /// <summary>採購數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal OrderQty { get; set; }

        /// <summary>採購單位 → FK to Uom</summary>
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

        /// <summary>稅率（自動帶入，可覆寫）</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        /// <summary>未稅金額（採購幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        /// <summary>稅額（採購幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>含稅總額（採購幣，未換算本幣）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣含稅總額（TotalAmount × ExchangeRate）。混幣單據的表頭合計一律加總本欄。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>備註</summary>
        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>已結案</summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// 已進貨數量，單位為本行的 <see cref="UomCode"/>。
        /// 進貨行單位可與採購行不同，故累加前先換算回本行單位（同 SalesOrderLine.ShippedQty）。
        /// 超量守門不看這一欄，看 <see cref="ReceivedStockQty"/>。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceivedQty { get; set; }

        /// <summary>
        /// 已進貨數量的庫存基準單位累計（對應 <see cref="StockQty"/>／<see cref="StockUomCode"/>）。
        /// 超量守門的權威值，直接加進貨行的 <c>StockQty</c>，不經換算故無捨入漂移。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReceivedStockQty { get; set; }

        // Navigation
        [ForeignKey(nameof(PurchaseOrderId))]
        public PurchaseOrder? PurchaseOrder { get; set; }
    }
}
