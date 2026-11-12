using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// §5.15 AP 付款 → 匯兌損益計算、多批次付款、多發票核銷
/// 已覆蓋於 FinPaymentServiceAPTests：基本付款流程/狀態/容許值/作廢
/// 本檔補充：ExchangeGainLoss 計算精度與方向、分批累加
/// </summary>
public class APPaymentJournalTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "S001";

    private readonly FinPaymentService          _svc;
    private readonly Mock<IDocNumberService>    _mockDocNo    = new();
    private readonly Mock<IDateTimeService>     _mockDt       = new();
    private readonly Mock<ICurrencyService>     _mockCurrency;
    private readonly Mock<IJournalEntryService> _mockJournal  = new();

    private int _pmtSeq;
    private static readonly DateOnly Today = new DateOnly(2026, 1, 15);

    public APPaymentJournalTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync("APPayment", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"APM{++_pmtSeq:D6}"));
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        _mockCurrency = GetMockCurrencyService();
        _mockCurrency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                     .ReturnsAsync((decimal?)null);

        _svc = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
            journalSvc: _mockJournal.Object);

        _context.Companies.Add(new Company
        {
            CompanyCode = SuppCode, CompanyName = "供應商A",
            IsSupplier  = true,     IsActive    = true
        });
        _context.SaveChanges();
    }

    private APInvoice SeedInvoice(string invId, decimal totalAmount, decimal exchangeRate = 1m, string? currencyCode = null)
    {
        var inv = new APInvoice
        {
            APInvoiceId  = invId,
            SupplierCode = SuppCode,
            InvoiceDate  = Today,
            DueDate      = Today.AddDays(30),
            TotalAmount  = totalAmount,
            AmountLocal  = Math.Round(totalAmount * exchangeRate, 4),
            ExchangeRate = exchangeRate,
            CurrencyCode = currencyCode,
            Status       = APInvoiceStatus.Open,
            PlantId      = PlantId,
        };
        _context.APInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }

    private async Task<APPayment> CreateAndConfirm(
        string invId, decimal amount, decimal paymentRate = 1m, string? currencyCode = null)
    {
        var req = new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  currencyCode,
            ExchangeRate:  paymentRate,
            Amount:        amount,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new(invId, amount) });

        var pmt = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);
        await _svc.ConfirmAPPaymentAsync(pmt.APPaymentId);
        return _context.APPayments.Find(pmt.APPaymentId)!;
    }

    /// <summary>付清 → 折讓 → 退回後，要先作廢退回那張才能作廢原付款（與 AR 對稱）。</summary>
    [Fact]
    public async Task VoidPayment_AfterRefund_RequiresVoidingTheRefundFirst()
    {
        SeedInvoice("API-RF1", totalAmount: 1000m);
        var original = await CreateAndConfirm("API-RF1", amount: 1000m);
        _context.ChangeTracker.Clear();
        var inv = _context.APInvoices.Find("API-RF1")!;
        inv.DebitedAmount = 300m;
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        var refund = await CreateAndConfirm("API-RF1", amount: -300m);
        _context.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.VoidAPPaymentAsync(original.APPaymentId));
        Assert.Contains("API-RF1", ex.Message);
        _context.ChangeTracker.Clear();
        Assert.Equal(700m, _context.APInvoices.Find("API-RF1")!.PaidAmount);

        await _svc.VoidAPPaymentAsync(refund.APPaymentId);
        _context.ChangeTracker.Clear();
        await _svc.VoidAPPaymentAsync(original.APPaymentId);
        _context.ChangeTracker.Clear();
        Assert.Equal(0m, _context.APInvoices.Find("API-RF1")!.PaidAmount);
    }

    /// <summary>作廢發票不得再付款（與 AR 對稱；2026-10-22 PayableLedgerModelTests 隨機序列發現）。</summary>
    [Fact]
    public async Task Confirm_OnVoidedInvoice_IsRejected_AndNothingIsApplied()
    {
        SeedInvoice("API-VD1", totalAmount: 1050m).Status = APInvoiceStatus.Void;
        _context.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAndConfirm("API-VD1", amount: 500m));
        Assert.Contains("API-VD1", ex.Message);

        _context.ChangeTracker.Clear();
        var inv = _context.APInvoices.Find("API-VD1")!;
        Assert.Equal(0m, inv.PaidAmount);
        Assert.Equal(APInvoiceStatus.Void, inv.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C1：匯率有利（AP）→ ExchangeGainLoss < 0（少付本幣＝匯兌利益）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 FX: 發票匯率 30，付款匯率 28 → 本幣少付 200 → GainLoss = -200</summary>
    [Fact]
    public async Task Confirm_FxFavorable_GainLossNegative()
    {
        // 發票：100 USD @ 30 TWD/USD = 3000 本幣
        SeedInvoice("INV-F01", totalAmount: 100m, exchangeRate: 30m, currencyCode: "USD");

        // 付款：100 USD @ 28 TWD/USD = 2800 本幣
        var pmt = await CreateAndConfirm("INV-F01", amount: 100m, paymentRate: 28m, currencyCode: "USD");

        // AllocatedAmountLocal = 100 * 28 = 2800; invoiceLocalShare = 100 * 30 = 3000
        // GainLoss = 2800 - 3000 = -200 (有利匯差)
        Assert.Equal(-200m, pmt.ExchangeGainLoss);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C2：匯率不利（AP）→ ExchangeGainLoss > 0（多付本幣＝匯兌損失）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 FX: 發票匯率 28，付款匯率 30 → 本幣多付 200 → GainLoss = +200</summary>
    [Fact]
    public async Task Confirm_FxUnfavorable_GainLossPositive()
    {
        SeedInvoice("INV-F02", totalAmount: 100m, exchangeRate: 28m, currencyCode: "USD");

        var pmt = await CreateAndConfirm("INV-F02", amount: 100m, paymentRate: 30m, currencyCode: "USD");

        // AllocatedAmountLocal = 100 * 30 = 3000; invoiceLocalShare = 100 * 28 = 2800
        // GainLoss = 3000 - 2800 = +200 (損失)
        Assert.Equal(200m, pmt.ExchangeGainLoss);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C3：相同匯率 → GainLoss = 0
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 FX: 發票與付款匯率相同 → ExchangeGainLoss = 0</summary>
    [Fact]
    public async Task Confirm_SameRate_GainLossZero()
    {
        SeedInvoice("INV-F03", totalAmount: 100m, exchangeRate: 30m, currencyCode: "USD");

        var pmt = await CreateAndConfirm("INV-F03", amount: 100m, paymentRate: 30m, currencyCode: "USD");

        Assert.Equal(0m, pmt.ExchangeGainLoss);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4：匯兌損益精度至 4 位小數
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 BVA Precision: GainLoss = Round(payLocal - invLocal, 4, AwayFromZero)</summary>
    [Fact]
    public async Task Confirm_FxGainLoss_PrecisionFourDecimals()
    {
        // invLocal = 1.0001 * 30 = 30.003; pmtLocal = 1.0001 * 29.9999 = Round(30.0027..., 4) = 29.9999
        SeedInvoice("INV-F04", totalAmount: 1.0001m, exchangeRate: 30m, currencyCode: "USD");

        var pmt = await CreateAndConfirm("INV-F04", amount: 1.0001m, paymentRate: 29.9999m, currencyCode: "USD");

        // AllocatedAmountLocal = Round(1.0001 * 29.9999, 4) = Round(30.0027..., 4) = 30.003
        // invoiceLocalShare = 1.0001 * 30 = 30.003
        // GainLoss = 30.003 - 30.003 = 0 (tiny diff rounds to 0)
        var gainLoss = pmt.ExchangeGainLoss;
        // 精度要求：結果為 4 位小數
        Assert.Equal(gainLoss, Math.Round(gainLoss, 4, MidpointRounding.AwayFromZero));
    }

    // ════════════════════════════════════════════════════════════════════════
    // C5：分批付款 – PaidAmount 累加
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 EP Valid: 兩次部分付款 PaidAmount 正確累加</summary>
    [Fact]
    public async Task Confirm_TwoBatchPayments_PaidAmountAccumulates()
    {
        SeedInvoice("INV-B01", totalAmount: 1000m);

        await CreateAndConfirm("INV-B01", amount: 600m);
        await CreateAndConfirm("INV-B01", amount: 400m);

        var inv = _context.APInvoices.Find("INV-B01")!;
        Assert.Equal(1000m, inv.PaidAmount);
        Assert.Equal(APInvoiceStatus.Paid, inv.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C6：一筆付款核銷兩張發票
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 EP Valid: 一張付款單同時核銷兩張發票，各自 PaidAmount 正確</summary>
    [Fact]
    public async Task Confirm_MultiInvoiceBatch_EachInvoiceUpdated()
    {
        SeedInvoice("INV-M01", totalAmount: 500m);
        SeedInvoice("INV-M02", totalAmount: 800m);

        var req = new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  null,
            ExchangeRate:  1m,
            Amount:        1300m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine>
            {
                new("INV-M01", 500m),
                new("INV-M02", 800m),
            });

        var pmt = await _svc.CreateAPPaymentAsync(req, "U1", PlantId);
        await _svc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        var inv1 = _context.APInvoices.Find("INV-M01")!;
        var inv2 = _context.APInvoices.Find("INV-M02")!;
        Assert.Equal(500m, inv1.PaidAmount);
        Assert.Equal(APInvoiceStatus.Paid, inv1.Status);
        Assert.Equal(800m, inv2.PaidAmount);
        Assert.Equal(APInvoiceStatus.Paid, inv2.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C7：BVA 最後一筆剛好結清 → Status = Paid
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.15 BVA: 先付 999，再付 1 剛好湊滿 → Paid（999 明確 PartialPaid，999+1=1000 → Paid）</summary>
    [Fact]
    public async Task Confirm_BVA_LastPaymentExactBalance_StatusPaid()
    {
        SeedInvoice("INV-BV1", totalAmount: 1000m);
        await CreateAndConfirm("INV-BV1", amount: 999m);

        var invMid = _context.APInvoices.Find("INV-BV1")!;
        Assert.Equal(APInvoiceStatus.PartialPaid, invMid.Status);

        await CreateAndConfirm("INV-BV1", amount: 1m);

        var invFinal = _context.APInvoices.Find("INV-BV1")!;
        Assert.Equal(APInvoiceStatus.Paid, invFinal.Status);
        Assert.Equal(1000m, invFinal.PaidAmount);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C8：負匯率 → 拒絕建立付款
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Guard: ExchangeRate < 0 → ArgumentOutOfRangeException</summary>
    [Fact]
    public async Task Create_NegativeExchangeRate_Throws()
    {
        SeedInvoice("INV-NEG1", totalAmount: 100m);

        var req = new APPaymentRequest(
            SupplierCode:  SuppCode,
            PaymentDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  "USD",
            ExchangeRate:  -5m,
            Amount:        100m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("INV-NEG1", 100m) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.CreateAPPaymentAsync(req, "U1", PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // C9：AP Confirm → 自動日記帳借貸方金額驗證
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §5.15 JournalEntry: 確認 AP 付款後自動過帳：
    /// 借 應付帳款 (2100) = 發票本幣份額，貸 銀行存款 (1130) = 付款本幣；
    /// 匯兌損益差額補記借/貸 FX 科目，確保分錄平衡。
    /// </summary>
    [Fact]
    public async Task Confirm_FxFavorable_JournalEntryPosted()
    {
        // 發票：100 USD @ 30 TWD = 3000 本幣；付款 @ 28 → GainLoss = -200
        SeedInvoice("INV-J01", totalAmount: 100m, exchangeRate: 30m, currencyCode: "USD");
        var pmt = await CreateAndConfirm("INV-J01", amount: 100m, paymentRate: 28m, currencyCode: "USD");

        // 驗證 CreateAutoEntryAsync 被呼叫一次（sourceDocType="APPayment", sourceDocId=pmt.APPaymentId）
        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "APPayment",
            pmt.APPaymentId,
            It.IsAny<DateOnly>(),
            It.IsAny<string>(),
            It.Is<IList<JournalLineRequest>>(lines =>
                // 借 應付帳款 = 3000（發票本幣份額）
                lines.Any(l => l.AccountCode == StandardAccounts.AccountsPayableCode && l.Debit == 3000m) &&
                // 貸 銀行存款 = 2800（付款本幣）
                lines.Any(l => l.AccountCode == StandardAccounts.CashAndBankCode     && l.Credit == 2800m) &&
                // 貸 匯兌利益 = 200（有利匯差）
                lines.Any(l => l.AccountCode == StandardAccounts.FxGainCode          && l.Credit == 200m)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
