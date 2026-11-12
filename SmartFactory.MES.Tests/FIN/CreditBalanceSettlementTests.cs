using SmartFactory.MES.Tests.Shared;
using Moq;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// 收付清之後才開折讓＝多收付（發票未結餘額為負）。沿用收款單／付款單沖銷：
/// 對負餘額發票填負數核銷——單獨一筆是退款，與別張發票的正數核銷同一張單是抵用。
/// </summary>
public class CreditBalanceSettlementTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly Today = new(2026, 1, 20);

    private readonly FinPaymentService       _svc;
    private readonly Mock<IJournalEntryService> _journal = new();
    private readonly List<IList<JournalLineRequest>> _posted = new();
    private int _seq;

    public CreditBalanceSettlementTests()
    {
        _context.CurrentPlantId = PlantId;
        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.FromResult($"DOC{++_seq:D6}"));
        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                .ReturnsAsync((decimal?)null);
        _journal.Setup(j => j.CreateAutoEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
                    It.IsAny<string>(), It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, DateOnly, string, IList<JournalLineRequest>, string?, CancellationToken>(
                    (_, _, _, _, lines, _, _) => _posted.Add(lines))
                .ReturnsAsync(new SmartFactory.MES.Modules.ACT.Models.JournalEntry());
        _svc = new FinPaymentService(_context, docNo.Object, dt.Object, currency.Object, journalSvc: _journal.Object);

        _context.BankAccounts.Add(new BankAccount { AccountId = "BK1", BankName = "B", AccountNo = "1", Balance = 1000m, PlantId = PlantId });
        _context.SaveChanges();
    }

    private ARInvoice SeedAr(string id, decimal total, decimal received, decimal credited, decimal rate = 1m, string? ccy = null)
    {
        var inv = new ARInvoice
        {
            ARInvoiceId = id, CustomerCode = "C1", InvoiceDate = Today, DueDate = Today.AddDays(30),
            TotalAmount = total, AmountLocal = total * rate, ExchangeRate = rate, CurrencyCode = ccy,
            ReceivedAmount = received, CreditedAmount = credited,
            Status = received + credited >= total ? ARInvoiceStatus.Received : received > 0 ? ARInvoiceStatus.PartialReceived : ARInvoiceStatus.Open,
            PlantId = PlantId,
        };
        _context.ARInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }

    private async Task<string> Receive(decimal amount, string? bank, decimal rate = 1m, string? ccy = null, params (string Inv, decimal Amt)[] allocs)
    {
        var rec = await _svc.CreateARReceiptAsync(new ARReceiptRequest("C1", Today, PaymentMethod.Wire, ccy, rate, amount, bank, null, null,
            allocs.Select(a => new PaymentAllocationLine(a.Inv, a.Amt)).ToList()), "U1", PlantId);
        await _svc.ConfirmARReceiptAsync(rec.ARReceiptId);
        return rec.ARReceiptId;
    }

    /// <summary>某次分錄逐科目「借−貸」，依科目排序。</summary>
    private static string Net(IList<JournalLineRequest> lines) => string.Join("|", lines
        .GroupBy(l => l.AccountCode).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => $"{g.Key}:{g.Sum(l => l.Debit - l.Credit):0.####}"));

    [Fact]
    public async Task 退款給客戶_負數核銷_銀行減少且借應收貸銀行()
    {
        SeedAr("AR-1", total: 1050m, received: 1050m, credited: 105m);   // 多收 105

        await Receive(-105m, "BK1", allocs: ("AR-1", -105m));

        var inv = _context.ARInvoices.Find("AR-1")!;
        Assert.Equal(945m, inv.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Received, inv.Status);
        Assert.Equal(895m, _context.BankAccounts.Find("BK1")!.Balance);
        Assert.Equal("1130:-105|1170:105", Net(Assert.Single(_posted)));
        Assert.All(_posted[0], l => Assert.True(l.Debit >= 0 && l.Credit >= 0, "分錄行不可有負數"));
    }

    [Fact]
    public async Task 抵用_多收款扣抵下一張發票_實收淨額()
    {
        SeedAr("AR-1", total: 1050m, received: 1050m, credited: 105m);
        SeedAr("AR-2", total: 1000m, received: 0m, credited: 0m);

        await Receive(895m, "BK1", allocs: new[] { ("AR-2", 1000m), ("AR-1", -105m) });

        Assert.Equal(ARInvoiceStatus.Received, _context.ARInvoices.Find("AR-2")!.Status);
        var old = _context.ARInvoices.Find("AR-1")!;
        Assert.Equal(0m, old.TotalAmount - old.ReceivedAmount - old.CreditedAmount);
        Assert.Equal(1895m, _context.BankAccounts.Find("BK1")!.Balance);
        Assert.Equal("1130:895|1170:-895", Net(Assert.Single(_posted)));
    }

    [Fact]
    public async Task 外幣退款_匯率上升_差額記匯兌損失且借貸平衡()
    {
        SeedAr("AR-FX", total: 100m, received: 100m, credited: 10m, rate: 30m, ccy: "USD");   // 多收 10 USD

        await Receive(-10m, "BK1", rate: 32m, ccy: "USD", allocs: ("AR-FX", -10m));

        // 退 10 USD × 32 = 320；應收以入帳匯率 30 沖回 300；差 20 是損失
        Assert.Equal("1130:-320|1170:300|5199:20", Net(Assert.Single(_posted)));
        Assert.Equal(_posted[0].Sum(l => l.Debit), _posted[0].Sum(l => l.Credit));
    }

    [Fact]
    public async Task 作廢退款單_發票回到多收狀態且仍是已收訖()
    {
        SeedAr("AR-1", total: 1050m, received: 1050m, credited: 105m);
        var id = await Receive(-105m, "BK1", allocs: ("AR-1", -105m));
        _journal.Setup(j => j.CreateAutoEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(),
                It.IsAny<string>(), It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartFactory.MES.Modules.ACT.Models.JournalEntry());

        await _svc.VoidARReceiptAsync(id);

        var inv = _context.ARInvoices.Find("AR-1")!;
        Assert.Equal(1050m, inv.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Received, inv.Status);   // 原本作廢一律判 Open／PartialReceived
        Assert.Equal(1000m, _context.BankAccounts.Find("BK1")!.Balance);
    }

    [Fact]
    public async Task 對沒有多收的發票填負數_擋下()
    {
        SeedAr("AR-OPEN", total: 1000m, received: 200m, credited: 0m);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Receive(-100m, null, allocs: ("AR-OPEN", -100m)));
        PzAssert.Message("Fin_NegativeAllocNoCreditFmt", ex.Message);
    }

    [Fact]
    public async Task 負數核銷超過多收餘額_擋下()
    {
        SeedAr("AR-1", total: 1050m, received: 1050m, credited: 105m);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Receive(-200m, null, allocs: ("AR-1", -200m)));
        PzAssert.Message("Fin_NegativeAllocExceedsCreditFmt", ex.Message);
    }

    [Fact]
    public async Task 退款金額超過銀行餘額_擋下且發票不動()
    {
        SeedAr("AR-1", total: 5000m, received: 5000m, credited: 2000m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Receive(-2000m, "BK1", allocs: ("AR-1", -2000m)));
        _context.ChangeTracker.Clear();
        Assert.Equal(5000m, _context.ARInvoices.Find("AR-1")!.ReceivedAmount);
        Assert.Empty(_posted);
    }

    /// <summary>原本不檢查：金額與核銷不等的單據確認時先存成已收、分錄再因借貸不平衡失敗，留下沒有分錄的已收款單。</summary>
    [Fact]
    public async Task 收款金額與核銷合計不符_建立時就擋下()
    {
        SeedAr("AR-2", total: 1000m, received: 0m, credited: 0m);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateARReceiptAsync(
            new ARReceiptRequest("C1", Today, PaymentMethod.Wire, null, 1m, 1200m, null, null, null,
                new List<PaymentAllocationLine> { new("AR-2", 1000m) }), "U1", PlantId));
        PzAssert.Message("Fin_AmountAllocationMismatchFmt", ex.Message);
    }

    [Fact]
    public async Task 供應商退款_負數核銷_銀行增加且借銀行貸應付()
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = "AP-1", SupplierCode = "S1", InvoiceDate = Today, DueDate = Today.AddDays(30),
            TotalAmount = 1050m, AmountLocal = 1050m, ExchangeRate = 1m, PaidAmount = 1050m, DebitedAmount = 105m,
            Status = APInvoiceStatus.Paid, PlantId = PlantId,
        });
        _context.SaveChanges();

        var pmt = await _svc.CreateAPPaymentAsync(new APPaymentRequest("S1", Today, PaymentMethod.Wire, null, 1m, -105m, "BK1", null, null,
            new List<PaymentAllocationLine> { new("AP-1", -105m) }), "U1", PlantId);
        await _svc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        var inv = _context.APInvoices.Find("AP-1")!;
        Assert.Equal(945m, inv.PaidAmount);
        Assert.Equal(APInvoiceStatus.Paid, inv.Status);
        Assert.Equal(1105m, _context.BankAccounts.Find("BK1")!.Balance);
        Assert.Equal("1130:105|2100:-105", Net(Assert.Single(_posted)));
    }
}
