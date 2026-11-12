using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class FxRevaluationService : IFxRevaluationService
    {
        private readonly ApplicationDbContext _db;

        public FxRevaluationService(ApplicationDbContext db) => _db = db;

        public async Task<IList<FxRevaluationEntry>> RunRevaluationAsync(
            string periodId, string plantId,
            IDictionary<string, decimal> periodEndRates,
            CancellationToken ct = default)
        {
            // 刪除本期間舊重評記錄
            var old = await _db.FxRevaluationEntries
                .Where(e => e.PeriodId == periodId && e.PlantId == plantId)
                .ToListAsync(ct);
            _db.FxRevaluationEntries.RemoveRange(old);

            var results = new List<FxRevaluationEntry>();

            // 以期末當時評估（見 OpenItemsAsOf）：只看期末日以前開立的發票，未結＝現在未結＋期末日之後才收付／折讓的。
            // 找不到期間（舊呼叫端）維持以現在為準。
            var periodEnd = await _db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PeriodId == periodId && p.PlantId == plantId)
                .Select(p => (DateOnly?)p.EndDate).FirstOrDefaultAsync(ct);
            var asOf = periodEnd ?? DateOnly.MaxValue;
            var apAfter = periodEnd is { } e1 ? await OpenItemsAsOf.ApSettledAfterAsync(_db, plantId, e1, ct) : new();
            var arAfter = periodEnd is { } e2 ? await OpenItemsAsOf.ArSettledAfterAsync(_db, plantId, e2, ct) : new();

            // AP Invoice 重評
            // 現在已付清的也要看：期末當時可能還沒付（未結由下方倒推，期末為 0 才略過）；
            // 已付清後才開折讓＝供應商欠我們（負餘額），同樣看得到
            var openAP = await _db.APInvoices
                .Where(i => i.PlantId == plantId
                         && i.Status != APInvoiceStatus.Void
                         && i.InvoiceDate <= asOf
                         && i.CurrencyCode != null)
                .AsNoTracking().ToListAsync(ct);

            foreach (var inv in openAP)
            {
                if (!periodEndRates.TryGetValue(inv.CurrencyCode!, out var endRate)) continue;
                // 未結＝總額 − 已付 − 已折讓（折讓單只累加 DebitedAmount、不動 TotalAmount，見 APDebitNoteService）
                var outstanding   = inv.TotalAmount - inv.PaidAmount - inv.DebitedAmount
                                  + apAfter.GetValueOrDefault(inv.APInvoiceId);
                if (outstanding == 0m) continue;
                var bookLocal     = Math.Round(outstanding * inv.ExchangeRate, 4);
                var revaluedLocal = Math.Round(outstanding * endRate, 4);
                // 應付是負債：期末本幣變大＝要多付＝損失。ExchangeGainLoss 一律以公司立場記
                // （正＝利益），原本 AP 與 AR 同一個減法，AP 的正數其實是損失。
                var gainLoss      = bookLocal - revaluedLocal;

                var entry = new FxRevaluationEntry
                {
                    PeriodId            = periodId,
                    DocType             = RevaluationDocType.APInvoice,
                    DocId               = inv.APInvoiceId,
                    CurrencyCode        = inv.CurrencyCode,
                    BookRate            = inv.ExchangeRate,
                    PeriodEndRate       = endRate,
                    OutstandingAmount   = outstanding,
                    BookAmountLocal     = bookLocal,
                    RevaluedAmountLocal = revaluedLocal,
                    ExchangeGainLoss    = gainLoss,
                    RevaluedAt          = DateTime.UtcNow,
                    PlantId             = plantId,
                };
                results.Add(entry);
                _db.FxRevaluationEntries.Add(entry);
            }

            // AR Invoice 重評
            var openAR = await _db.ARInvoices
                .Where(i => i.PlantId == plantId
                         && i.Status != ARInvoiceStatus.Void
                         && i.InvoiceDate <= asOf
                         && i.CurrencyCode != null)
                .AsNoTracking().ToListAsync(ct);

            foreach (var inv in openAR)
            {
                if (!periodEndRates.TryGetValue(inv.CurrencyCode!, out var endRate)) continue;
                // 未結＝總額 − 已收 − 已折讓（折讓單只累加 CreditedAmount、不動 TotalAmount，見 ARCreditNoteService）
                var outstanding   = inv.TotalAmount - inv.ReceivedAmount - inv.CreditedAmount
                                  + arAfter.GetValueOrDefault(inv.ARInvoiceId);
                if (outstanding == 0m) continue;
                var bookLocal     = Math.Round(outstanding * inv.ExchangeRate, 4);
                var revaluedLocal = Math.Round(outstanding * endRate, 4);
                var gainLoss      = revaluedLocal - bookLocal;

                var entry = new FxRevaluationEntry
                {
                    PeriodId            = periodId,
                    DocType             = RevaluationDocType.ARInvoice,
                    DocId               = inv.ARInvoiceId,
                    CurrencyCode        = inv.CurrencyCode,
                    BookRate            = inv.ExchangeRate,
                    PeriodEndRate       = endRate,
                    OutstandingAmount   = outstanding,
                    BookAmountLocal     = bookLocal,
                    RevaluedAmountLocal = revaluedLocal,
                    ExchangeGainLoss    = gainLoss,
                    RevaluedAt          = DateTime.UtcNow,
                    PlantId             = plantId,
                };
                results.Add(entry);
                _db.FxRevaluationEntries.Add(entry);
            }

            await _db.SaveChangesAsync(ct);
            return results;
        }
    }
}
