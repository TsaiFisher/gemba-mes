using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.FIN.Models
{
    public class APInvoiceLine
    {
        [Required, MaxLength(30)]
        public string APInvoiceId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        [MaxLength(30)]
        public string? ProductCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal Qty { get; set; }

        [MaxLength(20)]
        public string? UomCode { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal UnitPrice { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal LineAmount { get; set; }

        [MaxLength(20)]
        public string? TaxCode { get; set; }

        [Column(TypeName = "decimal(8,4)")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal TaxAmount { get; set; }

        /// <summary>
        /// 手動發票明細指定的科目（<c>ChartOfAccount.AccountCode</c>）。
        ///
        /// 解析順序是**這個欄位優先，沒填才退回產品類別**（見 <c>APInvoiceService.ExpenseAccountFor</c>）：
        /// 費用類的手動發票（水電、運費、顧問費）本來就沒有品號，逼它們掛一個假品號等於
        /// 把費用塞進物料主檔。認列型發票（由進貨單產生）永遠不填這一欄，行為完全不變。
        /// </summary>
        [MaxLength(20)]
        public string? AccountCode { get; set; }

        /// <summary>來源明細序號（對應進貨單 LineNo）</summary>
        public int? SourceLineNo { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [ForeignKey(nameof(APInvoiceId))]
        public APInvoice? APInvoice { get; set; }
    }
}
