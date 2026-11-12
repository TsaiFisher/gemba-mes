using System.ComponentModel.DataAnnotations;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.PUR.ViewModels
{
    /// <summary>進貨單 Side Panel 表單 ViewModel</summary>
    public class GoodsReceiptFormVm
    {
        public string? GoodsReceiptId { get; set; }
        /// <summary>xmin 樂觀並發 token（對應 Entity.xmin；見 ord-module.md §13）</summary>
        public uint RowVersion { get; set; }
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;
        public DocStatus Status { get; set; } = DocStatus.Draft;

        [Required]
        public DateOnly ReceiptDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        /// <summary>Phase G: 委外進貨來源單據類型（如 "OPO"）</summary>
        [MaxLength(30)]
        public string? SourceDocType { get; set; }

        /// <summary>Phase G: 委外進貨來源單號（OutsourcePurchaseOrderId）</summary>
        [MaxLength(40)]
        public string? SourceDocId { get; set; }

        public List<GoodsReceiptLineVm> Lines { get; set; } = new();
    }

    public class GoodsReceiptLineVm
    {
        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? PurchaseOrderId { get; set; }

        public int? PoLineNo { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Range(0.0001, double.MaxValue)]
        public decimal ReceiptQty { get; set; }

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

        /// <summary>Phase G: 委外進貨 QC 良品數量</summary>
        public decimal QtyGood { get; set; }

        /// <summary>Phase G: 委外進貨 QC 不良品數量（&gt; 0 時觸發重工工單）</summary>
        public decimal QtyRejected { get; set; }
    }
}
