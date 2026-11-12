using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    /// <summary>
    /// 銷貨折讓單服務。設計理由見 <see cref="ARCreditNote"/>；
    /// 這裡只記與實作有關的三件事。
    ///
    /// **一、分錄與既有回沖完全相同，是刻意的。**
    /// Open 發票被退貨時 <c>ReverseForReturnedQtyAsync</c> 產生
    /// 借 銷貨收入 ＋ 借 銷項稅額 ／ 貸 應收帳款；折讓單產生的是同一組。
    /// 同一件經濟事實（貨退回來了）不該因為「當時收款了沒」而落到不同科目——
    /// 那會讓損益表上的銷貨收入依客戶的付款速度而變動。
    ///
    /// **二、`TotalAmount` 一律不動。**
    /// 發票開出去並申報之後金額就是定的；折讓在稅務上是另一張憑證。
    /// 子帳的未結餘額改由 <c>TotalAmount − ReceivedAmount − CreditedAmount</c> 算。
    ///
    /// **三、狀態不跟著折讓走。**
    /// 發票的 <c>Status</c>（Open／PartialReceived／Received）描述的是**收款**進度，
    /// 折讓不是收款。全額折讓後發票仍可能停在 PartialReceived——那是對的，
    /// 它如實記著「客戶付過一部分錢」，而餘額由 CreditedAmount 那一欄解釋。
    /// </summary>
    public class ARCreditNoteService : IARCreditNoteService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currency;
        private readonly Modules.ACT.Services.IAccountingPeriodService? _periodSvc;
        private readonly Modules.ACT.Services.IJournalEntryService? _journalSvc;

        private const string CreditDocType = "ARCreditNote";

        public ARCreditNoteService(
            ApplicationDbContext db,
            IDocNumberService docNo,
            IDateTimeService dt,
            ICurrencyService currency,
            Modules.ACT.Services.IAccountingPeriodService? periodSvc = null,
            Modules.ACT.Services.IJournalEntryService? journalSvc = null)
        {
            _db = db; _docNo = docNo; _dt = dt; _currency = currency;
            _periodSvc = periodSvc; _journalSvc = journalSvc;
        }

        private Task AssertPeriodOpenAsync(DateOnly date, string? plantId, CancellationToken ct)
            => _periodSvc?.AssertCanPostAsync(date, plantId ?? string.Empty, ct) ?? Task.CompletedTask;

        public async Task<ARCreditNote?> CreateDraftFromSalesReturnAsync(
            string salesReturnId, CancellationToken ct = default)
        {
            // 冪等前置：唯一索引是最後一道，這裡先查是為了讓重試拿到同一張而不是撞 23505。
            var existing = await _db.ARCreditNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.SourceDocType == "SalesReturn" && n.SourceDocId == salesReturnId, ct);
            if (existing != null) return existing;

            var ret = await _db.SalesReturns.IgnoreQueryFilters()
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.SalesReturnId == salesReturnId, ct);
            if (ret == null) return null;

            var shipIds = ret.Lines.Where(l => l.GoodsShipmentId != null)
                .Select(l => l.GoodsShipmentId!).Distinct().ToList();
            if (shipIds.Count == 0) return null;

            var invoices = await _db.ARInvoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Where(i => i.SourceDocType == "GoodsShipment" && shipIds.Contains(i.SourceDocId!)
                         && i.Status != ARInvoiceStatus.Void)   // 作廢舊單與重開新單並存時只看新單
                .ToListAsync(ct);
            if (invoices.Count == 0) return null;

            // 已折讓過的量（跨所有未作廢的折讓單）——部分退貨分多次來時不能重複折。
            var invoiceIds = invoices.Select(i => i.ARInvoiceId).ToList();
            var alreadyCredited = await _db.ARCreditNoteLines.IgnoreQueryFilters()
                .Where(l => invoiceIds.Contains(l.ARInvoiceId)
                         && l.ARCreditNote!.Status != CreditNoteStatus.Void)
                .GroupBy(l => new { l.ARInvoiceId, l.InvoiceLineNo })
                .Select(g => new { g.Key.ARInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                .ToListAsync(ct);

            var note = new ARCreditNote
            {
                CustomerCode  = ret.CompanyCode,
                NoteDate      = ret.ReturnDate,
                SourceDocType = "SalesReturn",
                SourceDocId   = salesReturnId,
                PlantId       = ret.PlantId,
                CreatedBy     = ret.CreatedBy,
                CreatedByRoleId = ret.CreatedByRoleId,
                Status        = CreditNoteStatus.Draft,
                Remarks       = $"銷退單 {salesReturnId} 之已收款發票折讓",
            };

            var lineNo = 0;
            foreach (var rl in ret.Lines.OrderBy(l => l.LineNo))
            {
                if (rl.GoodsShipmentId == null || rl.ShipmentLineNo == null || rl.ReturnQty <= 0m) continue;

                var inv = invoices.FirstOrDefault(i => i.SourceDocId == rl.GoodsShipmentId);
                if (inv == null) continue;

                // 只處理「已結清／部分收款」的——Open 的那些 ReverseForReturnedQtyAsync
                // 當場就把發票金額改小了，再開一張折讓會沖兩次。
                if (inv.Status == ARInvoiceStatus.Open || inv.Status == ARInvoiceStatus.Void) continue;

                var arLine = inv.Lines.FirstOrDefault(l => l.SourceLineNo == rl.ShipmentLineNo.Value);
                if (arLine == null || arLine.Qty <= 0m) continue;

                var credited = alreadyCredited.FirstOrDefault(
                    c => c.ARInvoiceId == inv.ARInvoiceId && c.InvoiceLineNo == arLine.LineNo)?.Qty ?? 0m;
                var creditable = arLine.Qty - credited;
                if (creditable <= 0m) continue;

                var qty = Math.Min(rl.ReturnQty, creditable);
                var currency = inv.CurrencyCode ?? await _currency.GetBaseCurrencyAsync() ?? "";
                var lineAmt  = await _currency.RoundAsync(qty * arLine.UnitPrice, currency);
                var taxAmt   = await _currency.RoundAsync(lineAmt * arLine.TaxRate, currency);

                note.Lines.Add(new ARCreditNoteLine
                {
                    LineNo        = ++lineNo,
                    ARInvoiceId   = inv.ARInvoiceId,
                    InvoiceLineNo = arLine.LineNo,
                    ProductCode   = rl.ProductCode,
                    Qty           = qty,
                    UomCode       = rl.UomCode,
                    UnitPrice     = arLine.UnitPrice,
                    LineAmount    = lineAmt,
                    TaxRate       = arLine.TaxRate,
                    TaxAmount     = taxAmt,
                });

                note.CurrencyCode ??= inv.CurrencyCode;
                note.ExchangeRate  = inv.ExchangeRate == 0m ? 1m : inv.ExchangeRate;
            }

            if (note.Lines.Count == 0) return null;   // 沒有一行需要折讓就不建空單

            var baseCcy = await _currency.GetBaseCurrencyAsync() ?? "";
            note.TaxAmount   = note.Lines.Sum(l => l.TaxAmount);
            note.TotalAmount = note.Lines.Sum(l => l.LineAmount) + note.TaxAmount;
            note.AmountLocal = await _currency.RoundAsync(note.TotalAmount * note.ExchangeRate, baseCcy);

            note.ARCreditNoteId = await _docNo.NextAsync(CreditDocType, note.PlantId ?? "", ct: ct);
            foreach (var l in note.Lines) l.ARCreditNoteId = note.ARCreditNoteId;

            _db.ARCreditNotes.Add(note);
            await _db.SaveChangesAsync(ct);
            return note;
        }

        public async Task ConfirmAsync(string creditNoteId, CancellationToken ct = default)
        {
            var note = await _db.ARCreditNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.ARCreditNoteId == creditNoteId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_NoteNotFoundFmt", creditNoteId));

            if (note.Status != CreditNoteStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Fin_NoteNotDraftFmt", creditNoteId));

            await AssertPeriodOpenAsync(note.NoteDate, note.PlantId, ct);

            var invoiceIds = note.Lines.Select(l => l.ARInvoiceId).Distinct().ToList();
            var invoices = await _db.ARInvoices.IgnoreQueryFilters()
                .Where(i => invoiceIds.Contains(i.ARInvoiceId))
                .ToDictionaryAsync(i => i.ARInvoiceId, ct);

            var baseCcy = await _currency.GetBaseCurrencyAsync() ?? "";

            // 上限守的是**數量**，不是未結餘額。
            //
            // 起初寫成「折讓不得超過未結餘額」，被 ConfirmingTheCreditNote_ClosesTheGap 當場擋下：
            // 發票已全額收款時未結餘額是 0，而那正是這張單存在的情境——客戶付了錢又退貨，
            // 公司本來就欠客戶那筆錢。把它擋掉等於宣告「已付清的發票不能退貨」。
            // 折讓後應收出現貸餘（公司欠客戶）是**正確的結果**，後續由退款或抵下一張發票處理。
            //
            // 真正的上限是「折讓量不得超過發票上開過的量」——那也正是
            // RETURN_SUBLEDGER_UNREVERSED 在比的東西。草稿產生時已經上限過一次，
            // 這裡必須再驗一次：草稿到確認之間可能有另一張折讓單先過帳了。
            var noteInvIds = note.Lines.Select(l => l.ARInvoiceId).Distinct().ToList();
            var postedQty = await _db.ARCreditNoteLines.IgnoreQueryFilters()
                .Where(l => noteInvIds.Contains(l.ARInvoiceId)
                         && l.ARCreditNoteId != note.ARCreditNoteId
                         && l.ARCreditNote!.Status == CreditNoteStatus.Posted)
                .GroupBy(l => new { l.ARInvoiceId, l.InvoiceLineNo })
                .Select(g => new { g.Key.ARInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                .ToListAsync(ct);

            var invoiceLines = await _db.ARInvoiceLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => noteInvIds.Contains(l.ARInvoiceId))
                .ToListAsync(ct);

            foreach (var line in note.Lines)
            {
                var invLine = invoiceLines.FirstOrDefault(
                    l => l.ARInvoiceId == line.ARInvoiceId && l.LineNo == line.InvoiceLineNo)
                    ?? throw new InvalidOperationException(
                        PzText.Get("Fin_NoteLineInvoiceLineMissingFmt", creditNoteId, line.LineNo, line.ARInvoiceId, line.InvoiceLineNo));

                var already = postedQty.FirstOrDefault(
                    q => q.ARInvoiceId == line.ARInvoiceId && q.InvoiceLineNo == line.InvoiceLineNo)?.Qty ?? 0m;
                if (already + line.Qty > invLine.Qty)
                    throw new InvalidOperationException(
                        PzText.Get("Fin_NoteQtyExceedsInvoiceFmt", already + line.Qty, line.ARInvoiceId, line.InvoiceLineNo, invLine.Qty));
            }

            foreach (var g in note.Lines.GroupBy(l => l.ARInvoiceId))
            {
                if (!invoices.TryGetValue(g.Key, out var inv))
                    throw new InvalidOperationException(PzText.Get("Fin_NoteInvoiceMissingFmt", creditNoteId, g.Key));
                // 草稿開出來之後發票才被作廢（收款都沖掉就能作廢）：再確認等於對一張不存在的應收記折讓，
                // 分錄貸應收卻沒有發票承接（2026-10-22 隨機序列測試發現）
                if (inv.Status == ARInvoiceStatus.Void)
                    throw new InvalidOperationException(PzText.Get("Fin_NoteInvoiceVoidedFmt", creditNoteId, g.Key));

                inv.CreditedAmount += g.Sum(l => l.LineAmount + l.TaxAmount);
                inv.UpdatedAt = DateTime.UtcNow;
            }

            note.Status = CreditNoteStatus.Posted;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await PostJournalAsync(note, reverse: false, baseCcy, ct);
        }

        public async Task VoidAsync(string creditNoteId, string? reason = null, CancellationToken ct = default)
        {
            var note = await _db.ARCreditNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.ARCreditNoteId == creditNoteId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_NoteNotFoundFmt", creditNoteId));

            if (note.Status == CreditNoteStatus.Void) return;   // 冪等

            var wasPosted = note.Status == CreditNoteStatus.Posted;
            if (wasPosted)
            {
                await AssertPeriodOpenAsync(_dt.GetLocalDate(), note.PlantId, ct);

                var invoiceIds = note.Lines.Select(l => l.ARInvoiceId).Distinct().ToList();
                var invoices = await _db.ARInvoices.IgnoreQueryFilters()
                    .Where(i => invoiceIds.Contains(i.ARInvoiceId))
                    .ToDictionaryAsync(i => i.ARInvoiceId, ct);

                foreach (var g in note.Lines.GroupBy(l => l.ARInvoiceId))
                    if (invoices.TryGetValue(g.Key, out var inv))
                    {
                        inv.CreditedAmount -= g.Sum(l => l.LineAmount + l.TaxAmount);
                        inv.UpdatedAt = DateTime.UtcNow;
                    }
            }

            note.Status  = CreditNoteStatus.Void;
            note.Remarks = string.IsNullOrWhiteSpace(reason) ? note.Remarks : $"{note.Remarks}；作廢原因：{reason}";
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (wasPosted)
                await PostJournalAsync(note, reverse: true, await _currency.GetBaseCurrencyAsync() ?? "", ct);
        }

        /// <summary>
        /// 折讓分錄：借 銷貨收入(淨) ＋ 借 銷項稅額(稅) ／ 貸 應收帳款(含稅)。
        /// 作廢時整組對調。金額一律先算成本位幣再進分錄——分錄沒有幣別欄位。
        /// </summary>
        private async Task PostJournalAsync(ARCreditNote note, bool reverse, string baseCcy, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            var rate = note.ExchangeRate == 0m ? 1m : note.ExchangeRate;
            var netLocal = await _currency.RoundAsync(note.Lines.Sum(l => l.LineAmount) * rate, baseCcy);
            var taxLocal = await _currency.RoundAsync(note.Lines.Sum(l => l.TaxAmount) * rate, baseCcy);
            var grossLocal = netLocal + taxLocal;
            if (grossLocal <= 0m) return;

            var jLines = new List<Modules.ACT.Services.JournalLineRequest>
            {
                Leg(Modules.ACT.StandardAccounts.SalesRevenueCode,
                    Modules.ACT.StandardAccounts.SalesRevenueName, netLocal, debit: !reverse),
            };
            if (taxLocal > 0m)
                jLines.Add(Leg(Modules.ACT.StandardAccounts.OutputVatCode,
                    Modules.ACT.StandardAccounts.OutputVatName, taxLocal, debit: !reverse));
            jLines.Add(Leg(Modules.ACT.StandardAccounts.AccountsReceivableCode,
                Modules.ACT.StandardAccounts.AccountsReceivableName, grossLocal, debit: reverse));

            await _journalSvc.CreateAutoEntryAsync(
                CreditDocType, note.ARCreditNoteId,
                // 分錄日取廠別當地日：UTC 會讓 UTC+8 廠別在當地 1 號凌晨的動作落到上月最後一天，
                // 輕則撞期間鎖、重則記進已結帳月份（同 ReverseForReturnedQtyAsync 的理由）。
                reverse ? _dt.GetLocalDate() : note.NoteDate,
                note.PlantId ?? "", jLines, ct: ct);
        }

        private static Modules.ACT.Services.JournalLineRequest Leg(
            string code, string name, decimal amount, bool debit)
            => new(code, name, debit ? amount : 0m, debit ? 0m : amount);
    }
}
