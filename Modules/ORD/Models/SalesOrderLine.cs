using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ORD.Models
{
    public class SalesOrderLine
    {
        [MaxLength(30)]
        public string SalesOrderId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal OrderQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitPrice { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        [Column(TypeName = "decimal(18,6)")]
        public decimal ExchangeRate { get; set; } = 1;

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TotalAmount { get; set; }

        /// <summary>本位幣含稅總額（TotalAmount × ExchangeRate）。混幣單據的表頭合計一律加總本欄。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal AmountLocal { get; set; }

        /// <summary>
        /// 已出貨數量，單位為本行的 <see cref="UomCode"/>（由 GoodsShipment 確認時累加）。
        ///
        /// 出貨行的 UomCode 可以與訂單行不同（訂 PCS、出 BOX），故累加前一律先經
        /// 庫存基準單位換算回本行單位再加——否則這一欄是「不同單位的數字相加」，
        /// 而剩餘可出量 <c>OrderQty − ShippedQty</c> 也就跟著失真。
        /// 超量守門不看這一欄，看 <see cref="ShippedStockQty"/>（見該欄註解）。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ShippedQty { get; set; }

        /// <summary>
        /// 已出貨數量的庫存基準單位累計（對應 <see cref="StockQty"/>／<see cref="StockUomCode"/>）。
        ///
        /// 超量守門的權威值。<see cref="ShippedQty"/> 是換算回訂單單位後才累加的，
        /// 每次換算都會依該單位的小數位四捨五入，多次分批出貨的誤差會累積；
        /// 本欄直接加出貨行的 <c>StockQty</c>（同一個基準單位，不再換算），
        /// 因此守門比對的是精確值，不會因為捨入漂移而誤擋或漏擋。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ShippedStockQty { get; set; }

        /// <summary>已退回數量（由 SalesReturn 確認時累加）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReturnedQty { get; set; }

        public bool IsClosed { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public SalesOrder? SalesOrder { get; set; }
    }
}
