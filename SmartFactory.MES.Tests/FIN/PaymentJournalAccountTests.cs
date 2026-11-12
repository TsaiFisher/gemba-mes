using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// 付款／收款自動日記帳的**科目逐項比對**（manual-test-plan.md 1-7、3-5、3-7）。
///
/// 為什麼要單獨一支：既有的 <c>Flows/P2PFlowTests</c>／<c>O2CFlowTests</c> 驗的是
/// 「整條鏈跑完 GL 仍一致」，也就是**借貸總額平衡**；平衡是必要條件不是充分條件——
/// 把應付帳款寫成應收帳款、或把匯兌利益寫進損失科目，總額照樣平衡，那類錯誤
/// 一路到月結看損益表才會現形。§12 的覆蓋對照把這一段標為 🟡，這裡補上。
///
/// 匯兌方向是本檔的重點。AP 與 AR 兩邊的判斷式**符號相反**：
///   AP：<c>gainLoss = 付款本幣 − 發票本幣</c>，付得比當初認列的少＝賺到，故 <b>負</b>值是利益；
///   AR：<c>gainLoss = 收款本幣 − 發票本幣</c>，收得比當初認列的多＝賺到，故 <b>正</b>值是利益。
/// 兩邊各測利益與損失一次，複製貼上時把符號帶錯就會紅。
/// </summary>
public class PaymentJournalAccountTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "S002";
    private const string CustCode = "C002";

    /// <summary>發票認列當時的匯率；付款／收款匯率高於它＝本幣付得／收得更多。</summary>
    private const decimal InvoiceRate = 30m;

    private static readonly DateOnly Today = new DateOnly(2026, 2, 1);

    private readonly FinPaymentService _paymentSvc;
    private readonly List<(string DocType, string DocId, IList<JournalLineRequest> Lines)> _journals = new();

    private int _pmtSeq;
    private int _recSeq;

    public PaymentJournalAccountTests()
    {
        _context.CurrentPlantId = PlantId;

        var sysParam = new Mock<ISystemParamService>();
        sysParam.Setup(x => x.GetAsync(ProjectZSystemParamKeys.BaseCurrency)).ReturnsAsync("TWD");
        var currencySvc = new CurrencyService(_context, sysParam.Object, new MemoryCache(new MemoryCacheOptions()));

        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("APPayment", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.FromResult($"APM{++_pmtSeq:D6}"));
        docNo.Setup(d => d.NextAsync("ARReceipt", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.FromResult($"ARR{++_recSeq:D6}"));

        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);

        // 分錄服務用 mock 攔下來，斷言的是「送進去的科目與金額」而不是落庫結果——
        // 落庫那一段已由 JournalEntryServiceTests 與 Flows 測過，這裡要驗的是呼叫端。
        var journalSvc = new Mock<IJournalEntryService>();
        journalSvc.Setup(j => j.CreateAutoEntryAsync(
                      It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
                      It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, DateOnly, string, IList<JournalLineRequest>, string?, CancellationToken>(
                      (docType, docId, _, _, lines, _, _) => _journals.Add((docType, docId, lines)))
                  .ReturnsAsync(new JournalEntry());

        _paymentSvc = new FinPaymentService(_context, docNo.Object, dt.Object, currencySvc,
            periodSvc: null, journalSvc: journalSvc.Object);

        SeedBaseData();
    }

    private void SeedBaseData()
    {
        _context.Currencies.AddRange(
            new Currency { CurrencyCode = "TWD", CurrencyName = "新台幣", Symbol = "NT$", DecimalPlaces = 0, IsActive = true },
            new Currency { CurrencyCode = "USD", CurrencyName = "美元",   Symbol = "$",   DecimalPlaces = 2, IsActive = true });
        _context.Companies.AddRange(
            new Company { CompanyCode = SuppCode, CompanyName = "供應商B", IsSupplier = true, IsActive = true },
            new Company { CompanyCode = CustCode, CompanyName = "客戶B",   IsCustomer = true, IsActive = true });
        _context.SaveChanges();
    }

    // ── 佈置 ────────────────────────────────────────────────────────────────

    private void SeedApInvoice(string id, decimal amount, string? currency, decimal rate)
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = id, SupplierCode = SuppCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            CurrencyCode = currency, ExchangeRate = rate,
            TotalAmount  = amount, AmountLocal = amount * rate,
            Status       = APInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    private void SeedArInvoice(string id, decimal amount, string? currency, decimal rate)
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId  = id, CustomerCode = CustCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            CurrencyCode = currency, ExchangeRate = rate,
            TotalAmount  = amount, AmountLocal = amount * rate,
            Status       = ARInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    private async Task PayAsync(string invoiceId, decimal amount, string? currency, decimal rate)
    {
        var pmt = await _paymentSvc.CreateAPPaymentAsync(new APPaymentRequest(
            SupplierCode: SuppCode, PaymentDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: currency, ExchangeRate: rate, Amount: amount,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new(invoiceId, amount) }), "U1", PlantId);

        await _paymentSvc.ConfirmAPPaymentAsync(pmt.APPaymentId);
    }

    private async Task ReceiveAsync(string invoiceId, decimal amount, string? currency, decimal rate)
    {
        var rec = await _paymentSvc.CreateARReceiptAsync(new ARReceiptRequest(
            CustomerCode: CustCode, ReceiptDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: currency, ExchangeRate: rate, Amount: amount,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new(invoiceId, amount) }), "U1", PlantId);

        await _paymentSvc.ConfirmARReceiptAsync(rec.ARReceiptId);
    }

    // ── 斷言工具 ────────────────────────────────────────────────────────────

    private IList<JournalLineRequest> SingleJournal(string expectedDocType)
    {
        var j = Assert.Single(_journals);
        Assert.Equal(expectedDocType, j.DocType);
        return j.Lines;
    }

    /// <summary>某科目的借方合計（找不到該科目回 0）。</summary>
    private static decimal Debit(IList<JournalLineRequest> lines, string account)
        => lines.Where(l => l.AccountCode == account).Sum(l => l.Debit);

    private static decimal Credit(IList<JournalLineRequest> lines, string account)
        => lines.Where(l => l.AccountCode == account).Sum(l => l.Credit);

    private static void AssertBalanced(IList<JournalLineRequest> lines)
        => Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));

    /// <summary>本張分錄只准出現這些科目——多寫一個科目就是錯，少寫也是。</summary>
    private static void AssertAccountsExactly(IList<JournalLineRequest> lines, params string[] accounts)
        => Assert.Equal(accounts.OrderBy(a => a), lines.Select(l => l.AccountCode).Distinct().OrderBy(a => a));

    // ── AP 付款 ─────────────────────────────────────────────────────────────

    /// <summary>1-7 本幣付款：借 應付帳款 2100 / 貸 銀行存款 1130，不得出現匯兌科目。</summary>
    [Fact]
    public async Task ApPayment_LocalCurrency_DebitsPayableCreditsBank_NoFxLine()
    {
        SeedApInvoice("API-L1", 5000m, currency: null, rate: 1m);

        await PayAsync("API-L1", 5000m, currency: null, rate: 1m);

        var lines = SingleJournal("APPayment");
        AssertAccountsExactly(lines, StandardAccounts.AccountsPayableCode, StandardAccounts.CashAndBankCode);
        Assert.Equal(5000m, Debit(lines,  StandardAccounts.AccountsPayableCode));
        Assert.Equal(5000m, Credit(lines, StandardAccounts.CashAndBankCode));
        AssertBalanced(lines);
    }

    /// <summary>
    /// 1-7／1-8 外幣付款、匯率升到 32（付出更多本幣）＝**匯兌損失**，借方 5199。
    /// 應付帳款腿記的是**發票當時**的本幣（100 × 30），不是付款當時的。
    /// </summary>
    [Fact]
    public async Task ApPayment_RateUp_RecordsFxLossOnDebitSide()
    {
        SeedApInvoice("API-F1", 100m, "USD", InvoiceRate);

        await PayAsync("API-F1", 100m, "USD", rate: 32m);

        var lines = SingleJournal("APPayment");
        AssertAccountsExactly(lines,
            StandardAccounts.AccountsPayableCode, StandardAccounts.CashAndBankCode, StandardAccounts.FxLossCode);
        Assert.Equal(3000m, Debit(lines,  StandardAccounts.AccountsPayableCode));  // 100 × 30
        Assert.Equal(3200m, Credit(lines, StandardAccounts.CashAndBankCode));      // 100 × 32
        Assert.Equal(200m,  Debit(lines,  StandardAccounts.FxLossCode));
        Assert.Equal(0m,    Credit(lines, StandardAccounts.FxGainCode));
        AssertBalanced(lines);
    }

    /// <summary>1-7／1-8 外幣付款、匯率跌到 28（付出較少本幣）＝**匯兌利益**，貸方 4190。</summary>
    [Fact]
    public async Task ApPayment_RateDown_RecordsFxGainOnCreditSide()
    {
        SeedApInvoice("API-F2", 100m, "USD", InvoiceRate);

        await PayAsync("API-F2", 100m, "USD", rate: 28m);

        var lines = SingleJournal("APPayment");
        AssertAccountsExactly(lines,
            StandardAccounts.AccountsPayableCode, StandardAccounts.CashAndBankCode, StandardAccounts.FxGainCode);
        Assert.Equal(3000m, Debit(lines,  StandardAccounts.AccountsPayableCode));
        Assert.Equal(2800m, Credit(lines, StandardAccounts.CashAndBankCode));
        Assert.Equal(200m,  Credit(lines, StandardAccounts.FxGainCode));
        Assert.Equal(0m,    Debit(lines,  StandardAccounts.FxLossCode));
        AssertBalanced(lines);
    }

    /// <summary>1-6 部分付款：分錄金額按**本次核銷額**走，不是發票全額。</summary>
    [Fact]
    public async Task ApPayment_Partial_JournalUsesAllocatedAmountNotInvoiceTotal()
    {
        SeedApInvoice("API-P1", 5000m, currency: null, rate: 1m);

        await PayAsync("API-P1", 2000m, currency: null, rate: 1m);

        var lines = SingleJournal("APPayment");
        Assert.Equal(2000m, Debit(lines,  StandardAccounts.AccountsPayableCode));
        Assert.Equal(2000m, Credit(lines, StandardAccounts.CashAndBankCode));
        AssertBalanced(lines);

        var inv = await _context.APInvoices.FindAsync("API-P1");
        Assert.Equal(APInvoiceStatus.PartialPaid, inv!.Status);
    }

    // ── AR 收款 ─────────────────────────────────────────────────────────────

    /// <summary>3-5 本幣收款：借 銀行存款 1130 / 貸 應收帳款 1170。方向與 AP 相反。</summary>
    [Fact]
    public async Task ArReceipt_LocalCurrency_DebitsBankCreditsReceivable_NoFxLine()
    {
        SeedArInvoice("ARI-L1", 5000m, currency: null, rate: 1m);

        await ReceiveAsync("ARI-L1", 5000m, currency: null, rate: 1m);

        var lines = SingleJournal("ARReceipt");
        AssertAccountsExactly(lines, StandardAccounts.CashAndBankCode, StandardAccounts.AccountsReceivableCode);
        Assert.Equal(5000m, Debit(lines,  StandardAccounts.CashAndBankCode));
        Assert.Equal(5000m, Credit(lines, StandardAccounts.AccountsReceivableCode));
        AssertBalanced(lines);
    }

    /// <summary>
    /// 3-5／3-7 外幣收款、匯率升到 32（收到更多本幣）＝**匯兌利益**。
    /// 與 <see cref="ApPayment_RateUp_RecordsFxLossOnDebitSide"/> 同樣是「匯率升」，
    /// 但一邊是損失、一邊是利益——這一對就是防符號抄錯的守門。
    /// </summary>
    [Fact]
    public async Task ArReceipt_RateUp_RecordsFxGainOnCreditSide()
    {
        SeedArInvoice("ARI-F1", 100m, "USD", InvoiceRate);

        await ReceiveAsync("ARI-F1", 100m, "USD", rate: 32m);

        var lines = SingleJournal("ARReceipt");
        AssertAccountsExactly(lines,
            StandardAccounts.CashAndBankCode, StandardAccounts.AccountsReceivableCode, StandardAccounts.FxGainCode);
        Assert.Equal(3200m, Debit(lines,  StandardAccounts.CashAndBankCode));            // 100 × 32
        Assert.Equal(3000m, Credit(lines, StandardAccounts.AccountsReceivableCode));     // 100 × 30
        Assert.Equal(200m,  Credit(lines, StandardAccounts.FxGainCode));
        Assert.Equal(0m,    Debit(lines,  StandardAccounts.FxLossCode));
        AssertBalanced(lines);
    }

    /// <summary>3-5／3-7 外幣收款、匯率跌到 28（收到較少本幣）＝**匯兌損失**，借方 5199。</summary>
    [Fact]
    public async Task ArReceipt_RateDown_RecordsFxLossOnDebitSide()
    {
        SeedArInvoice("ARI-F2", 100m, "USD", InvoiceRate);

        await ReceiveAsync("ARI-F2", 100m, "USD", rate: 28m);

        var lines = SingleJournal("ARReceipt");
        AssertAccountsExactly(lines,
            StandardAccounts.CashAndBankCode, StandardAccounts.AccountsReceivableCode, StandardAccounts.FxLossCode);
        Assert.Equal(2800m, Debit(lines,  StandardAccounts.CashAndBankCode));
        Assert.Equal(3000m, Credit(lines, StandardAccounts.AccountsReceivableCode));
        Assert.Equal(200m,  Debit(lines,  StandardAccounts.FxLossCode));
        Assert.Equal(0m,    Credit(lines, StandardAccounts.FxGainCode));
        AssertBalanced(lines);
    }

    /// <summary>3-4 部分收款：分錄按本次核銷額，發票轉 PartialReceived。</summary>
    [Fact]
    public async Task ArReceipt_Partial_JournalUsesAllocatedAmountNotInvoiceTotal()
    {
        SeedArInvoice("ARI-P1", 5000m, currency: null, rate: 1m);

        await ReceiveAsync("ARI-P1", 2000m, currency: null, rate: 1m);

        var lines = SingleJournal("ARReceipt");
        Assert.Equal(2000m, Debit(lines,  StandardAccounts.CashAndBankCode));
        Assert.Equal(2000m, Credit(lines, StandardAccounts.AccountsReceivableCode));
        AssertBalanced(lines);

        var inv = await _context.ARInvoices.FindAsync("ARI-P1");
        Assert.Equal(ARInvoiceStatus.PartialReceived, inv!.Status);
    }

    // ── 兩邊對照 ────────────────────────────────────────────────────────────

    /// <summary>
    /// AP 與 AR 不得共用同一組借貸方向。同一筆金額、同一個匯率變動下，
    /// 銀行存款在 AP 走貸方、在 AR 走借方；應付走借方、應收走貸方。
    /// 這支測試存在的意義是：把 AR 的分錄複製去改 AP（或反過來）而忘了對調時，
    /// 前面每一支單獨看都還可能矇混過去，這支一定紅。
    /// </summary>
    [Fact]
    public async Task PaymentAndReceipt_MirrorEachOther_OnBankAndSettlementAccounts()
    {
        SeedApInvoice("API-M1", 1000m, currency: null, rate: 1m);
        SeedArInvoice("ARI-M1", 1000m, currency: null, rate: 1m);

        await PayAsync("API-M1", 1000m, currency: null, rate: 1m);
        await ReceiveAsync("ARI-M1", 1000m, currency: null, rate: 1m);

        Assert.Equal(2, _journals.Count);
        var ap = _journals.Single(j => j.DocType == "APPayment").Lines;
        var ar = _journals.Single(j => j.DocType == "ARReceipt").Lines;

        // 銀行存款：付款走貸方、收款走借方
        Assert.Equal(1000m, Credit(ap, StandardAccounts.CashAndBankCode));
        Assert.Equal(0m,    Debit(ap,  StandardAccounts.CashAndBankCode));
        Assert.Equal(1000m, Debit(ar,  StandardAccounts.CashAndBankCode));
        Assert.Equal(0m,    Credit(ar, StandardAccounts.CashAndBankCode));

        // 沖銷腿：應付走借方、應收走貸方，且兩邊不得互相污染
        Assert.Equal(1000m, Debit(ap,  StandardAccounts.AccountsPayableCode));
        Assert.Equal(0m,    Debit(ap,  StandardAccounts.AccountsReceivableCode));
        Assert.Equal(1000m, Credit(ar, StandardAccounts.AccountsReceivableCode));
        Assert.Equal(0m,    Credit(ar, StandardAccounts.AccountsPayableCode));
    }
}
