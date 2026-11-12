using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    /// <summary>
    /// 期末當時的應收／應付未結：發票上的已收付、已折讓欄位是「現在」的累計，回頭評估某一期期末時，
    /// 要把期末日之後才確認的收付款與折讓加回去（交易幣）。
    ///
    /// <para>外幣重評與 ECL 原本都直接用現在的累計：月底當下跑沒有差別，但反關帳後重做上一期月結，
    /// 下一期的收款會讓上期未結變少、下一期才開的發票（重評沒有過濾開立日）會被拿上期期末匯率重評，
    /// 期末應收應付與匯兌損益整個算錯（2026-10-24 月結隨機序列）。</para>
    /// </summary>
    public static class OpenItemsAsOf
    {
        /// <summary>應收發票 → 期末日之後才確認的收款＋過帳的折讓（交易幣）。</summary>
        public static async Task<Dictionary<string, decimal>> ArSettledAfterAsync(
            ApplicationDbContext db, string plantId, DateOnly asOf, CancellationToken ct = default)
        {
            var receipts = await db.ARReceiptAllocations.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.ARReceipt!.PlantId == plantId && a.ARReceipt.Status == ARReceiptStatus.Received
                         && a.ARReceipt.ReceiptDate > asOf)
                .GroupBy(a => a.ARInvoiceId)
                .Select(g => new { Id = g.Key, Amt = g.Sum(a => a.AllocatedAmount) })
                .ToListAsync(ct);
            var notes = await db.ARCreditNoteLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.ARCreditNote!.PlantId == plantId && l.ARCreditNote.Status == CreditNoteStatus.Posted
                         && l.ARCreditNote.NoteDate > asOf)
                .GroupBy(l => l.ARInvoiceId)
                .Select(g => new { Id = g.Key, Amt = g.Sum(l => l.LineAmount + l.TaxAmount) })
                .ToListAsync(ct);
            return Merge(receipts.Select(r => (r.Id, r.Amt)).Concat(notes.Select(n => (n.Id, n.Amt))));
        }

        /// <summary>應付發票 → 期末日之後才確認的付款＋過帳的折讓（交易幣）。</summary>
        public static async Task<Dictionary<string, decimal>> ApSettledAfterAsync(
            ApplicationDbContext db, string plantId, DateOnly asOf, CancellationToken ct = default)
        {
            var payments = await db.APPaymentAllocations.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.APPayment!.PlantId == plantId && a.APPayment.Status == APPaymentStatus.Paid
                         && a.APPayment.PaymentDate > asOf)
                .GroupBy(a => a.APInvoiceId)
                .Select(g => new { Id = g.Key, Amt = g.Sum(a => a.AllocatedAmount) })
                .ToListAsync(ct);
            var notes = await db.APDebitNoteLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.APDebitNote!.PlantId == plantId && l.APDebitNote.Status == CreditNoteStatus.Posted
                         && l.APDebitNote.NoteDate > asOf)
                .GroupBy(l => l.APInvoiceId)
                .Select(g => new { Id = g.Key, Amt = g.Sum(l => l.LineAmount + l.TaxAmount) })
                .ToListAsync(ct);
            return Merge(payments.Select(r => (r.Id, r.Amt)).Concat(notes.Select(n => (n.Id, n.Amt))));
        }

        private static Dictionary<string, decimal> Merge(IEnumerable<(string Id, decimal Amt)> rows)
            => rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Sum(r => r.Amt));
    }
}
