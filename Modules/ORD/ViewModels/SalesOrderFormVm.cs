using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.ORD.ViewModels
{
    public class SalesOrderFormVm
    {
        public string? SalesOrderId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        [Required]
        public DateOnly OrderDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? OrderType { get; set; }

        /// <summary>貿易條件（Incoterms，CodeEntry: <c>trade_term</c>）。語意見對應 entity 的文件。</summary>
        public string? TradeTermCode { get; set; }

        [Required, MaxLength(20)]
        public string CompanyCode { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? DepartmentCode { get; set; }

        [MaxLength(50)]
        public string? CustomerOrderNo { get; set; }

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<SalesOrderLineVm> Lines { get; set; } = new();
    }

    public class SalesOrderLineVm
    {
        public int LineNo { get; set; }

        [Required, MaxLength(50)]
        public string ProductCode { get; set; } = string.Empty;

        public DateOnly? DeliveryDate { get; set; }

        [Range(0.0001, double.MaxValue)]
        public decimal OrderQty { get; set; }

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
        public bool IsClosed { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public decimal ShippedQty { get; set; }
        public decimal ReturnedQty { get; set; }
    }
}
