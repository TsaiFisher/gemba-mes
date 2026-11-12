using Moq;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// FinPaymentService — AP Payment 付款業務規則測試。
/// P2PReconciliationTests 已覆蓋基本付款沖銷流程；
/// 本檔補強控制器層 Confirm/Void 狀態機與邊界條件。
/// </summary>
public class FinPaymentServiceAPTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";

    private readonly FinPaymentService       _svc;
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService>  _mockDt    = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private int _pmtSeq;

    public FinPaymentServiceAPTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("APPayment", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"APM{++_pmtSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        _svc = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);
    }

    // ── ConfirmAPPaymentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task Confirm_FullPayment_InvoiceStatusPaid()
    {
        var (invId, _) = SeedInvoice("API001", totalAmount: 6000m);
        var pmtId = await CreatePayment(invId, amount: 6000m);

        await _svc.ConfirmAPPaymentAsync(pmtId);

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.Paid, inv!.Status);
        Assert.Equal(6000m, inv.PaidAmount);
    }

    [Fact]
    public async Task Confirm_PartialPayment_InvoiceStatusPartialPaid()
    {
        var (invId, _) = SeedInvoice("API002", totalAmount: 10000m);
        var pmtId = await CreatePayment(invId, amount: 4000m);

        await _svc.ConfirmAPPaymentAsync(pmtId);

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.PartialPaid, inv!.Status);
        Assert.Equal(4000m, inv.PaidAmount);
    }

    [Fact]
    public async Task Confirm_AlreadyPaid_Throws()
    {
        var (invId, _) = SeedInvoice("API003", totalAmount: 5000m);
        var pmtId = await CreatePayment(invId, amount: 5000m);

        await _svc.ConfirmAPPaymentAsync(pmtId);  // 第一次：成功

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAPPaymentAsync(pmtId));  // 第二次：已付款拋例外
    }

    [Fact]
    public async Task Confirm_AllocationExceedsOutstanding_Throws()
    {
        var (invId, _) = SeedInvoice("API004", totalAmount: 3000m);
        var pmtId = await CreatePayment(invId, amount: 5000m);  // 超出發票金額

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAPPaymentAsync(pmtId));
    }

    /// <summary>
    /// 折讓單只累加 DebitedAmount、不動 TotalAmount——未結要扣折讓。原本核銷上限用 Total − Paid，
    /// 開過折讓的發票照原額付得出去（溢付），而且付清折讓後餘額也永遠不會變 Paid。
    /// </summary>
    [Fact]
    public async Task Confirm_AfterDebitNote_CannotPayTheCreditedPart()
    {
        var (invId, inv) = SeedInvoice("API201", totalAmount: 1050m);
        inv.DebitedAmount = 105m;
        _context.SaveChanges();

        var pmtId = await CreatePayment(invId, amount: 1050m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.ConfirmAPPaymentAsync(pmtId));
    }

    [Fact]
    public async Task Confirm_AfterDebitNote_PayingTheRemainderSettlesTheInvoice()
    {
        var (invId, inv) = SeedInvoice("API202", totalAmount: 1050m);
        inv.DebitedAmount = 105m;
        _context.SaveChanges();

        await _svc.ConfirmAPPaymentAsync(await CreatePayment(invId, amount: 945m));

        var after = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.Paid, after!.Status);
    }

    [Fact]
    public async Task Confirm_WithinTolerance_AllowsFullClear()
    {
        // BVA: 付款金額 = TotalAmount - 0.001（在 0.001 容忍範圍內） → 應標記 Paid
        var (invId, _) = SeedInvoice("API005", totalAmount: 5000m);
        var pmtId = await CreatePayment(invId, amount: 4999.999m);

        await _svc.ConfirmAPPaymentAsync(pmtId);

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.Paid, inv!.Status);
    }

    [Fact]
    public async Task Confirm_SettleToleranceParamOverride_Applied()
    {
        // §9 P-7：fin_settle_tolerance 系統參數覆寫核銷尾差。
        // 尾差 0.005 在預設 0.001 下判 PartialPaid；參數放寬為 0.01 後判 Paid。
        var mockParams = new Mock<ISystemParamService>();
        mockParams.Setup(p => p.GetAsync(SmartFactory.MES.Models.ProjectZSystemParamKeys.FinSettleTolerance))
            .ReturnsAsync("0.01");
        var svc = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
            sysParams: mockParams.Object);

        var (invId, _) = SeedInvoice("API101", totalAmount: 100m);
        var req = BuildPaymentRequest(invId, amount: 99.995m);
        var pmt = await svc.CreateAPPaymentAsync(req, "U1", PlantId);

        await svc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.Paid, inv!.Status);
    }

    [Fact]
    public async Task Confirm_SettleToleranceDefault_KeepsPartialPaid()
    {
        // 對照組：未設參數時尾差 0.005 超過預設 0.001 → PartialPaid（行為不變）
        var (invId, _) = SeedInvoice("API102", totalAmount: 100m);
        var pmtId = await CreatePayment(invId, amount: 99.995m);

        await _svc.ConfirmAPPaymentAsync(pmtId);

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.PartialPaid, inv!.Status);
    }

    // ── VoidAPPaymentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Void_DraftPayment_SetsVoidWithoutRollback()
    {
        var (invId, _) = SeedInvoice("API006", totalAmount: 2000m);
        var req   = BuildPaymentRequest(invId, amount: 2000m);
        var draft = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);
        // 未 Confirm，直接 Void

        await _svc.VoidAPPaymentAsync(draft.APPaymentId);

        var pmt = await _context.APPayments.FindAsync(draft.APPaymentId);
        Assert.Equal(APPaymentStatus.Void, pmt!.Status);
        // 發票 PaidAmount 不受影響
        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(0m, inv!.PaidAmount);
    }

    [Fact]
    public async Task Void_PaidPayment_RestoresPaidAmountAndStatus()
    {
        var (invId, _) = SeedInvoice("API007", totalAmount: 8000m);
        var pmtId = await CreatePayment(invId, amount: 3000m);
        await _svc.ConfirmAPPaymentAsync(pmtId);

        // 確認後 PaidAmount = 3000
        var beforeVoid = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(3000m, beforeVoid!.PaidAmount);

        await _svc.VoidAPPaymentAsync(pmtId);

        var afterVoid = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(0m,                    afterVoid!.PaidAmount);
        Assert.Equal(APInvoiceStatus.Open,  afterVoid.Status);
    }

    /// <summary>
    /// 已作廢的付款單不可再確認。原本 Confirm 只擋「已付款」，作廢後再確認會把核銷
    /// 重覆套用一次（發票 PaidAmount 重複加、銀行餘額重複扣），而作廢已經反沖過了。
    /// 畫面把確認鈕的條件放寬成「非已付款／非已作廢」之後，這個守門就是最後一道。
    /// </summary>
    [Fact]
    public async Task Confirm_VoidedPayment_Throws()
    {
        var (invId, _) = SeedInvoice("API008", totalAmount: 4000m);
        var pmtId = await CreatePayment(invId, amount: 4000m);
        await _svc.VoidAPPaymentAsync(pmtId);   // 草稿直接作廢

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmAPPaymentAsync(pmtId));

        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(0m, inv!.PaidAmount);
        Assert.Equal(APInvoiceStatus.Open, inv.Status);
    }

    /// <summary>
    /// 草稿付款單即可確認：全系統沒有任何地方會把付款單設為 Approved，
    /// 若確認的前提是 Approved，付款單將永遠無法核銷（畫面上確認鈕也曾因此永不渲染）。
    /// </summary>
    [Fact]
    public async Task Confirm_DraftPayment_IsAllowed()
    {
        var (invId, _) = SeedInvoice("API009", totalAmount: 1500m);
        var req   = BuildPaymentRequest(invId, amount: 1500m);
        var draft = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);
        Assert.Equal(APPaymentStatus.Draft, draft.Status);

        await _svc.ConfirmAPPaymentAsync(draft.APPaymentId);

        var pmt = await _context.APPayments.FindAsync(draft.APPaymentId);
        Assert.Equal(APPaymentStatus.Paid, pmt!.Status);
        var inv = await _context.APInvoices.FindAsync(invId);
        Assert.Equal(APInvoiceStatus.Paid, inv!.Status);
    }

    // ── 外幣匯率：表單預設帶入、可覆寫（送入值優先）+ 乘法方向 ─────────────

    [Fact]
    public async Task CreateAPPayment_SubmittedRate_OverridesMaster_AndMultiplies()
    {
        // 幣別維護 USD = 99；但表單送入（預設帶入或使用者覆寫）= 32 → 以送入值為準
        _mockCurrency
            .Setup(c => c.GetRateAsync("USD", It.IsAny<DateOnly>(), RateType.Buy))
            .ReturnsAsync(99m);

        var req = new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   DateOnly.FromDateTime(DateTime.UtcNow),
            Method:        PaymentMethod.Wire,
            CurrencyCode:  "USD",
            ExchangeRate:  32m,            // 表單帶入/覆寫值，優先採用
            Amount:        100m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("API-RATE", 100m) });

        var pmt = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);

        Assert.Equal(32m, pmt.ExchangeRate);          // 送入值優先
        Assert.Equal(3200m, pmt.AmountLocal);          // 本幣 = 100 × 32（乘法）
    }

    [Fact]
    public async Task CreateAPPayment_NoSubmittedRate_FallsBackToMaster()
    {
        // 表單未帶值（ExchangeRate ≤ 0）→ 回退幣別維護 = 32
        _mockCurrency
            .Setup(c => c.GetRateAsync("USD", It.IsAny<DateOnly>(), RateType.Buy))
            .ReturnsAsync(32m);

        var req = new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   DateOnly.FromDateTime(DateTime.UtcNow),
            Method:        PaymentMethod.Wire,
            CurrencyCode:  "USD",
            ExchangeRate:  0m,             // 未帶值
            Amount:        100m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("API-RATE", 100m) });

        var pmt = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);

        Assert.Equal(32m, pmt.ExchangeRate);          // 回退主檔
        Assert.Equal(3200m, pmt.AmountLocal);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private (string InvoiceId, APInvoice Invoice) SeedInvoice(string invoiceId, decimal totalAmount)
    {
        var inv = new APInvoice
        {
            APInvoiceId  = invoiceId,
            SupplierCode = SuppCode,
            InvoiceDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate      = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            TotalAmount  = totalAmount,
            AmountLocal  = totalAmount,
            Status       = APInvoiceStatus.Open,
            PlantId      = PlantId,
        };
        _context.APInvoices.Add(inv);
        _context.SaveChanges();
        return (invoiceId, inv);
    }

    private APPaymentRequest BuildPaymentRequest(string invoiceId, decimal amount)
        => new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   DateOnly.FromDateTime(DateTime.UtcNow),
            Method:        PaymentMethod.Wire,
            CurrencyCode:  null,
            ExchangeRate:  1m,
            Amount:        amount,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations: new List<PaymentAllocationLine>
            {
                new PaymentAllocationLine(invoiceId, amount),
            });

    private async Task<string> CreatePayment(string invoiceId, decimal amount)
    {
        var req = BuildPaymentRequest(invoiceId, amount);
        var pmt = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);
        return pmt.APPaymentId;
    }
}
