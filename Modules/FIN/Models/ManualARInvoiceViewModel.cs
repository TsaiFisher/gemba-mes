using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.FIN.Models
{
    /// <summary>
    /// 手動應收發票的建單表單（manual-test-plan.md §11 #9），與 AP 側對稱。
    ///
    /// 一樣**只收「科目 + 金額」，沒有品號欄位**，但理由與 AP 側不完全相同：
    /// AP 那邊是為了擋掉「拿它補該進存貨的進貨」（會讓存貨少計）；
    /// 這邊是**營收落點**——正常出貨開票走 `StandardAccounts.SalesRevenueCode`，
    /// 手動開票若也掛品號，同一個成品會同時存在兩條開票路徑，
    /// 5-6 報表對帳只要兩條都有單就對不起來（§3.4 ⑪ 記的正是這個形狀）。
    /// 手動 AR 的用途是**沒有出貨的收入**（服務費、雜項收入），那些本來就沒有品號。
    /// </summary>
    public class ManualARInvoiceViewModel
    {
        [Required]
        [MaxLength(20)]
        public string CustomerCode { get; set; } = string.Empty;

        public DateOnly InvoiceDate { get; set; }

        public DateOnly DueDate { get; set; }

        [MaxLength(20)]
        public string? CurrencyCode { get; set; }

        /// <summary>交易幣 → 本幣。留空時由伺服器依發票日查**賣出**匯率（收款側）。</summary>
        public decimal? ExchangeRate { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public List<ManualARInvoiceLineViewModel> Lines { get; set; } = new();
    }

    /// <summary>手動應收發票的明細行。</summary>
    public class ManualARInvoiceLineViewModel
    {
        /// <summary>收入科目（<c>ChartOfAccount.AccountCode</c>，用途必須是收入）。</summary>
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
