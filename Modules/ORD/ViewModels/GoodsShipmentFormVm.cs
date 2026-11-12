using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.ORD.ViewModels
{
    public class GoodsShipmentFormVm
    {
        public string? GoodsShipmentId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public DocStatus Status { get; set; } = DocStatus.Draft;

        [Required]
        public DateOnly ShipDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        [MaxLength(30)]
        public string? SalesOrderId { get; set; }

        [MaxLength(300)]
        public string? ShipAddress { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<GoodsShipmentLineVm> Lines { get; set; } = new();
    }

    public class GoodsShipmentLineVm
    {
        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? SalesOrderId { get; set; }

        public int? SoLineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue)]
        public decimal ShipQty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        public decimal StockQty { get; set; }

        [MaxLength(20)]
        public string? StockUomCode { get; set; }

        public decimal UnitPrice { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        public decimal ExchangeRate { get; set; } = 1;

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        public decimal TaxRate { get; set; }
        public decimal LineAmount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        /// <summary>本位幣含稅總額（顯示用；儲存時由後端依權威匯率重算，不採信前端值）</summary>
        public decimal AmountLocal { get; set; }

        public decimal QtyPutAway { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }
    }
}
