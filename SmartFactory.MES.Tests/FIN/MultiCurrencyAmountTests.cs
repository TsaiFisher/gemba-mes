using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// §5.17 多幣別金額鏈路：CurrencyService.ToBaseAsync 精度、匯率方向、負匯率防護
/// </summary>
public class MultiCurrencyAmountTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "S002";
    private const string CustCode = "C002";

    private readonly Mock<ISystemParamService> _mockSysParam;
    private readonly IMemoryCache              _cache;
    private readonly CurrencyService           _currencySvc;

    private readonly FinPaymentService      _paymentSvc;
    private readonly Mock<IDocNumberService> _mockDocNo  = new();
    private readonly Mock<IDateTimeService>  _mockDt     = new();

    private int _pmtSeq;
    private int _recSeq;
    private static readonly DateOnly Today = new DateOnly(2026, 2, 1);

    public MultiCurrencyAmountTests()
    {
        _context.CurrentPlantId = PlantId;

        // CurrencyService（使用真實 DB + mock sysParam）
        _mockSysParam = new Mock<ISystemParamService>();
        _mockSysParam.Setup(x => x.GetAsync(ProjectZSystemParamKeys.BaseCurrency)).ReturnsAsync("TWD");
        _cache = new MemoryCache(new MemoryCacheOptions());
        _currencySvc = new CurrencyService(_context, _mockSysParam.Object, _cache);

        // FinPaymentService（使用真實 CurrencyService）
        _mockDocNo.Setup(d => d.NextAsync("APPayment", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"APM{++_pmtSeq:D6}"));
        _mockDocNo.Setup(d => d.NextAsync("ARReceipt", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"ARR{++_recSeq:D6}"));
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);

        _paymentSvc = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _currencySvc);

        SeedBaseData();
    }

    private void SeedBaseData()
    {
        _context.Currencies.AddRange(
            new Currency { CurrencyCode = "TWD", CurrencyName = "新台幣",  Symbol = "NT$", DecimalPlaces = 0, IsActive = true },
            new Currency { CurrencyCode = "USD", CurrencyName = "美元",    Symbol = "$",   DecimalPlaces = 2, IsActive = true },
            new Currency { CurrencyCode = "JPY", CurrencyName = "日圓",    Symbol = "¥",   DecimalPlaces = 0, IsActive = true });
        _context.CurrencyRates.AddRange(
            new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2026, 1, 1), BuyRate = 30.1234m, SellRate = 30.5m },
            new CurrencyRate { CurrencyCode = "JPY", RateDate = new DateOnly(2026, 1, 1), BuyRate = 0.2m,    SellRate = 0.21m });
        _context.Companies.AddRange(
            new Company { CompanyCode = SuppCode, CompanyName = "供應商B", IsSupplier = true, IsActive = true },
            new Company { CompanyCode = CustCode, CompanyName = "客戶B",   IsCustomer = true, IsActive = true });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // E1：ToBaseAsync 精度 – MidpointRounding.AwayFromZero，按基幣小數位
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Precision: 1.0001 USD × 30.1234 Buy → Round(30.12583234, 0, AwayFromZero) = 30</summary>
    [Fact]
    public async Task ToBaseAsync_Precision_AwayFromZero_BaseCurrencyTWD()
    {
        // TWD.DecimalPlaces = 0
        var result = await _currencySvc.ToBaseAsync(
            1.0001m, "USD", new DateOnly(2026, 1, 15), RateType.Buy);

        // Round(1.0001 * 30.1234, 0, AwayFromZero) = Round(30.1237..., 0) = 30
        Assert.NotNull(result);
        Assert.Equal(0, result!.Value % 1 == 0 ? 0 : -1); // 整數（無小數）
        Assert.Equal(Math.Round(1.0001m * 30.1234m, 0, MidpointRounding.AwayFromZero), result.Value);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E2：ToBaseAsync 無匯率 → 回傳 null（不拋例外）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17: 幣別無匯率紀錄 → ToBaseAsync 回傳 null</summary>
    [Fact]
    public async Task ToBaseAsync_NoCurrencyRate_ReturnsNull()
    {
        var result = await _currencySvc.ToBaseAsync(
            100m, "USD", new DateOnly(2025, 1, 1), RateType.Buy); // 無 2025 年資料

        Assert.Null(result);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3：本幣直接轉換 → AmountLocal = Amount（1:1）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 EP Valid: 付款幣別為空（本幣）→ AmountLocal = Amount</summary>
    [Fact]
    public async Task APPayment_LocalCurrency_AmountLocalEqualsAmount()
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "INV-LC1", SupplierCode = SuppCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            TotalAmount  = 5000m, AmountLocal = 5000m,
            Status       = APInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        var req = new APPaymentRequest(
            SupplierCode: SuppCode, PaymentDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: null, ExchangeRate: 1m, Amount: 5000m,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new("INV-LC1", 5000m) });

        var pmt = await _paymentSvc.CreateAPPaymentAsync(req, "U1", PlantId);

        Assert.Equal(5000m, pmt.Amount);
        Assert.Equal(5000m, pmt.AmountLocal);
        Assert.Equal(1m,    pmt.ExchangeRate);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E4：外幣付款 AmountLocal = RoundAsync(Amount × Rate, 本位幣)
    //     （currency.md：一律依 Currency.DecimalPlaces 進位，不得硬編碼位數）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 EP Valid: 100.0001 USD × 32 = 3200.0032 → 本位幣 TWD（0 位）= 3200</summary>
    [Fact]
    public async Task APPayment_ForeignAmountLocal_RoundsToBaseCurrencyDecimals()
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "INV-FX1", SupplierCode = SuppCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            TotalAmount  = 100.0001m, AmountLocal = 3200.003m,
            ExchangeRate = 32m, CurrencyCode = "USD",
            Status       = APInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        var req = new APPaymentRequest(
            SupplierCode: SuppCode, PaymentDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: "USD", ExchangeRate: 32m, Amount: 100.0001m,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new("INV-FX1", 100.0001m) });

        var pmt = await _paymentSvc.CreateAPPaymentAsync(req, "U1", PlantId);

        // 乘積 3200.0032 先算滿精度，落帳時才依本位幣 TWD（DecimalPlaces=0）收斂
        var expected = await _currencySvc.RoundAsync(100.0001m * 32m, "TWD");
        Assert.Equal(3200m, expected);
        Assert.Equal(expected, pmt.AmountLocal);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E5：付款匯率方向 – 本幣 = 外幣 × Rate（買匯方向）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §5.17 FX Direction: 1 USD × 30.1234 Buy → 本位幣 TWD（0 位）= 30（乘法方向驗證）。
    /// 方向由 ExchangeRate 斷言把關；AmountLocal 另受本位幣小數位數收斂。
    /// </summary>
    [Fact]
    public async Task APPayment_ExchangeDirection_BaseEqualsForeinTimesRate()
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "INV-DIR1", SupplierCode = SuppCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            TotalAmount  = 1m, AmountLocal = 30.1234m,
            ExchangeRate = 30.1234m, CurrencyCode = "USD",
            Status       = APInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        // 使用幣別維護匯率（submitted=0 → fallback to DB → Buy 30.1234）
        var req = new APPaymentRequest(
            SupplierCode: SuppCode, PaymentDate: new DateOnly(2026, 1, 15), Method: PaymentMethod.Wire,
            CurrencyCode: "USD", ExchangeRate: 0m, Amount: 1m,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new("INV-DIR1", 1m) });

        var pmt = await _paymentSvc.CreateAPPaymentAsync(req, "U1", PlantId);

        // 匯率原值保留 4 位（方向：本幣 = 外幣 × Rate）；本幣金額依 TWD 收斂為 30
        Assert.Equal(30.1234m, pmt.ExchangeRate);
        Assert.Equal(await _currencySvc.RoundAsync(1m * 30.1234m, "TWD"), pmt.AmountLocal);
        Assert.Equal(30m, pmt.AmountLocal);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E6：AP 負匯率 → 拒絕
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Guard: AP ExchangeRate < 0 → ArgumentOutOfRangeException</summary>
    [Fact]
    public async Task APPayment_NegativeRate_Throws()
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "INV-NR1", SupplierCode = SuppCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            TotalAmount  = 100m, AmountLocal = 100m,
            Status       = APInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        var req = new APPaymentRequest(
            SupplierCode: SuppCode, PaymentDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: "USD", ExchangeRate: -1m, Amount: 100m,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new("INV-NR1", 100m) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _paymentSvc.CreateAPPaymentAsync(req, "U1", PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E7：AR 負匯率 → 拒絕
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Guard: AR ExchangeRate < 0 → ArgumentOutOfRangeException</summary>
    [Fact]
    public async Task ARReceipt_NegativeRate_Throws()
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId  = "ARI-NR1", CustomerCode = CustCode,
            InvoiceDate  = Today, DueDate = Today.AddDays(30),
            TotalAmount  = 100m, AmountLocal = 100m,
            Status       = ARInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        var req = new ARReceiptRequest(
            CustomerCode: CustCode, ReceiptDate: Today, Method: PaymentMethod.Wire,
            CurrencyCode: "USD", ExchangeRate: -2m, Amount: 100m,
            BankAccountId: null, Reference: null, Remarks: null,
            Allocations: new List<PaymentAllocationLine> { new("ARI-NR1", 100m) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _paymentSvc.CreateARReceiptAsync(req, "U1", PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E8：JPY 整數精度 – 0 小數位
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Precision: 1000 JPY × 0.2 Buy → Round(200, 0, AwayFromZero) = 200 TWD</summary>
    [Fact]
    public async Task ToBaseAsync_JPY_ZeroDecimalPlaces_RoundsCorrectly()
    {
        var result = await _currencySvc.ToBaseAsync(
            1000m, "JPY", new DateOnly(2026, 1, 15), RateType.Buy);

        Assert.NotNull(result);
        Assert.Equal(200m, result!.Value);
        Assert.Equal(result.Value, Math.Floor(result.Value)); // 整數
    }
}
