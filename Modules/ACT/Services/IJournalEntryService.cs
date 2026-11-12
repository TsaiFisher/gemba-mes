using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public record JournalLineRequest(
        string AccountCode,
        string AccountName,
        decimal Debit,
        decimal Credit,
        string? CurrencyCode = null,
        decimal Amount = 0,
        decimal ExchangeRate = 1,
        decimal AmountLocal = 0,
        string? CostCenter = null,
        string? Description = null,
        string? SourceRef = null);   // 行級來源維度（§9 P-2 D2-A：如工單號），期間彙總分錄逐來源拆行時填入

    public interface IJournalEntryService
    {
        /// <summary>建立自動日記帳（業務交易觸發）</summary>
        Task<JournalEntry> CreateAutoEntryAsync(
            string sourceDocType, string sourceDocId,
            DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default);

        /// <summary>建立手動調整分錄</summary>
        Task<JournalEntry> CreateManualEntryAsync(
            string periodId, DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default);

        /// <summary>建立期末結算調整分錄（Adjustment 型）。Closing（月結中）期間唯一可入帳的類型，
        /// 供匯率重評、NRV 減損、工單實際費率重估等月結調整使用（§8 期間鎖）。</summary>
        /// <summary>
        /// 沖銷一張手動傳票：產生借貸對調、逐行對應的新傳票（來源類別 ManualReversal、來源單號＝原傳票），
        /// 原傳票保留不動以供追溯。只限手動傳票；同一張只能沖銷一次；沖銷傳票本身不可再沖銷。
        /// 期間鎖照常生效（沖銷日落在已關帳期間會被擋）。
        /// </summary>
        Task<JournalEntry> ReverseManualEntryAsync(
            string journalId, DateOnly entryDate, string plantId,
            string? description = null,
            CancellationToken ct = default);

        Task<JournalEntry> CreateAdjustmentEntryAsync(
            string sourceDocType, string? sourceDocId,
            DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default);
    }
}
