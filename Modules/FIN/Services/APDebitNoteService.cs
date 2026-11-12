using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    /// <summary>
    /// 進貨折讓單服務（AP 側）。與 <see cref="APDebitNoteService"/> 逐段對稱，
    /// 設計理由不重複；只記兩處**方向相反**的地方：
    ///
    /// **一、分錄的借貸相反。** AP 既有回沖是
    /// 借 應付帳款(含稅) ／ 貸 進貨暫估應付(淨) ＋ 貸 進項稅額(稅)，折讓單產生同一組。
    /// 特別注意貸方是 **2152 進貨暫估應付**而不是存貨——退貨過帳當下就已經
    /// 貸過存貨、借過 2152 了，這裡再借一次存貨會讓存貨被沖兩次。
    ///
    /// **二、上限看的是「已認列量」。** AP 是按已收量分次認列的，
    /// 發票明細的 Qty 未必等於進貨量，故可折讓量一律以 <c>apLine.Qty</c> 為準。
    /// </summary>
    public class APDebitNoteService : IAPDebitNoteService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currency;
        private readonly Modules.ACT.Services.IAccountingPeriodService? _periodSvc;
        private readonly Modules.ACT.Services.IJournalEntryService? _journalSvc;

        private const string DebitDocType = "APDebitNote";

        public APDebitNoteService(
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

        public async Task<APDebitNote?> CreateDraftFromGoodsReturnAsync(
            string goodsReturnId, CancellationToken ct = default)
        {
            // 冪等前置：唯一索引是最後一道，這裡先查是為了讓重試拿到同一張而不是撞 23505。
            var existing = await _db.APDebitNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.SourceDocType == "GoodsReturn" && n.SourceDocId == goodsReturnId, ct);
            if (existing != null) return existing;

            var ret = await _db.GoodsReturns.IgnoreQueryFilters()
                .Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.GoodsReturnId == goodsReturnId, ct);
            if (ret == null) return null;

            var grIds = ret.Lines.Where(l => l.GoodsReceiptId != null)
                .Select(l => l.GoodsReceiptId!).Distinct().ToList();
            if (grIds.Count == 0) return null;

            var invoices = await _db.APInvoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Where(i => i.SourceDocType == "GoodsReceipt" && grIds.Contains(i.SourceDocId!)
                         && i.Status != APInvoiceStatus.Void)   // 作廢舊單與重開新單並存時只看新單
                .ToListAsync(ct);
            if (invoices.Count == 0) return null;

            // 已折讓過的量（跨所有未作廢的折讓單）——部分退貨分多次來時不能重複折。
            var invoiceIds = invoices.Select(i => i.APInvoiceId).ToList();
            var alreadyCredited = await _db.APDebitNoteLines.IgnoreQueryFilters()
                .Where(l => invoiceIds.Contains(l.APInvoiceId)
                         && l.APDebitNote!.Status != CreditNoteStatus.Void)
                .GroupBy(l => new { l.APInvoiceId, l.InvoiceLineNo })
                .Select(g => new { g.Key.APInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                .ToListAsync(ct);

            var note = new APDebitNote
            {
                SupplierCode  = ret.SupplierCode,
                NoteDate      = ret.ReturnDate,
                SourceDocType = "GoodsReturn",
                SourceDocId   = goodsReturnId,
                PlantId       = ret.PlantId,
                CreatedBy     = ret.CreatedBy,
                CreatedByRoleId = ret.CreatedByRoleId,
                Status        = CreditNoteStatus.Draft,
                Remarks       = $"進貨退出單 {goodsReturnId} 之已付款發票折讓",
            };

            var lineNo = 0;
            foreach (var rl in ret.Lines.OrderBy(l => l.LineNo))
            {
                if (rl.GoodsReceiptId == null || rl.ReceiptLineNo == null || rl.ReturnQty <= 0m) continue;

                var inv = invoices.FirstOrDefault(i => i.SourceDocId == rl.GoodsReceiptId);
                if (inv == null) continue;

                // 只處理「已結清／部分收款」的——Open 的那些 ReverseForReturnedQtyAsync
                // 當場就把發票金額改小了，再開一張折讓會沖兩次。
                if (inv.Status == APInvoiceStatus.Open || inv.Status == APInvoiceStatus.Void) continue;

                var apLine = inv.Lines.FirstOrDefault(l => l.SourceLineNo == rl.ReceiptLineNo.Value);
                if (apLine == null || apLine.Qty <= 0m) continue;

                var credited = alreadyCredited.FirstOrDefault(
                    c => c.APInvoiceId == inv.APInvoiceId && c.InvoiceLineNo == apLine.LineNo)?.Qty ?? 0m;
                var creditable = apLine.Qty - credited;
                if (creditable <= 0m) continue;

                var qty = Math.Min(rl.ReturnQty, creditable);
                var currency = inv.CurrencyCode ?? await _currency.GetBaseCurrencyAsync() ?? "";
                var lineAmt  = await _currency.RoundAsync(qty * apLine.UnitPrice, currency);
                var taxAmt   = await _currency.RoundAsync(lineAmt * apLine.TaxRate, currency);

                note.Lines.Add(new APDebitNoteLine
                {
                    LineNo        = ++lineNo,
                    APInvoiceId   = inv.APInvoiceId,
                    InvoiceLineNo = apLine.LineNo,
                    ProductCode   = rl.ProductCode,
                    Qty           = qty,
                    UomCode       = rl.UomCode,
                    UnitPrice     = apLine.UnitPrice,
                    LineAmount    = lineAmt,
                    TaxRate       = apLine.TaxRate,
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

            note.APDebitNoteId = await _docNo.NextAsync(DebitDocType, note.PlantId ?? "", ct: ct);
            foreach (var l in note.Lines) l.APDebitNoteId = note.APDebitNoteId;

            _db.APDebitNotes.Add(note);
            await _db.SaveChangesAsync(ct);
            return note;
        }

        public async Task ConfirmAsync(string creditNoteId, CancellationToken ct = default)
        {
            var note = await _db.APDebitNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.APDebitNoteId == creditNoteId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_NoteNotFoundFmt", creditNoteId));

            if (note.Status != CreditNoteStatus.Draft)
                throw new InvalidOperationException(PzText.Get("Fin_NoteNotDraftFmt", creditNoteId));

            await AssertPeriodOpenAsync(note.NoteDate, note.PlantId, ct);

            var invoiceIds = note.Lines.Select(l => l.APInvoiceId).Distinct().ToList();
            var invoices = await _db.APInvoices.IgnoreQueryFilters()
                .Where(i => invoiceIds.Contains(i.APInvoiceId))
                .ToDictionaryAsync(i => i.APInvoiceId, ct);

            var baseCcy = await _currency.GetBaseCurrencyAsync() ?? "";
            // 上限守的是**數量**，不是未結餘額。
            //
            // 起初寫成「折讓不得超過未結餘額」，被 ConfirmingTheCreditNote_ClosesTheGap（AR 側） 當場擋下：
            // 發票已全額付款時未結餘額是 0，而那正是這張單存在的情境——廠商付了錢又退貨，
            // 廠商本來就欠公司那筆錢。把它擋掉等於宣告「已付清的發票不能退貨」。
            // 折讓後應付出現借餘（廠商欠公司）是**正確的結果**，後續由廠商退款或抵下一張發票處理。
            //
            // 真正的上限是「折讓量不得超過發票上開過的量」——那也正是
            // RETURN_SUBLEDGER_UNREVERSED 在比的東西。草稿產生時已經上限過一次，
            // 這裡必須再驗一次：草稿到確認之間可能有另一張折讓單先過帳了。
            var noteInvIds = note.Lines.Select(l => l.APInvoiceId).Distinct().ToList();
            var postedQty = await _db.APDebitNoteLines.IgnoreQueryFilters()
                .Where(l => noteInvIds.Contains(l.APInvoiceId)
                         && l.APDebitNoteId != note.APDebitNoteId
                         && l.APDebitNote!.Status == CreditNoteStatus.Posted)
                .GroupBy(l => new { l.APInvoiceId, l.InvoiceLineNo })
                .Select(g => new { g.Key.APInvoiceId, g.Key.InvoiceLineNo, Qty = g.Sum(x => x.Qty) })
                .ToListAsync(ct);

            var invoiceLines = await _db.APInvoiceLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => noteInvIds.Contains(l.APInvoiceId))
                .ToListAsync(ct);

            foreach (var line in note.Lines)
            {
                var invLine = invoiceLines.FirstOrDefault(
                    l => l.APInvoiceId == line.APInvoiceId && l.LineNo == line.InvoiceLineNo)
                    ?? throw new InvalidOperationException(
                        PzText.Get("Fin_NoteLineInvoiceLineMissingFmt", creditNoteId, line.LineNo, line.APInvoiceId, line.InvoiceLineNo));

                var already = postedQty.FirstOrDefault(
                    q => q.APInvoiceId == line.APInvoiceId && q.InvoiceLineNo == line.InvoiceLineNo)?.Qty ?? 0m;
                if (already + line.Qty > invLine.Qty)
                    throw new InvalidOperationException(
                        PzText.Get("Fin_NoteQtyExceedsInvoiceFmt", already + line.Qty, line.APInvoiceId, line.InvoiceLineNo, invLine.Qty));
            }

            foreach (var g in note.Lines.GroupBy(l => l.APInvoiceId))
            {
                if (!invoices.TryGetValue(g.Key, out var inv))
                    throw new InvalidOperationException(PzText.Get("Fin_NoteInvoiceMissingFmt", creditNoteId, g.Key));
                // 同 AR：草稿開出後發票才被作廢，不得再確認
                if (inv.Status == APInvoiceStatus.Void)
                    throw new InvalidOperationException(PzText.Get("Fin_NoteInvoiceVoidedFmt", creditNoteId, g.Key));

                inv.DebitedAmount += g.Sum(l => l.LineAmount + l.TaxAmount);
                inv.UpdatedAt = DateTime.UtcNow;
            }


            note.Status = CreditNoteStatus.Posted;
            note.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await PostJournalAsync(note, reverse: false, baseCcy, ct);
        }

        public async Task VoidAsync(string creditNoteId, string? reason = null, CancellationToken ct = default)
        {
            var note = await _db.APDebitNotes.IgnoreQueryFilters()
                .Include(n => n.Lines)
                .FirstOrDefaultAsync(n => n.APDebitNoteId == creditNoteId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_NoteNotFoundFmt", creditNoteId));

            if (note.Status == CreditNoteStatus.Void) return;   // 冪等

            var wasPosted = note.Status == CreditNoteStatus.Posted;
            if (wasPosted)
            {
                await AssertPeriodOpenAsync(_dt.GetLocalDate(), note.PlantId, ct);

                var invoiceIds = note.Lines.Select(l => l.APInvoiceId).Distinct().ToList();
                var invoices = await _db.APInvoices.IgnoreQueryFilters()
                    .Where(i => invoiceIds.Contains(i.APInvoiceId))
                    .ToDictionaryAsync(i => i.APInvoiceId, ct);

                foreach (var g in note.Lines.GroupBy(l => l.APInvoiceId))
                    if (invoices.TryGetValue(g.Key, out var inv))
                    {
                        inv.DebitedAmount -= g.Sum(l => l.LineAmount + l.TaxAmount);
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
        /// 折讓分錄：借 應付帳款(含稅) ／ 貸 進貨暫估應付(淨) ＋ 貸 進項稅額(稅)。
        /// 作廢時整組對調。金額一律先算成本位幣再進分錄——分錄沒有幣別欄位。
        /// </summary>
        private async Task PostJournalAsync(APDebitNote note, bool reverse, string baseCcy, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            var rate = note.ExchangeRate == 0m ? 1m : note.ExchangeRate;
            var netLocal = await _currency.RoundAsync(note.Lines.Sum(l => l.LineAmount) * rate, baseCcy);
            var taxLocal = await _currency.RoundAsync(note.Lines.Sum(l => l.TaxAmount) * rate, baseCcy);
            var grossLocal = netLocal + taxLocal;
            if (grossLocal <= 0m) return;

            var jLines = new List<Modules.ACT.Services.JournalLineRequest>
            {
                Leg(Modules.ACT.StandardAccounts.AccountsPayableCode,
                    Modules.ACT.StandardAccounts.AccountsPayableName, grossLocal, debit: !reverse),
                Leg(Modules.ACT.StandardAccounts.GrIrAPClearingCode,
                    Modules.ACT.StandardAccounts.GrIrAPClearingName, netLocal, debit: reverse),
            };
            if (taxLocal > 0m)
                jLines.Add(Leg(Modules.ACT.StandardAccounts.InputVatCode,
                    Modules.ACT.StandardAccounts.InputVatName, taxLocal, debit: reverse));

            await _journalSvc.CreateAutoEntryAsync(
                DebitDocType, note.APDebitNoteId,
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
