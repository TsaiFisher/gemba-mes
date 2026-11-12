using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>
    /// 手動應付發票的建單表單（manual-test-plan.md §11 #9）。
    ///
    /// **刻意只收「科目 + 金額」，沒有品號欄位。**
    ///
    /// 理由不是簡化，是**把危險路徑用結構關掉**：手動 AP 依定義走的是「不進存貨」那條路
    /// （分錄寫死借費用科目），拿它補「該進存貨卻沒走 GR」的進貨會讓存貨永久少計，
    /// 而且沒有任何訊號。開放品號欄位等於在畫面上邀請這種用法，
    /// 卻**沒有換來任何新能力**——物料進貨本來就該走進貨單，那條路徑已經存在且會正確入庫。
    ///
    /// ⚠️ 服務層仍然支援品號解析（`ExpenseAccountFor` 的第二條路徑），那是給
    /// 認列型發票（由進貨單產生）用的，不是給這張表單用的。兩者不要混談。
    /// </summary>
    public class ManualAPInvoiceViewModel
    {
        [Required]
        [MaxLength(20)]
        public string SupplierCode { get; set; } = string.Empty;

        public DateOnly InvoiceDate { get; set; }

        public DateOnly DueDate { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>交易幣 → 本幣。留空時由伺服器依發票日查匯率，查不到才用 1。</summary>
        public decimal? ExchangeRate { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<ManualAPInvoiceLineViewModel> Lines { get; set; } = new();
    }

    /// <summary>手動應付發票的明細行。</summary>
    public class ManualAPInvoiceLineViewModel
    {
        /// <summary>費用科目（<c>ChartOfAccount.AccountCode</c>，用途必須是費用）。</summary>
        [Required]
        [MaxLength(20)]
        public string AccountCode { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Description { get; set; }

        public decimal Qty { get; set; } = 1m;

        public decimal UnitPrice { get; set; }

        [MaxLength(20)]
        public string? TaxCode { get; set; }
    }
}
