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
/// §5.16 AR 收款 → 匯兌損益計算、分批收款、作廢回補
/// 補充現有 O2CReconciliationTests 未覆蓋的 ExchangeGainLoss 計算
/// </summary>
public class ARReceiptJournalTests : TestBase
{
    private const string PlantId    = "P001";
    private const string CustCode   = "C001";

    private readonly FinPaymentService          _svc;
    private readonly Mock<IDocNumberService>    _mockDocNo   = new();
    private readonly Mock<IDateTimeService>     _mockDt      = new();
    private readonly Mock<ICurrencyService>     _mockCurrency;
    private readonly Mock<IJournalEntryService> _mockJournal = new();

    private int _recSeq;
    private static readonly DateOnly Today = new DateOnly(2026, 1, 20);

    public ARReceiptJournalTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync("ARReceipt", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"ARR{++_recSeq:D6}"));
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
        _mockCurrency = GetMockCurrencyService();
        _mockCurrency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                     .ReturnsAsync((decimal?)null);

        _svc = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
            journalSvc: _mockJournal.Object);

        _context.Companies.Add(new Company
        {
            CompanyCode = CustCode, CompanyName = "客戶A",
            IsCustomer  = true,     IsActive    = true
        });
        _context.SaveChanges();
    }

    private ARInvoice SeedARInvoice(string invId, decimal totalAmount,
        decimal exchangeRate = 1m, string? currencyCode = null)
    {
        var inv = new ARInvoice
        {
            ARInvoiceId    = invId,
            CustomerCode   = CustCode,
            InvoiceDate    = Today,
            DueDate        = Today.AddDays(30),
            TotalAmount    = totalAmount,
            AmountLocal    = Math.Round(totalAmount * exchangeRate, 4),
            ExchangeRate   = exchangeRate,
            CurrencyCode   = currencyCode,
            Status         = ARInvoiceStatus.Open,
            PlantId        = PlantId,
        };
        _context.ARInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }

    private async Task<ARReceipt> CreateAndConfirm(
        string invId, decimal amount, decimal receiptRate = 1m, string? currencyCode = null)
    {
        var req = new ARReceiptRequest(
            CustomerCode:  CustCode,
            ReceiptDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  currencyCode,
            ExchangeRate:  receiptRate,
            Amount:        amount,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new(invId, amount) });

        var rec = await _svc.CreateARReceiptAsync(req, "U1", PlantId);
        await _svc.ConfirmARReceiptAsync(rec.ARReceiptId);
        return _context.ARReceipts.Find(rec.ARReceiptId)!;
    }

    /// <summary>
    /// 作廢發票的 TotalAmount 不歸零、未結照算得出正數：不擋就收得進錢，應收被沖成貸餘
    /// （2026-10-22 ReceivableLedgerModelTests 隨機序列發現）。
    /// </summary>
    [Fact]
    public async Task Confirm_OnVoidedInvoice_IsRejected_AndNothingIsApplied()
    {
        SeedARInvoice("ARI-VD1", totalAmount: 1050m).Status = ARInvoiceStatus.Void;
        _context.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAndConfirm("ARI-VD1", amount: 500m));
        Assert.Contains("ARI-VD1", ex.Message);

        _context.ChangeTracker.Clear();
        var inv = _context.ARInvoices.Find("ARI-VD1")!;
        Assert.Equal(0m, inv.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Void, inv.Status);
    }

    /// <summary>
    /// 收足 → 折讓 → 退款後，再作廢原收款會讓已收變負數。原本以 Math.Max(0, …) 夾掉，退款從子帳消失
    /// （2026-10-22 隨機序列發現）。要先作廢退款；作廢退款後原收款就能作廢。
    /// </summary>
    [Fact]
    public async Task VoidReceipt_AfterRefund_RequiresVoidingTheRefundFirst()
    {
        SeedARInvoice("ARI-RF1", totalAmount: 1000m);
        var original = await CreateAndConfirm("ARI-RF1", amount: 1000m);
        _context.ChangeTracker.Clear();
        var inv = _context.ARInvoices.Find("ARI-RF1")!;
        inv.CreditedAmount = 300m;                       // 收清後才折讓 → 多收 300
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        var refund = await CreateAndConfirm("ARI-RF1", amount: -300m);
        _context.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.VoidARReceiptAsync(original.ARReceiptId));
        Assert.Contains("ARI-RF1", ex.Message);
        _context.ChangeTracker.Clear();
        Assert.Equal(700m, _context.ARInvoices.Find("ARI-RF1")!.ReceivedAmount);   // 沒被動到

        await _svc.VoidARReceiptAsync(refund.ARReceiptId);
        _context.ChangeTracker.Clear();
        await _svc.VoidARReceiptAsync(original.ARReceiptId);
        _context.ChangeTracker.Clear();
        Assert.Equal(0m, _context.ARInvoices.Find("ARI-RF1")!.ReceivedAmount);
    }

    /// <summary>折讓單只累加 CreditedAmount——未結要扣折讓：不得超收，收足餘額即結清。</summary>
    [Fact]
    public async Task Confirm_AfterCreditNote_CannotReceiveTheCreditedPart_AndRemainderSettles()
    {
        SeedARInvoice("ARI-CN1", totalAmount: 1050m).CreditedAmount = 105m;
        _context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAndConfirm("ARI-CN1", amount: 1050m));

        _context.ChangeTracker.Clear();
        await CreateAndConfirm("ARI-CN1", amount: 945m);
        Assert.Equal(ARInvoiceStatus.Received, _context.ARInvoices.Find("ARI-CN1")!.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D1：匯率有利（AR）→ ExchangeGainLoss > 0（多收本幣＝匯兌利益）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 FX: 發票匯率 28，收款匯率 30 → 多收 200 → GainLoss = +200</summary>
    [Fact]
    public async Task Confirm_FxFavorable_GainLossPositive()
    {
        // 發票：100 USD @ 28 TWD/USD = 2800 本幣（AR 記錄收入）
        SeedARInvoice("ARI-F01", totalAmount: 100m, exchangeRate: 28m, currencyCode: "USD");

        // 收款：100 USD @ 30 TWD/USD = 3000 本幣
        var rec = await CreateAndConfirm("ARI-F01", amount: 100m, receiptRate: 30m, currencyCode: "USD");

        // AllocatedAmountLocal = 100 * 30 = 3000; invoiceLocalShare = 100 * 28 = 2800
        // GainLoss = 3000 - 2800 = +200 (有利匯差)
        Assert.Equal(200m, rec.ExchangeGainLoss);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D2：匯率不利（AR）→ ExchangeGainLoss < 0（少收本幣＝匯兌損失）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 FX: 發票匯率 30，收款匯率 28 → 少收 200 → GainLoss = -200</summary>
    [Fact]
    public async Task Confirm_FxUnfavorable_GainLossNegative()
    {
        SeedARInvoice("ARI-F02", totalAmount: 100m, exchangeRate: 30m, currencyCode: "USD");

        var rec = await CreateAndConfirm("ARI-F02", amount: 100m, receiptRate: 28m, currencyCode: "USD");

        Assert.Equal(-200m, rec.ExchangeGainLoss);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D3：分批收款 – ReceivedAmount 累加，最終 Received
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 EP Valid: 三批收款累加至 TotalAmount，Status → Received</summary>
    [Fact]
    public async Task Confirm_ThreeBatchReceipts_ReceivedAmountAccumulates()
    {
        SeedARInvoice("ARI-B01", totalAmount: 1500m);

        await CreateAndConfirm("ARI-B01", 500m);
        await CreateAndConfirm("ARI-B01", 500m);
        await CreateAndConfirm("ARI-B01", 500m);

        var inv = _context.ARInvoices.Find("ARI-B01")!;
        Assert.Equal(1500m, inv.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Received, inv.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D4：作廢已確認收款 → ReceivedAmount 回補、Status → Open
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 EP Valid: Void 已確認收款 → ARInvoice.ReceivedAmount 回補為 0</summary>
    [Fact]
    public async Task Void_ConfirmedReceipt_ReceivedAmountRestored()
    {
        SeedARInvoice("ARI-V01", totalAmount: 500m);

        var rec = await CreateAndConfirm("ARI-V01", 500m);
        var afterConfirm = _context.ARInvoices.Find("ARI-V01")!;
        Assert.Equal(ARInvoiceStatus.Received, afterConfirm.Status);

        await _svc.VoidARReceiptAsync(rec.ARReceiptId);

        var afterVoid = _context.ARInvoices.Find("ARI-V01")!;
        Assert.Equal(0m, afterVoid.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Open, afterVoid.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D5：超出未結餘額 → 拒絕
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 EP Invalid: 核銷金額超出 AR 未結餘額 → 拒絕</summary>
    [Fact]
    public async Task Confirm_OverAllocation_Throws()
    {
        SeedARInvoice("ARI-O01", totalAmount: 100m);
        await CreateAndConfirm("ARI-O01", 80m);

        // 第二次嘗試收 21 → 累計 101 > 100 + 0.001 → 拒絕
        var req = new ARReceiptRequest(
            CustomerCode:  CustCode,
            ReceiptDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  null,
            ExchangeRate:  1m,
            Amount:        21m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("ARI-O01", 21m) });

        var rec2 = await _svc.CreateARReceiptAsync(req, "U1", PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmARReceiptAsync(rec2.ARReceiptId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // D6：負匯率 → 拒絕建立收款
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.17 Guard: ExchangeRate < 0 → ArgumentOutOfRangeException</summary>
    [Fact]
    public async Task Create_NegativeExchangeRate_Throws()
    {
        SeedARInvoice("ARI-NEG1", totalAmount: 100m);

        var req = new ARReceiptRequest(
            CustomerCode:  CustCode,
            ReceiptDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  "USD",
            ExchangeRate:  -3m,
            Amount:        100m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("ARI-NEG1", 100m) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.CreateARReceiptAsync(req, "U1", PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // D7：BVA 0.001 容許差 → Received
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.16 BVA: 累計收款超出 TotalAmount 在 0.001 容許內 → Received（不拒絕）</summary>
    [Fact]
    public async Task Confirm_BVA_WithinTolerance_StatusReceived()
    {
        SeedARInvoice("ARI-BV1", totalAmount: 100m);

        // 收 100.001 → 100.001 ≤ 100 + 0.001 → 在容許內
        var req = new ARReceiptRequest(
            CustomerCode:  CustCode,
            ReceiptDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  null,
            ExchangeRate:  1m,
            Amount:        100.001m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("ARI-BV1", 100.001m) });

        var rec = await _svc.CreateARReceiptAsync(req, "U1", PlantId);
        await _svc.ConfirmARReceiptAsync(rec.ARReceiptId);

        var inv = _context.ARInvoices.Find("ARI-BV1")!;
        Assert.Equal(ARInvoiceStatus.Received, inv.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D7-b：Confirm 的狀態前提（草稿可確認、已作廢不可確認）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 已作廢的收款單不可再確認。原本 Confirm 只擋「已收款」，作廢後再確認會把核銷
    /// 重覆套用一次（發票 ReceivedAmount 重複加、銀行餘額重複入帳），而作廢已反沖過了。
    /// 畫面把確認鈕的條件放寬成「非已收款／非已作廢」之後，這個守門就是最後一道。
    /// </summary>
    [Fact]
    public async Task Confirm_VoidedReceipt_Throws()
    {
        SeedARInvoice("ARI-VD1", totalAmount: 500m);
        var req = new ARReceiptRequest(
            CustomerCode:  CustCode,
            ReceiptDate:   Today,
            Method:        PaymentMethod.Wire,
            CurrencyCode:  null,
            ExchangeRate:  1m,
            Amount:        500m,
            BankAccountId: null,
            Reference:     null,
            Remarks:       null,
            Allocations:   new List<PaymentAllocationLine> { new("ARI-VD1", 500m) });

        var rec = await _svc.CreateARReceiptAsync(req, "U1", PlantId);
        await _svc.VoidARReceiptAsync(rec.ARReceiptId);   // 草稿直接作廢

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ConfirmARReceiptAsync(rec.ARReceiptId));

        var inv = _context.ARInvoices.Find("ARI-VD1")!;
        Assert.Equal(0m, inv.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Open, inv.Status);
    }

    /// <summary>
    /// 草稿收款單即可確認：全系統沒有任何地方會把收款單設為 Approved，
    /// 若確認的前提是 Approved，收款單將永遠無法核銷（畫面上確認鈕也曾因此永不渲染）。
    /// </summary>
    [Fact]
    public async Task Confirm_DraftReceipt_IsAllowed()
    {
        SeedARInvoice("ARI-DR1", totalAmount: 900m);
        var rec = await CreateAndConfirm("ARI-DR1", amount: 900m);

        Assert.Equal(ARReceiptStatus.Received, rec.Status);
        var inv = _context.ARInvoices.Find("ARI-DR1")!;
        Assert.Equal(ARInvoiceStatus.Received, inv.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // D8：AR Confirm → 自動日記帳借貸方金額驗證
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §5.16 JournalEntry: 確認 AR 收款後自動過帳：
    /// 借 銀行存款 (1130) = 收款本幣，貸 應收帳款 (1170) = 發票本幣份額；
    /// 匯兌利益（匯率有利時）補記貸方 FX Gain 科目，確保分錄平衡。
    /// </summary>
    [Fact]
    public async Task Confirm_FxFavorable_JournalEntryPosted()
    {
        // 發票：100 USD @ 28 TWD = 2800 本幣；收款 @ 30 → GainLoss = +200
        SeedARInvoice("ARI-J01", totalAmount: 100m, exchangeRate: 28m, currencyCode: "USD");
        var rec = await CreateAndConfirm("ARI-J01", amount: 100m, receiptRate: 30m, currencyCode: "USD");

        // 驗證 CreateAutoEntryAsync 被呼叫一次（sourceDocType="ARReceipt", sourceDocId=rec.ARReceiptId）
        _mockJournal.Verify(j => j.CreateAutoEntryAsync(
            "ARReceipt",
            rec.ARReceiptId,
            It.IsAny<DateOnly>(),
            It.IsAny<string>(),
            It.Is<IList<JournalLineRequest>>(lines =>
                // 借 銀行存款 = 3000（收款本幣）
                lines.Any(l => l.AccountCode == StandardAccounts.CashAndBankCode        && l.Debit  == 3000m) &&
                // 貸 應收帳款 = 2800（發票本幣份額）
                lines.Any(l => l.AccountCode == StandardAccounts.AccountsReceivableCode && l.Credit == 2800m) &&
                // 貸 匯兌利益 = 200（有利匯差）
                lines.Any(l => l.AccountCode == StandardAccounts.FxGainCode             && l.Credit == 200m)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
