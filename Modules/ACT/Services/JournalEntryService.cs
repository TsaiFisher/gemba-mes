using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class JournalEntryService : IJournalEntryService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService    _docNo;
        private readonly IAccountingPeriodService _periods;

        public JournalEntryService(
            ApplicationDbContext db,
            IDocNumberService docNo,
            IAccountingPeriodService periods)
        {
            _db      = db;
            _docNo   = docNo;
            _periods = periods;
        }

        public async Task<JournalEntry> CreateAutoEntryAsync(
            string sourceDocType, string sourceDocId,
            DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default)
        {
            var period = await _periods.GetByDateAsync(entryDate, plantId, ct);
            var periodId = period?.PeriodId
                ?? $"{entryDate.Year:D4}{entryDate.Month:D2}";

            return await CreateEntryAsync(
                JournalEntryType.Automatic, periodId, entryDate, plantId,
                sourceDocType, sourceDocId, lines, description, ct);
        }

        public async Task<JournalEntry> CreateManualEntryAsync(
            string periodId, DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default)
        {
            return await CreateEntryAsync(
                JournalEntryType.Manual, periodId, entryDate, plantId,
                "Manual", null, lines, description, ct);
        }

        public const string ManualReversalSourceType = "ManualReversal";

        public async Task<JournalEntry> ReverseManualEntryAsync(
            string journalId, DateOnly entryDate, string plantId,
            string? description = null,
            CancellationToken ct = default)
        {
            var original = await _db.JournalEntries.IgnoreQueryFilters().Include(e => e.Lines)
                .FirstOrDefaultAsync(e => e.JournalId == journalId && e.PlantId == plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_JournalNotFoundFmt", journalId));

            // 只沖手動傳票：自動分錄的更正走來源單據（作廢／回沖），從這裡沖會與子帳對不起來
            if (original.Type != JournalEntryType.Manual)
                throw new InvalidOperationException(PzText.Get("Act_JournalReverseManualOnlyFmt", journalId));
            if (original.SourceDocType == ManualReversalSourceType)
                throw new InvalidOperationException(PzText.Get("Act_JournalIsReversalFmt", journalId));
            var existing = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.PlantId == plantId && e.SourceDocType == ManualReversalSourceType && e.SourceDocId == journalId)
                .Select(e => e.JournalId).FirstOrDefaultAsync(ct);
            if (existing != null)
                throw new InvalidOperationException(PzText.Get("Act_JournalAlreadyReversedFmt", journalId, existing));

            var lines = original.Lines.OrderBy(l => l.LineNo).Select(l => new JournalLineRequest(
                l.AccountCode, l.AccountName,
                Debit: l.Credit, Credit: l.Debit,
                CurrencyCode: l.CurrencyCode, Amount: l.Amount, ExchangeRate: l.ExchangeRate, AmountLocal: l.AmountLocal,
                CostCenter: l.CostCenter, Description: l.Description, SourceRef: l.SourceRef)).ToList();

            var period = await _periods.GetByDateAsync(entryDate, plantId, ct);
            var periodId = period?.PeriodId ?? $"{entryDate.Year:D4}{entryDate.Month:D2}";
            return await CreateEntryAsync(
                JournalEntryType.Manual, periodId, entryDate, plantId,
                ManualReversalSourceType, journalId, lines,
                description ?? PzText.Get("Act_JournalReversalDescFmt", journalId), ct);
        }

        public async Task<JournalEntry> CreateAdjustmentEntryAsync(
            string sourceDocType, string? sourceDocId,
            DateOnly entryDate, string plantId,
            IList<JournalLineRequest> lines,
            string? description = null,
            CancellationToken ct = default)
        {
            var period = await _periods.GetByDateAsync(entryDate, plantId, ct);
            var periodId = period?.PeriodId
                ?? $"{entryDate.Year:D4}{entryDate.Month:D2}";

            return await CreateEntryAsync(
                JournalEntryType.Adjustment, periodId, entryDate, plantId,
                sourceDocType, sourceDocId, lines, description, ct);
        }

        private async Task<JournalEntry> CreateEntryAsync(
            JournalEntryType type,
            string periodId, DateOnly entryDate, string plantId,
            string sourceDocType, string? sourceDocId,
            IList<JournalLineRequest> lines,
            string? description,
            CancellationToken ct)
        {
            // 期間鎖倉（roadmap-fin-cst-sub.md §8）：Closed 一律不可入帳；
            // Closing（月結中）僅允許 Adjustment 調整分錄（匯率重評、NRV 減損等）。
            var period = await _periods.GetByIdAsync(periodId, plantId, ct)
                         ?? await _periods.GetByDateAsync(entryDate, plantId, ct);
            if (period != null)
            {
                if (period.Status == Models.PeriodStatus.Closed)
                    throw new InvalidOperationException(
                        PzText.Get("Act_PeriodClosedNoJournalFmt", period.PeriodId));
                if (period.Status == Models.PeriodStatus.Closing && type != JournalEntryType.Adjustment)
                    throw new InvalidOperationException(
                        PzText.Get("Act_PeriodClosingAdjustOnlyFmt", period.PeriodId));
            }

            // 借貸平衡守門：先前只有 DataAuditService §3.4 事後稽核，寫入端完全不擋，
            // 不平衡的分錄照樣落地，要等下一次稽核跑完才會被發現（且已影響財報）。
            // 複式簿記的基本不變量應該在入口就成立，不該靠事後對帳補救。
            if (lines.Count == 0)
                throw new InvalidOperationException(PzText.Get("Act_JournalNeedsLines"));

            var totalDebit  = lines.Sum(l => l.Debit);
            var totalCredit = lines.Sum(l => l.Credit);
            if (totalDebit != totalCredit)
                throw new InvalidOperationException(
                    PzText.Get("Act_JournalUnbalancedFmt", totalDebit, totalCredit, sourceDocType, sourceDocId));

            var entry = new JournalEntry
            {
                JournalId     = await _docNo.NextAsync("JournalEntry", plantId, ct: ct),
                PeriodId      = periodId,
                EntryDate     = entryDate,
                SourceDocType = sourceDocType,
                SourceDocId   = sourceDocId,
                Type          = type,
                Description   = description,
                PlantId       = plantId,
                CreatedAt     = DateTime.UtcNow,
            };

            int lineNo = 1;
            foreach (var l in lines)
            {
                entry.Lines.Add(new JournalLine
                {
                    LineNo       = lineNo++,
                    AccountCode  = l.AccountCode,
                    AccountName  = l.AccountName,
                    Debit        = l.Debit,
                    Credit       = l.Credit,
                    CurrencyCode = l.CurrencyCode,
                    Amount       = l.Amount,
                    ExchangeRate = l.ExchangeRate,
                    AmountLocal  = l.AmountLocal > 0 ? l.AmountLocal : l.Amount * l.ExchangeRate,
                    CostCenter   = l.CostCenter,
                    SourceRef    = l.SourceRef,
                    Description  = l.Description,
                });
            }

            _db.JournalEntries.Add(entry);
            await _db.SaveChangesAsync(ct);
            return entry;
        }
    }
}
