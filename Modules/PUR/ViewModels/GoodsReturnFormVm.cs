using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.PUR.ViewModels
{
    /// <summary>進貨退出 Side Panel 表單 ViewModel</summary>
    public class GoodsReturnFormVm
    {
        public string? GoodsReturnId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public DocStatus Status { get; set; } = DocStatus.Draft;

        [Required]
        public DateOnly ReturnDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<GoodsReturnLineVm> Lines { get; set; } = new();
    }

    public class GoodsReturnLineVm
    {
        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? GoodsReceiptId { get; set; }

        public int? ReceiptLineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue)]
        public decimal ReturnQty { get; set; }

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

        [MaxLength(20)]
        public string? WhCode { get; set; }

        [MaxLength(20)]
        public string? LocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [MaxLength(20)]
        public string? TypeCode { get; set; }

        /// <summary>倉庫已處理量（= Σ LocationDetails.Qty），檢視用。</summary>
        public decimal QtyPutAway { get; set; }
    }
}
