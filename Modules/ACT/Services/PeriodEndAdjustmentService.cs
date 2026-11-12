using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>月結步驟 9「產生期末調整分錄」的結果；略過的項目附原因，畫面寫進步驟備註。</summary>
    public record PeriodEndAdjustmentResult(
        string? FxJournalId, string? FxReversalJournalId,
        string? NrvJournalId, string? EclJournalId,
        IReadOnlyList<string> Notes);

    public interface IPeriodEndAdjustmentService
    {
        /// <summary>把本期外幣重評、NRV 減損、ECL 備抵的評估結果入總帳（Adjustment 型）。可重跑：只補差額。</summary>
        Task<PeriodEndAdjustmentResult> PostAsync(string periodId, string plantId, CancellationToken ct = default);
    }

    /// <summary>
    /// 期末評價入帳。三支評估服務（<see cref="FxRevaluationService"/>、<see cref="NrvAssessmentService"/>、
    /// <see cref="EclService"/>）原本只寫評估表，月結步驟 9 只是打勾——關帳關得了，總帳卻沒有這些調整。
    ///
    /// <para><b>外幣重評：期末入帳、次期初迴轉。</b>重評是拿「入帳匯率」比「期末匯率」，
    /// 而收付款時的已實現匯兌損益也是拿入帳匯率算——不迴轉的話，下期收款會把同一段匯差再認一次。
    /// 迴轉分錄日期＝期末次日，落在下一期。逐張發票拆行（SourceRef＝發票號）。</para>
    ///
    /// <para><b>NRV／ECL：備抵科目調到等於本期評估額。</b>分錄金額＝評估合計 − 備抵科目截至期末的餘額，
    /// 增提借損失、迴轉貸損失。用總帳餘額而不是「上一期評估」算差額，前期有沒有跑、跑幾次都不影響。</para>
    ///
    /// <para><b>可重跑。</b>取消月結後重做步驟 9，三條腿都只補差額：外幣比對本期已入帳的逐張金額，
    /// 備抵比對科目餘額。沒有差額就不開分錄。</para>
    ///
    /// <para><b>本期沒有評估資料就略過該腿</b>（備註寫明），不把它當成「評估結果為 0」——
    /// 那會把前期提列的備抵整筆迴轉掉。</para>
    /// </summary>
    public class PeriodEndAdjustmentService : IPeriodEndAdjustmentService
    {
        public const string FxSourceType         = "FxRevaluation";
        public const string FxReversalSourceType = "FxRevaluationReversal";
        public const string NrvSourceType        = "NrvWriteDown";
        public const string EclSourceType        = "EclAllowance";

        private readonly ApplicationDbContext _db;
        private readonly IAccountingPeriodService _periods;
        private readonly IJournalEntryService _journal;

        public PeriodEndAdjustmentService(
            ApplicationDbContext db, IAccountingPeriodService periods, IJournalEntryService journal)
        {
            _db = db;
            _periods = periods;
            _journal = journal;
        }

        public async Task<PeriodEndAdjustmentResult> PostAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            var period = await _periods.GetByIdAsync(periodId, plantId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_AccountingPeriodNotFoundFmt", plantId, periodId));
            if (period.Status == PeriodStatus.Closed)
                throw new InvalidOperationException(PzText.Get("Act_PeriodClosedNoAdjustFmt", plantId, periodId));

            var notes = new List<string>();
            var (fxId, fxRevId) = await PostFxAsync(period, notes, ct);

            var nrvTarget = await _db.InventoryNrvAssessments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.PeriodId == periodId && a.PlantId == plantId)
                .Select(a => (decimal?)a.ImpairmentLoss).ToListAsync(ct);
            var nrvId = await PostAllowanceAsync(period, nrvTarget, NrvSourceType, "NRV 存貨跌價", "Nrv",
                StandardAccounts.InventoryWriteDownLossCode, StandardAccounts.InventoryWriteDownLossName,
                StandardAccounts.InventoryValuationAllowanceCode, StandardAccounts.InventoryValuationAllowanceName,
                notes, ct);

            var eclTarget = await _db.EclAllowances.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.PeriodId == periodId && a.PlantId == plantId)
                .Select(a => (decimal?)a.EclAmount).ToListAsync(ct);
            var eclId = await PostAllowanceAsync(period, eclTarget, EclSourceType, "ECL 備抵損失", "Ecl",
                StandardAccounts.CreditImpairmentLossCode, StandardAccounts.CreditImpairmentLossName,
                StandardAccounts.ReceivableLossAllowanceCode, StandardAccounts.ReceivableLossAllowanceName,
                notes, ct);

            return new PeriodEndAdjustmentResult(fxId, fxRevId, nrvId, eclId, notes);
        }

        private async Task<(string?, string?)> PostFxAsync(AccountingPeriod period, List<string> notes, CancellationToken ct)
        {
            var rows = await _db.FxRevaluationEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.PeriodId == period.PeriodId && e.PlantId == period.PlantId)
                .ToListAsync(ct);

            // 本期已入帳的逐張損益（貸方為利益）與控制科目：重跑時只補差額
            var postedLines = await _db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JournalEntry!.PlantId == period.PlantId
                         && l.JournalEntry.SourceDocType == FxSourceType
                         && l.JournalEntry.SourceDocId == period.PeriodId)
                .Select(l => new { l.SourceRef, l.AccountCode, Net = l.Credit - l.Debit })
                .ToListAsync(ct);
            var posted = postedLines
                .Where(l => l.AccountCode == StandardAccounts.FxGainCode || l.AccountCode == StandardAccounts.FxLossCode)
                .GroupBy(l => l.SourceRef ?? "")
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
            var postedAp = postedLines
                .Where(l => l.AccountCode == StandardAccounts.AccountsPayableCode)
                .Select(l => l.SourceRef ?? "").ToHashSet();

            if (rows.Count == 0 && posted.Count == 0)
            {
                notes.Add(MonthEndNote.Token("Note_FxNone"));
                return (null, null);
            }

            var docs = rows.ToDictionary(r => r.DocId, r => (Gain: r.ExchangeGainLoss, r.DocType));
            var lines = new List<JournalLineRequest>();
            foreach (var docId in docs.Keys.Union(posted.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var target = docs.TryGetValue(docId, out var d) ? d.Gain : 0m;
                var delta  = Math.Round(target - posted.GetValueOrDefault(docId), 4);
                if (delta == 0m) continue;

                // 已入帳、但這次重評沒有結果的發票（例如重跑前已收付清）：科目沿用原分錄。
                var isAp = docs.ContainsKey(docId) ? d.DocType == RevaluationDocType.APInvoice : postedAp.Contains(docId);
                var (ctrlCode, ctrlName) = isAp
                    ? (StandardAccounts.AccountsPayableCode, StandardAccounts.AccountsPayableName)
                    : (StandardAccounts.AccountsReceivableCode, StandardAccounts.AccountsReceivableName);

                // 利益：應收增加／應付減少都是借控制科目；損失反之。
                if (delta > 0)
                {
                    lines.Add(new(ctrlCode, ctrlName, delta, 0m, SourceRef: docId));
                    lines.Add(new(StandardAccounts.FxGainCode, StandardAccounts.FxGainName, 0m, delta, SourceRef: docId));
                }
                else
                {
                    lines.Add(new(StandardAccounts.FxLossCode, StandardAccounts.FxLossName, -delta, 0m, SourceRef: docId));
                    lines.Add(new(ctrlCode, ctrlName, 0m, -delta, SourceRef: docId));
                }
            }

            string? entryId = null;
            if (lines.Count > 0)
            {
                var entry = await _journal.CreateAdjustmentEntryAsync(
                    FxSourceType, period.PeriodId, period.EndDate, period.PlantId!, lines,
                    $"期末外幣重評 {period.PeriodId}", ct);
                entryId = entry.JournalId;
            }

            // 迴轉分錄不是「把這次的 lines 反過來」，而是補到「迴轉＝已入帳重評的反向」為止：
            // 期末那張存檔之後、迴轉這張才失敗（例如下一期已關帳）時，重跑看到重評已無差額，
            // 若只跟著本次 lines 走，缺的那張迴轉永遠不會再補。
            var reversalLines = await ReversalShortfallAsync(period, ct);
            string? reversalId = null;
            if (reversalLines.Count > 0)
            {
                var reversal = await _journal.CreateAdjustmentEntryAsync(
                    FxReversalSourceType, period.PeriodId, period.EndDate.AddDays(1), period.PlantId!,
                    reversalLines, $"迴轉 {period.PeriodId} 期末外幣重評", ct);
                reversalId = reversal.JournalId;
            }

            notes.Add(entryId is null && reversalId is null
                ? MonthEndNote.Token("Note_FxNoDiff")
                : MonthEndNote.Token("Note_FxPosted", entryId ?? "—", reversalId ?? "—"));
            return (entryId, reversalId);
        }

        /// <summary>逐（發票, 科目）比對：迴轉應為期末重評的反向，缺多少補多少。</summary>
        private async Task<List<JournalLineRequest>> ReversalShortfallAsync(AccountingPeriod period, CancellationToken ct)
        {
            var nets = await _db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JournalEntry!.PlantId == period.PlantId
                         && (l.JournalEntry.SourceDocType == FxSourceType || l.JournalEntry.SourceDocType == FxReversalSourceType)
                         && l.JournalEntry.SourceDocId == period.PeriodId)
                .GroupBy(l => new { l.SourceRef, l.AccountCode, l.AccountName })
                .Select(g => new { g.Key.SourceRef, g.Key.AccountCode, g.Key.AccountName, Net = g.Sum(l => l.Debit - l.Credit) })
                .ToListAsync(ct);

            // 重評＋迴轉合計應為 0；不為 0 的部分就是迴轉還欠的（取反向）
            return nets.Where(n => Math.Round(n.Net, 4) != 0m)
                .OrderBy(n => n.SourceRef, StringComparer.Ordinal).ThenBy(n => n.AccountCode, StringComparer.Ordinal)
                .Select(n => n.Net > 0
                    ? new JournalLineRequest(n.AccountCode, n.AccountName, 0m, n.Net, SourceRef: n.SourceRef)
                    : new JournalLineRequest(n.AccountCode, n.AccountName, -n.Net, 0m, SourceRef: n.SourceRef))
                .ToList();
        }

        private async Task<string?> PostAllowanceAsync(
            AccountingPeriod period, List<decimal?> assessed, string sourceType, string label, string noteKey,
            string lossCode, string lossName, string allowanceCode, string allowanceName,
            List<string> notes, CancellationToken ct)
        {
            if (assessed.Count == 0)
            {
                notes.Add(MonthEndNote.Token($"Note_{noteKey}None"));
                return null;
            }

            var target  = Math.Round(assessed.Sum() ?? 0m, 4);
            var balance = await _db.JournalLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JournalEntry!.PlantId == period.PlantId
                         && l.JournalEntry!.EntryDate <= period.EndDate
                         && l.AccountCode == allowanceCode)
                .SumAsync(l => l.Credit - l.Debit, ct);
            var delta = Math.Round(target - balance, 4);

            if (delta == 0m)
            {
                notes.Add(MonthEndNote.Token($"Note_{noteKey}NoDiff", target.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
                return null;
            }

            var lines = delta > 0
                ? new List<JournalLineRequest>
                  {
                      new(lossCode, lossName, delta, 0m),
                      new(allowanceCode, allowanceName, 0m, delta),
                  }
                : new List<JournalLineRequest>
                  {
                      new(allowanceCode, allowanceName, -delta, 0m),
                      new(lossCode, lossName, 0m, -delta),
                  };

            var entry = await _journal.CreateAdjustmentEntryAsync(
                sourceType, period.PeriodId, period.EndDate, period.PlantId!, lines,
                $"期末{label} {period.PeriodId}：評估 {target:0.####}、調整前備抵 {balance:0.####}", ct);
            notes.Add(MonthEndNote.Token(delta > 0 ? $"Note_{noteKey}Increase" : $"Note_{noteKey}Reverse",
                entry.JournalId, Math.Abs(delta).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            return entry.JournalId;
        }
    }
}
