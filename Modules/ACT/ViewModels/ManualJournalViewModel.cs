using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.ACT.ViewModels
{
    /// <summary>
    /// 手動傳票的輸入模型。
    ///
    /// <para><b>為什麼這個畫面到 2026-09-21 才有</b>：<c>IJournalEntryService.CreateManualEntryAsync</c>
    /// 早就實作好、也有測試，但**沒有任何產品程式呼叫它**——只有測試在用。
    /// 手動調整分錄、期初餘額這兩件事因此都沒有入口。</para>
    ///
    /// <para>期間鎖、借貸平衡、單號取號一律由服務層把關（<c>JournalEntryService.CreateEntryAsync</c>），
    /// 這裡只負責把表單轉成 <c>JournalLineRequest</c>。畫面上的即時合計是體驗，不是閘門。</para>
    /// </summary>
    public class ManualJournalViewModel
    {
        /// <summary>分錄日期。決定落在哪一個會計期間，期間已關帳時服務層會擋下。</summary>
        [Required]
        public DateOnly EntryDate { get; set; }

        [MaxLength(200)]
        public string? Description { get; set; }

        public List<ManualJournalLineViewModel> Lines { get; set; } = new();
    }

    public class ManualJournalLineViewModel
    {
        [Required, MaxLength(20)]
        public string AccountCode { get; set; } = string.Empty;

        /// <summary>
        /// 借方金額。與 <see cref="Credit"/> 只能填一邊——兩邊都填的分錄在借貸合計上
        /// 仍可能平衡，卻表達不出任何有意義的會計事件，而且事後看不出是誤填。
        /// </summary>
        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        [MaxLength(50)]
        public string? CostCenter { get; set; }

        [MaxLength(200)]
        public string? Description { get; set; }
    }
}
