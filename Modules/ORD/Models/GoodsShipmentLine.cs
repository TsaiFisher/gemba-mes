using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.ORD.Models
{
    public class GoodsShipmentLine
    {
        [MaxLength(30)]
        public string GoodsShipmentId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? SalesOrderId { get; set; }

        public int? SoLineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,4)")]
        public decimal ShipQty { get; set; }

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

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>已沖銷到來源訂單的數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtySourceCleared { get; set; }

        /// <summary>已退回數量</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReturnedQty { get; set; }

        /// <summary>倉庫作業已下架數量（= Σ LocationDetails.Qty）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public GoodsShipment? GoodsShipment { get; set; }

        public ICollection<GoodsShipmentLocationDetail> LocationDetails { get; set; } = new List<GoodsShipmentLocationDetail>();
    }
}
