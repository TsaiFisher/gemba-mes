using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public class FinPaymentService : IFinPaymentService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDocNumberService _docNo;
        private readonly IDateTimeService _dt;
        private readonly ICurrencyService _currency;

        private readonly Modules.ACT.Services.IAccountingPeriodService? _periodSvc;
        private readonly Modules.ACT.Services.IJournalEntryService?      _journalSvc;

        /// <summary>核銷判定尾差預設（§9 P-7 可經 fin_settle_tolerance 系統參數覆寫；
        /// 取 0.001 而非金額容忍值 0.01：核銷結清判定需緊一個量級，避免過度寬鬆結清）。</summary>
        public const decimal DefaultSettleTolerance = 0.001m;

        private readonly ISystemParamService? _sysParams;

        public FinPaymentService(ApplicationDbContext db, IDocNumberService docNo, IDateTimeService dt, ICurrencyService currency,
            Modules.ACT.Services.IAccountingPeriodService? periodSvc  = null,
            Modules.ACT.Services.IJournalEntryService?     journalSvc = null,
            ISystemParamService?                           sysParams  = null)
        {
            _db         = db;
            _docNo      = docNo;
            _dt         = dt;
            _currency   = currency;
            _periodSvc  = periodSvc;
            _journalSvc = journalSvc;
            _sysParams  = sysParams;
        }

        /// <summary>§9 P-7：核銷尾差取系統參數，未設定/不合法（非正數）時退回預設。</summary>
        private async Task<decimal> GetSettleToleranceAsync()
            => _sysParams != null
               && decimal.TryParse(await _sysParams.GetAsync(ProjectZSystemParamKeys.FinSettleTolerance), out var v)
               && v > 0
                ? v : DefaultSettleTolerance;

        /// <summary>
        /// 期間鎖倉前置檢查（roadmap-fin-cst-sub.md §8）；ACT 模組未啟用時略過。
        /// plantId 取**單據自己的廠別**，不是操作者當下切的廠。
        /// </summary>
        private Task AssertPeriodOpenAsync(DateOnly date, string? plantId, CancellationToken ct)
            => _periodSvc?.AssertCanPostAsync(date, plantId ?? string.Empty, ct) ?? Task.CompletedTask;

        /// <summary>
        /// 解析匯率：表單已由幣別維護預設帶入並允許使用者覆寫，故送入值（> 0）優先；
        /// 本幣或空 → 1；未帶值（≤ 0）才回退幣別維護 ≤ 單據日期最近一筆，仍無則 1。
        /// </summary>
        private async Task<decimal> ResolveRateAsync(string? currencyCode, DateOnly date, RateType rateType, decimal submitted)
        {
            if (string.IsNullOrEmpty(currencyCode)) return 1m;
            if (submitted > 0m) return submitted;
            return await _currency.GetRateAsync(currencyCode, date, rateType) ?? 1m;
        }

        // ── AP Payment ────────────────────────────────────────────────────────────

        public async Task<APPayment> CreateAPPaymentAsync(APPaymentRequest req, string userId, string plantId, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(req.CurrencyCode) && req.ExchangeRate < 0)
                throw new ArgumentOutOfRangeException(nameof(req.ExchangeRate), PzText.Get("Fin_RateNegativeFmt", req.ExchangeRate));

            // 付外幣給廠商 → Buy；表單預設帶入幣別維護匯率、可覆寫（送入值優先）
            var rate = await ResolveRateAsync(req.CurrencyCode, req.PaymentDate, RateType.Buy, req.ExchangeRate);
            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";

            var payment = new APPayment
            {
                APPaymentId   = await _docNo.NextAsync("APPayment", plantId, ct: ct),
                SupplierCode  = req.SupplierCode,
                PaymentDate   = req.PaymentDate,
                Method        = req.Method,
                CurrencyCode  = req.CurrencyCode,
                ExchangeRate  = rate,
                Amount        = req.Amount,
                AmountLocal   = await _currency.RoundAsync(req.Amount * rate, baseCurrency),
                BankAccountId = req.BankAccountId,
                Reference     = req.Reference,
                Remarks       = req.Remarks,
                Status        = APPaymentStatus.Draft,
                PlantId       = plantId,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            };

            foreach (var alloc in req.Allocations)
            {
                payment.Allocations.Add(new APPaymentAllocation
                {
                    APInvoiceId          = alloc.InvoiceId,
                    AllocatedAmount      = alloc.Amount,
                    AllocatedAmountLocal = await _currency.RoundAsync(alloc.Amount * rate, baseCurrency),
                    CreatedAt            = DateTime.UtcNow,
                });
            }

            AssertAmountMatchesAllocations(req.Amount, req.Allocations);
            _db.APPayments.Add(payment);
            await _db.SaveChangesAsync(ct);
            return payment;
        }

        public async Task ConfirmAPPaymentAsync(string paymentId, CancellationToken ct = default)
        {
            var pmt = await _db.APPayments
                .Include(p => p.Allocations)
                .FirstOrDefaultAsync(p => p.APPaymentId == paymentId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_PaymentNotFound"));

            if (pmt.Status == APPaymentStatus.Paid)
                throw new InvalidOperationException(PzText.Get("Fin_PaymentAlreadyConfirmed"));
            // 已作廢者不得再確認：否則核銷會被重覆套用一次（發票 PaidAmount 重複加、
            // 銀行餘額重複扣），而作廢已把這些反沖過了。
            if (pmt.Status == APPaymentStatus.Void)
                throw new InvalidOperationException(PzText.Get("Fin_VoidedPaymentCannotConfirm"));

            await AssertPeriodOpenAsync(pmt.PaymentDate, pmt.PlantId, ct);
            AssertAmountMatchesAllocations(pmt.Amount, pmt.Allocations.Select(a => new PaymentAllocationLine(a.APInvoiceId, a.AllocatedAmount)).ToList());

            // 驗證核銷金額 ≤ 各 Invoice 未結餘額
            foreach (var alloc in pmt.Allocations)
            {
                var inv = await _db.APInvoices.FindAsync(new object[] { alloc.APInvoiceId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFoundFmt", alloc.APInvoiceId));
                // 作廢發票的 TotalAmount 不歸零，未結餘額照算得出正數——不擋的話錢付出去、應付被沖成借餘，
                // 而那張發票已經不在任何人的待付清單上（2026-10-22 隨機序列測試發現）
                if (inv.Status == APInvoiceStatus.Void)
                    throw new InvalidOperationException(PzText.Get("Fin_VoidedInvoiceCannotSettleFmt", inv.APInvoiceId));
                // 未結＝總額 − 已付 − 已折讓（折讓單只累加 DebitedAmount、不動 TotalAmount，見 APDebitNoteService）
                var outstanding = inv.TotalAmount - inv.PaidAmount - inv.DebitedAmount;
                AssertAllocationWithinOutstanding(inv.APInvoiceId, alloc.AllocatedAmount, outstanding, await GetSettleToleranceAsync());
            }

            // 更新 BankAccount 餘額（xmin 樂觀鎖）
            if (!string.IsNullOrEmpty(pmt.BankAccountId))
            {
                var account = await _db.BankAccounts.FindAsync(new object[] { pmt.BankAccountId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Fin_BankAccountNotFound"));
                if (account.Balance < pmt.AmountLocal)
                    throw new InvalidOperationException(PzText.Get("Fin_BankBalanceInsufficient"));
                account.Balance   -= pmt.AmountLocal;
                account.UpdatedAt  = DateTime.UtcNow;
            }

            // 核銷 Invoice，計算匯兌損益
            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
            decimal sumInvoiceLocal = 0m;
            foreach (var alloc in pmt.Allocations)
            {
                var inv = await _db.APInvoices.FindAsync(new object[] { alloc.APInvoiceId }, ct)!;
                inv.PaidAmount += alloc.AllocatedAmount;

                // 已實現匯兌損益 = 付款本幣 − 發票本幣按比例（合計於下方以 AmountLocal − 沖帳本幣計）
                var invoiceLocalShare = alloc.AllocatedAmount * inv.ExchangeRate;
                sumInvoiceLocal += invoiceLocalShare;

                // 更新 Invoice Status
                inv.Status = inv.PaidAmount + inv.DebitedAmount >= inv.TotalAmount - await GetSettleToleranceAsync()
                    ? APInvoiceStatus.Paid
                    : APInvoiceStatus.PartialPaid;
                inv.UpdatedAt = DateTime.UtcNow;
            }

            pmt.ExchangeGainLoss = pmt.AmountLocal - await _currency.RoundAsync(sumInvoiceLocal, baseCurrency);
            pmt.Status    = APPaymentStatus.Paid;
            pmt.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 自動日記帳：借 應付帳款 / 貸 銀行存款 / 匯兌損益
            if (_journalSvc != null)
            {
                // 金額可正可負（供應商退款／以負餘額抵用），一律以「借方為正」的淨額落借或貸。
                // 匯兌差取「現金本幣 − 沖帳本幣」而不是另行四捨五入的合計，借貸因此恆平。
                var apDebit  = await _currency.RoundAsync(sumInvoiceLocal, baseCurrency);
                var fxAmount = pmt.AmountLocal - apDebit;   // 正＝多付本幣＝損失
                var jLines   = new List<ACT.Services.JournalLineRequest>();
                AddSigned(jLines, StandardAccounts.AccountsPayableCode, StandardAccounts.AccountsPayableName, apDebit);
                AddSigned(jLines, StandardAccounts.CashAndBankCode,     StandardAccounts.CashAndBankName,     -pmt.AmountLocal);
                if (fxAmount < 0m)      // 匯兌利益 → 貸方
                    jLines.Add(new(StandardAccounts.FxGainCode, StandardAccounts.FxGainName, 0m, -fxAmount));
                else if (fxAmount > 0m) // 匯兌損失 → 借方
                    jLines.Add(new(StandardAccounts.FxLossCode, StandardAccounts.FxLossName, fxAmount, 0m));

                await _journalSvc.CreateAutoEntryAsync(
                    ApPaymentDocType, pmt.APPaymentId, pmt.PaymentDate, pmt.PlantId!, jLines, ct: ct);
            }
        }

        public async Task VoidAPPaymentAsync(string paymentId, CancellationToken ct = default)
        {
            var pmt = await _db.APPayments
                .Include(p => p.Allocations)
                .FirstOrDefaultAsync(p => p.APPaymentId == paymentId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_PaymentNotFound"));

            if (pmt.Status == APPaymentStatus.Draft || pmt.Status == APPaymentStatus.Void)
            {
                pmt.Status    = APPaymentStatus.Void;
                pmt.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }

            if (pmt.Status != APPaymentStatus.Paid)
                throw new InvalidOperationException(PzText.Get("Fin_OnlyPaidCanVoid"));

            await AssertPeriodOpenAsync(_dt.GetLocalDate(), pmt.PlantId, ct);   // 作廢沖銷以當下日期入帳

            // 多付後已退回（負核銷）的發票，再作廢原付款會讓已付變負數。原本以 Math.Max(0, …) 夾掉，
            // 那筆退回款就從子帳消失、總帳卻還記著（2026-10-22 隨機序列發現）。要先作廢退回那張。
            foreach (var alloc in pmt.Allocations.Where(a => a.AllocatedAmount > 0m))
            {
                var inv = await _db.APInvoices.FindAsync(new object[] { alloc.APInvoiceId }, ct);
                if (inv != null && inv.PaidAmount - alloc.AllocatedAmount < -await GetSettleToleranceAsync())
                    throw new InvalidOperationException(PzText.Get("Fin_VoidPaymentRefundFirstFmt", inv.APInvoiceId));
            }

            // 反沖：回補 Invoice PaidAmount
            foreach (var alloc in pmt.Allocations)
            {
                var inv = await _db.APInvoices.FindAsync(new object[] { alloc.APInvoiceId }, ct);
                if (inv == null) continue;
                inv.PaidAmount -= alloc.AllocatedAmount;
                var tol = await GetSettleToleranceAsync();
                // 作廢的可能是負核銷（退款／抵用）：回補後發票可能仍是付清的，狀態要跟確認時同一套判定
                inv.Status     = inv.PaidAmount + inv.DebitedAmount >= inv.TotalAmount - tol ? APInvoiceStatus.Paid
                               : inv.PaidAmount <= tol ? APInvoiceStatus.Open : APInvoiceStatus.PartialPaid;
                inv.UpdatedAt  = DateTime.UtcNow;
            }

            // 反沖 BankAccount 餘額
            if (!string.IsNullOrEmpty(pmt.BankAccountId))
            {
                var account = await _db.BankAccounts.FindAsync(new object[] { pmt.BankAccountId }, ct);
                if (account != null)
                {
                    account.Balance   += pmt.AmountLocal;
                    account.UpdatedAt  = DateTime.UtcNow;
                }
            }

            pmt.Status    = APPaymentStatus.Void;
            pmt.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await ReverseJournalAsync(ApPaymentDocType, ApPaymentVoidDocType,
                pmt.APPaymentId, pmt.PlantId ?? "", ct);
        }

        // ── AR Receipt ────────────────────────────────────────────────────────────

        public async Task<ARReceipt> CreateARReceiptAsync(ARReceiptRequest req, string userId, string plantId, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(req.CurrencyCode) && req.ExchangeRate < 0)
                throw new ArgumentOutOfRangeException(nameof(req.ExchangeRate), PzText.Get("Fin_RateNegativeFmt", req.ExchangeRate));

            // 收外幣自客戶 → Sell；表單預設帶入幣別維護匯率、可覆寫（送入值優先）
            var rate = await ResolveRateAsync(req.CurrencyCode, req.ReceiptDate, RateType.Sell, req.ExchangeRate);
            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";

            var receipt = new ARReceipt
            {
                ARReceiptId   = await _docNo.NextAsync("ARReceipt", plantId, ct: ct),
                CustomerCode  = req.CustomerCode,
                ReceiptDate   = req.ReceiptDate,
                Method        = req.Method,
                CurrencyCode  = req.CurrencyCode,
                ExchangeRate  = rate,
                Amount        = req.Amount,
                AmountLocal   = await _currency.RoundAsync(req.Amount * rate, baseCurrency),
                BankAccountId = req.BankAccountId,
                Reference     = req.Reference,
                Remarks       = req.Remarks,
                Status        = ARReceiptStatus.Draft,
                PlantId       = plantId,
                CreatedAt     = DateTime.UtcNow,
                UpdatedAt     = DateTime.UtcNow,
            };

            foreach (var alloc in req.Allocations)
            {
                receipt.Allocations.Add(new ARReceiptAllocation
                {
                    ARInvoiceId          = alloc.InvoiceId,
                    AllocatedAmount      = alloc.Amount,
                    AllocatedAmountLocal = await _currency.RoundAsync(alloc.Amount * rate, baseCurrency),
                    CreatedAt            = DateTime.UtcNow,
                });
            }

            AssertAmountMatchesAllocations(req.Amount, req.Allocations);
            _db.ARReceipts.Add(receipt);
            await _db.SaveChangesAsync(ct);
            return receipt;
        }

        public async Task ConfirmARReceiptAsync(string receiptId, CancellationToken ct = default)
        {
            var rec = await _db.ARReceipts
                .Include(r => r.Allocations)
                .FirstOrDefaultAsync(r => r.ARReceiptId == receiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_ReceiptNotFound"));

            if (rec.Status == ARReceiptStatus.Received)
                throw new InvalidOperationException(PzText.Get("Fin_ReceiptAlreadyConfirmed"));
            // 已作廢者不得再確認（理由同 ConfirmAPPaymentAsync）。
            if (rec.Status == ARReceiptStatus.Void)
                throw new InvalidOperationException(PzText.Get("Fin_VoidedReceiptCannotConfirm"));

            await AssertPeriodOpenAsync(rec.ReceiptDate, rec.PlantId, ct);
            AssertAmountMatchesAllocations(rec.Amount, rec.Allocations.Select(a => new PaymentAllocationLine(a.ARInvoiceId, a.AllocatedAmount)).ToList());

            foreach (var alloc in rec.Allocations)
            {
                var inv = await _db.ARInvoices.FindAsync(new object[] { alloc.ARInvoiceId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Fin_InvoiceNotFoundFmt", alloc.ARInvoiceId));
                // 同 AP：作廢發票不得再收款
                if (inv.Status == ARInvoiceStatus.Void)
                    throw new InvalidOperationException(PzText.Get("Fin_VoidedInvoiceCannotSettleFmt", inv.ARInvoiceId));
                // 未結＝總額 − 已收 − 已折讓（折讓單只累加 CreditedAmount、不動 TotalAmount，見 ARCreditNoteService）
                var outstanding = inv.TotalAmount - inv.ReceivedAmount - inv.CreditedAmount;
                AssertAllocationWithinOutstanding(inv.ARInvoiceId, alloc.AllocatedAmount, outstanding, await GetSettleToleranceAsync());
            }

            if (!string.IsNullOrEmpty(rec.BankAccountId))
            {
                var account = await _db.BankAccounts.FindAsync(new object[] { rec.BankAccountId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Fin_BankAccountNotFound"));
                if (rec.AmountLocal < 0m && account.Balance < -rec.AmountLocal)
                    throw new InvalidOperationException(PzText.Get("Fin_BankBalanceInsufficientRefund"));
                account.Balance   += rec.AmountLocal;
                account.UpdatedAt  = DateTime.UtcNow;
            }

            var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";
            decimal sumInvoiceLocal = 0m;
            foreach (var alloc in rec.Allocations)
            {
                var inv = await _db.ARInvoices.FindAsync(new object[] { alloc.ARInvoiceId }, ct)!;
                inv.ReceivedAmount += alloc.AllocatedAmount;

                var invoiceLocalShare = alloc.AllocatedAmount * inv.ExchangeRate;
                sumInvoiceLocal += invoiceLocalShare;

                inv.Status = inv.ReceivedAmount + inv.CreditedAmount >= inv.TotalAmount - await GetSettleToleranceAsync()
                    ? ARInvoiceStatus.Received
                    : ARInvoiceStatus.PartialReceived;
                inv.UpdatedAt = DateTime.UtcNow;
            }

            rec.ExchangeGainLoss = rec.AmountLocal - await _currency.RoundAsync(sumInvoiceLocal, baseCurrency);
            rec.Status    = ARReceiptStatus.Received;
            rec.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            // 自動日記帳：借 銀行存款 / 貸 應收帳款 / 匯兌損益
            if (_journalSvc != null)
            {
                // 金額可正可負（退款給客戶／以多付款抵用），一律以「借方為正」的淨額落借或貸。
                // 匯兌差取「現金本幣 − 沖帳本幣」，借貸因此恆平。
                var arCredit = await _currency.RoundAsync(sumInvoiceLocal, baseCurrency);
                var fxAmount = rec.AmountLocal - arCredit;   // 正＝多收本幣＝利益
                var jLines   = new List<ACT.Services.JournalLineRequest>();
                AddSigned(jLines, StandardAccounts.CashAndBankCode,        StandardAccounts.CashAndBankName,        rec.AmountLocal);
                AddSigned(jLines, StandardAccounts.AccountsReceivableCode, StandardAccounts.AccountsReceivableName, -arCredit);
                if (fxAmount > 0m)      // 匯兌利益 → 貸方
                    jLines.Add(new(StandardAccounts.FxGainCode, StandardAccounts.FxGainName, 0m, fxAmount));
                else if (fxAmount < 0m) // 匯兌損失 → 借方
                    jLines.Add(new(StandardAccounts.FxLossCode, StandardAccounts.FxLossName, -fxAmount, 0m));

                await _journalSvc.CreateAutoEntryAsync(
                    ArReceiptDocType, rec.ARReceiptId, rec.ReceiptDate, rec.PlantId!, jLines, ct: ct);
            }
        }

        public async Task VoidARReceiptAsync(string receiptId, CancellationToken ct = default)
        {
            var rec = await _db.ARReceipts
                .Include(r => r.Allocations)
                .FirstOrDefaultAsync(r => r.ARReceiptId == receiptId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Fin_ReceiptNotFound"));

            if (rec.Status == ARReceiptStatus.Draft || rec.Status == ARReceiptStatus.Void)
            {
                rec.Status    = ARReceiptStatus.Void;
                rec.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }

            // AP 側有這道守門，AR 側原本沒有——於是 Pending／Approved 會直接掉進下面的
            // 回沖分支，把「從來沒入過帳」的金額從銀行餘額扣掉，並開出一張沒有原分錄
            // 可對的回沖傳票。這兩個狀態目前不可達（收款單沒有送審路徑，實際只會出現
            // Draft／Received／Void），所以是潛在而非現行缺陷，但兩側語意本來就該一致。
            if (rec.Status != ARReceiptStatus.Received)
                throw new InvalidOperationException(PzText.Get("Fin_OnlyReceivedCanVoid"));

            await AssertPeriodOpenAsync(_dt.GetLocalDate(), rec.PlantId, ct);   // 作廢沖銷以當下日期入帳

            // 同 AP：多收後已退款的發票，要先作廢退款那張，否則已收會被夾成 0、退款從子帳消失
            foreach (var alloc in rec.Allocations.Where(a => a.AllocatedAmount > 0m))
            {
                var inv = await _db.ARInvoices.FindAsync(new object[] { alloc.ARInvoiceId }, ct);
                if (inv != null && inv.ReceivedAmount - alloc.AllocatedAmount < -await GetSettleToleranceAsync())
                    throw new InvalidOperationException(PzText.Get("Fin_VoidReceiptRefundFirstFmt", inv.ARInvoiceId));
            }

            foreach (var alloc in rec.Allocations)
            {
                var inv = await _db.ARInvoices.FindAsync(new object[] { alloc.ARInvoiceId }, ct);
                if (inv == null) continue;
                inv.ReceivedAmount -= alloc.AllocatedAmount;
                var tol = await GetSettleToleranceAsync();
                // 作廢的可能是負核銷（退款／抵用）：回補後發票可能仍是收訖的，狀態要跟確認時同一套判定
                inv.Status         = inv.ReceivedAmount + inv.CreditedAmount >= inv.TotalAmount - tol ? ARInvoiceStatus.Received
                                   : inv.ReceivedAmount <= tol ? ARInvoiceStatus.Open : ARInvoiceStatus.PartialReceived;
                inv.UpdatedAt      = DateTime.UtcNow;
            }

            if (!string.IsNullOrEmpty(rec.BankAccountId))
            {
                var account = await _db.BankAccounts.FindAsync(new object[] { rec.BankAccountId }, ct);
                if (account != null)
                {
                    account.Balance   -= rec.AmountLocal;
                    account.UpdatedAt  = DateTime.UtcNow;
                }
            }

            rec.Status    = ARReceiptStatus.Void;
            rec.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await ReverseJournalAsync(ArReceiptDocType, ArReceiptVoidDocType,
                rec.ARReceiptId, rec.PlantId ?? "", ct);
        }

        // ── 核銷驗證與分錄輔助 ────────────────────────────────────────────────────

        /// <summary>
        /// 收付款金額必須等於核銷合計。分錄只有「銀行／應收(應付)／匯兌損益」三種科目，
        /// 沒有預收預付科目可以放未核銷的差額——原本不檢查，金額與核銷不等的單據在確認時
        /// 先被存成已收付、接著分錄因借貸不平衡失敗，留下一張沒有分錄的已收付單。
        /// </summary>
        private static void AssertAmountMatchesAllocations(decimal amount, IList<PaymentAllocationLine> allocations)
        {
            if (allocations.Count == 0)
                throw new InvalidOperationException(PzText.Get("Fin_AllocationRequired"));
            var sum = allocations.Sum(a => a.Amount);
            if (amount != sum)
                throw new InvalidOperationException(PzText.Get("Fin_AmountAllocationMismatchFmt", amount, sum));
        }

        /// <summary>
        /// 核銷金額要落在 0 與該發票未結餘額之間（同號）：
        /// 正餘額收付正數；負餘額（收付清後才開折讓＝多收付）只能填負數，用於退款或抵用。
        /// 對正餘額填負數會把已收付的應收應付變回未收付，對負餘額填正數會讓多收付變更多，兩者都擋。
        /// </summary>
        private static void AssertAllocationWithinOutstanding(string invoiceId, decimal allocated, decimal outstanding, decimal tol)
        {
            if (allocated == 0m)
                throw new InvalidOperationException(PzText.Get("Fin_AllocationZeroFmt", invoiceId));
            if (allocated > 0m && allocated > outstanding + tol)
                throw new InvalidOperationException(PzText.Get("Fin_AllocationExceedsOutstandingFmt", invoiceId, allocated, outstanding));
            if (allocated < 0m && (outstanding >= 0m || allocated < outstanding - tol))
                throw new InvalidOperationException(outstanding >= 0m
                    ? PzText.Get("Fin_NegativeAllocNoCreditFmt", invoiceId)
                    : PzText.Get("Fin_NegativeAllocExceedsCreditFmt", invoiceId, allocated, outstanding));
        }

        /// <summary>以「借方為正」的淨額加一行：正數落借方、負數落貸方、零不加。</summary>
        private static void AddSigned(List<ACT.Services.JournalLineRequest> lines, string code, string name, decimal debitPositive)
        {
            if (debitPositive > 0m) lines.Add(new(code, name, debitPositive, 0m));
            else if (debitPositive < 0m) lines.Add(new(code, name, 0m, -debitPositive));
        }

        // ── 作廢回沖 ──────────────────────────────────────────────────────────────

        private const string ApPaymentDocType     = "APPayment";
        private const string ApPaymentVoidDocType = "APPayment-Void";
        private const string ArReceiptDocType     = "ARReceipt";
        private const string ArReceiptVoidDocType = "ARReceipt-Void";

        /// <summary>
        /// 付款／收款單作廢的總帳回沖。
        ///
        /// 原本作廢只做了三件事：回補發票的已核銷金額、回補銀行帳戶餘額、把單據標成 Void，
        /// **總帳完全沒動**。於是確認時那張「借 應付帳款／貸 銀行存款」永遠留在帳上：
        /// 子帳說錢沒付、負債還在，總帳說錢付了、負債沒了，兩邊從此各說各話而且不會收斂。
        /// 銀行帳戶餘額被回補這件事更讓它難查——現金對得起來，只有總帳的 1130 對不起來。
        ///
        /// 回沖以「原分錄借貸對調」產生，並以 -Void 這個來源類別記錄，
        /// 既與 APInvoice-Void／INV-Void 的口徑一致，也不會撞到重複立帳稽核
        /// （那支只看 1:1 的來源類別，-Void 不在其列）。
        /// </summary>
        private async Task ReverseJournalAsync(
            string sourceDocType, string voidDocType, string docId, string plantId, CancellationToken ct)
        {
            if (_journalSvc == null) return;

            var alreadyReversed = await _db.JournalEntries.IgnoreQueryFilters()
                .AnyAsync(j => j.SourceDocType == voidDocType && j.SourceDocId == docId, ct);
            if (alreadyReversed) return;

            var originalLines = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.SourceDocType == sourceDocType && j.SourceDocId == docId)
                .SelectMany(j => j.Lines)
                .ToListAsync(ct);
            if (originalLines.Count == 0) return;

            var reversal = originalLines
                .Select(l => new ACT.Services.JournalLineRequest(
                    l.AccountCode, l.AccountName,
                    Debit: l.Credit, Credit: l.Debit,
                    CurrencyCode: l.CurrencyCode, Amount: l.Amount,
                    ExchangeRate: l.ExchangeRate, AmountLocal: l.AmountLocal,
                    CostCenter: l.CostCenter, SourceRef: l.SourceRef))
                .ToList();

            await _journalSvc.CreateAutoEntryAsync(
                voidDocType, docId, _dt.GetLocalDate(), plantId, reversal, ct: ct);
        }
    }
}
