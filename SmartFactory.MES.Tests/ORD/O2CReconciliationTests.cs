using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.ORD.Models;

namespace SmartFactory.MES.Tests.ORD;

/// <summary>
/// 銷售收款流程（O2C, Order-to-Cash）資料一致性檢核測試。
/// 對應規格書 §1.2 三大檢核規則與 §5.5 / §5.6 EP/BVA 案例。
///
/// §5.5 — GS → AR Invoice 生成一致性（金額、數量、稅額精度）
/// §5.6 — AR vs Receipt 收款核銷平衡（部分/全額/超額/反沖）
/// </summary>
public class O2CReconciliationTests : TestBase
{
    private const string PlantId      = "P001";
    private const string CustomerCode = "CUST001";

    private readonly ARInvoiceService  _arInvoiceSvc;
    private readonly FinPaymentService _paymentSvc;

    private readonly Mock<IDocNumberService> _mockDocNo  = new();
    private readonly Mock<IDateTimeService>  _mockDt     = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;

    private int _arInvSeq;
    private int _arRcpSeq;

    public O2CReconciliationTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("ARInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"ARI{++_arInvSeq:D6}"));

        _mockDocNo
            .Setup(d => d.NextAsync("ARReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"ARR{++_arRcpSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        // null → service 以 1m 處理（本位幣情境）
        _mockCurrency
            .Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        _arInvoiceSvc = new ARInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);
        _paymentSvc   = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);

        _context.Companies.Add(new Company
        {
            CompanyCode = CustomerCode,
            CompanyName = "測試客戶",
            IsCustomer  = true,
        });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // §5.5 — GS → AR Invoice 生成一致性
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EP Valid: 單行出貨單生成 AR 發票，數量、金額與稅額正確對應。
    /// §5.5 EP Valid 第 1 案例。
    /// </summary>
    [Fact]
    public async Task GenerateFromShipment_SingleLine_CorrectQtyAndAmount()
    {
        var shipDate = new DateOnly(2026, 6, 1);
        var gs = SeedGoodsShipment("GS001", shipDate, new[]
        {
            (ProductCode: "PRD-A", ShipQty: 100m, UnitPrice: 50m, TaxRate: 0.05m),   // Tax.TaxRate 為小數（0.05 = 5%）
        });

        var arInv = await _arInvoiceSvc.GenerateFromShipmentAsync("GS001");

        // AR 數量 = GS 出貨數量
        Assert.Single(arInv.Lines);
        Assert.Equal(100m, arInv.Lines.First().Qty);
        Assert.Equal(50m,  arInv.Lines.First().UnitPrice);

        // LineAmount = Qty × UnitPrice；TaxAmount = LineAmount × TaxRate
        var expectedLine    = 100m * 50m;           // 5000
        var expectedTax     = Math.Round(5000m * 0.05m, 4, MidpointRounding.AwayFromZero);  // 250
        var expectedTotal   = expectedLine + expectedTax;  // 5250
        Assert.Equal(expectedLine,  arInv.Lines.First().LineAmount);
        Assert.Equal(expectedTax,   arInv.Lines.First().TaxAmount);
        Assert.Equal(expectedTotal, arInv.TotalAmount);
    }

    /// <summary>
    /// EP Valid: 多行出貨單，TotalAmount = Σ 明細金額 + Σ 稅額。
    /// §5.5 EP Valid 第 2 案例（SO vs GS 多品項比對）。
    /// </summary>
    [Fact]
    public async Task GenerateFromShipment_MultiLine_TotalAmountEqualsLineSum()
    {
        var gs = SeedGoodsShipment("GS002", new DateOnly(2026, 6, 1), new[]
        {
            ("PRD-A", 50m,  200m, 5m),
            ("PRD-B", 20m,  500m, 5m),
        });

        var arInv = await _arInvoiceSvc.GenerateFromShipmentAsync("GS002");

        Assert.Equal(2, arInv.Lines.Count);
        var totalLine  = arInv.Lines.Sum(l => l.LineAmount);
        var totalTax   = arInv.Lines.Sum(l => l.TaxAmount);
        Assert.Equal(totalLine + totalTax, arInv.TotalAmount);
        // 50×200 = 10000, 20×500 = 10000, total = 20000 + tax
        Assert.Equal(20000m, totalLine);
    }

    /// <summary>
    /// EP Valid: 同一出貨單重複呼叫 GenerateFromShipmentAsync，冪等回傳同一張 AR 發票。
    /// §5.5 EP Valid 第 3 案例（防止重複開票）。
    /// </summary>
    [Fact]
    public async Task GenerateFromShipment_Idempotent_ReturnsSameInvoice()
    {
        SeedGoodsShipment("GS003", new DateOnly(2026, 6, 1), new[]
        {
            ("PRD-A", 10m, 100m, 0m),
        });

        var first  = await _arInvoiceSvc.GenerateFromShipmentAsync("GS003");
        var second = await _arInvoiceSvc.GenerateFromShipmentAsync("GS003");

        Assert.Equal(first.ARInvoiceId, second.ARInvoiceId);
        // DB 中只有一張
        Assert.Equal(1, _context.ARInvoices.Count(i => i.SourceDocId == "GS003"));
    }

    /// <summary>
    /// BVA 稅額四捨五入精度：TaxAmount 四捨五入至小數第 4 位（AwayFromZero）。
    /// §5.5 BVA：稅額尾差精度驗證。
    /// </summary>
    [Fact]
    public async Task GenerateFromShipment_TaxRate_PrecisionTo4Decimals()
    {
        // 出貨 3 個，單價 100，稅率 5% → 稅額 = 300 × 5% = 15.0000（精確無截斷）
        SeedGoodsShipment("GS004", new DateOnly(2026, 6, 1), new[]
        {
            ("PRD-A", 3m, 100m, 0.05m),
        });

        var arInv = await _arInvoiceSvc.GenerateFromShipmentAsync("GS004");

        // 300 × 0.05 = 15，無需四捨五入問題
        Assert.Equal(15m, arInv.TaxAmount);
        // 單一明細稅額也一致
        Assert.Equal(15m, arInv.Lines.First().TaxAmount);
    }

    /// <summary>
    /// BVA 微額稅率：金額 × TaxRate 小數精度驗證（Round AwayFromZero 不截斷至 0）。
    /// §5.5 BVA：小額稅額不歸零。
    /// </summary>
    [Fact]
    public async Task GenerateFromShipment_SmallAmountTax_NotTruncatedToZero()
    {
        // 出貨 1 個，單價 1，稅率 5% → 稅額 = 0.05（不可截斷為 0）
        SeedGoodsShipment("GS005", new DateOnly(2026, 6, 1), new[]
        {
            ("PRD-A", 1m, 1m, 0.05m),
        });

        var arInv = await _arInvoiceSvc.GenerateFromShipmentAsync("GS005");

        Assert.Equal(0.05m, arInv.Lines.First().TaxAmount);
        Assert.Equal(1.05m, arInv.TotalAmount);   // 1 + 0.05
    }

    /// <summary>
    /// EP Invalid: 手動建立 AR 發票，明細 Qty ≤ 0 應拋出 InvalidOperationException。
    /// §5.5 EP Invalid 第 1 案例。
    /// </summary>
    [Fact]
    public async Task CreateManual_ZeroQty_ThrowsInvalidOperation()
    {
        var invoice = BuildManualARInvoice(qty: 0m, unitPrice: 100m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _arInvoiceSvc.CreateManualAsync(invoice));

        PzAssert.Message("Fin_ARLineQtyPriceFmt", ex.Message);
    }

    /// <summary>
    /// EP Invalid: 手動建立 AR 發票，明細 UnitPrice &lt; 0 應拋出 InvalidOperationException。
    /// §5.5 EP Invalid 第 2 案例。
    /// </summary>
    [Fact]
    public async Task CreateManual_NegativePrice_ThrowsInvalidOperation()
    {
        var invoice = BuildManualARInvoice(qty: 10m, unitPrice: -1m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _arInvoiceSvc.CreateManualAsync(invoice));

        PzAssert.Message("Fin_ARLineQtyPriceFmt", ex.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // §5.6 — AR vs Receipt 收款核銷平衡
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EP Valid: 部分收款（AllocatedAmount &lt; TotalAmount）→ ARInvoice.Status = PartialReceived。
    /// §5.6 EP Valid 第 1 案例。
    /// </summary>
    [Fact]
    public async Task ConfirmARReceipt_PartialAllocation_StatusPartialReceived()
    {
        var (invId, inv) = SeedARInvoice("ARI-P01", totalAmount: 5000m);
        var receiptId    = await CreateAndConfirmARReceipt(invId, allocatedAmount: 2000m);

        var updated = await _context.ARInvoices.FindAsync(invId);
        Assert.Equal(ARInvoiceStatus.PartialReceived, updated!.Status);
        Assert.Equal(2000m, updated.ReceivedAmount);
    }

    /// <summary>
    /// EP Valid: 全額收款（AllocatedAmount = TotalAmount）→ ARInvoice.Status = Received。
    /// §5.6 EP Valid 第 2 案例（§1.2.B.3 結清平衡）。
    /// </summary>
    [Fact]
    public async Task ConfirmARReceipt_FullAllocation_StatusReceived()
    {
        var (invId, inv) = SeedARInvoice("ARI-F01", totalAmount: 8000m);
        await CreateAndConfirmARReceipt(invId, allocatedAmount: 8000m);

        var updated = await _context.ARInvoices.FindAsync(invId);
        Assert.Equal(ARInvoiceStatus.Received, updated!.Status);
        Assert.Equal(8000m, updated.ReceivedAmount);
    }

    /// <summary>
    /// EP Invalid: 核銷金額超過未結餘額 → ConfirmARReceiptAsync 拋出 InvalidOperationException。
    /// §5.6 EP Invalid 案例（防止超額核銷）。
    /// </summary>
    [Fact]
    public async Task ConfirmARReceipt_AllocationExceedsOutstanding_Throws()
    {
        var (invId, _) = SeedARInvoice("ARI-E01", totalAmount: 3000m);
        var receipt    = BuildARReceiptRequest(invId, allocatedAmount: 4000m);  // 超額
        var rcp        = await _paymentSvc.CreateARReceiptAsync(receipt, "U1", PlantId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _paymentSvc.ConfirmARReceiptAsync(rcp.ARReceiptId));
        PzAssert.Message("Fin_AllocationExceedsOutstandingFmt", ex.Message);
    }

    /// <summary>
    /// BVA 全額臨界點: AllocatedAmount = TotalAmount − 0.001（容忍範圍）→ Received。
    /// §5.6 BVA 第 1 案例（容忍 0.001 元精度差）。
    /// </summary>
    [Fact]
    public async Task ConfirmARReceipt_AllocatedJustBelowTolerance_StatusReceived()
    {
        var (invId, _) = SeedARInvoice("ARI-T01", totalAmount: 5000m);
        // TotalAmount - ReceivedAmount = 5000 - 4999.999 = 0.001（在容忍範圍 0.001m 內）
        await CreateAndConfirmARReceipt(invId, allocatedAmount: 4999.999m);

        var updated = await _context.ARInvoices.FindAsync(invId);
        Assert.Equal(ARInvoiceStatus.Received, updated!.Status);
    }

    /// <summary>
    /// BVA 反沖: VoidARReceiptAsync 反沖已確認收款單 → ReceivedAmount 歸零，Status → Open。
    /// §5.6 BVA 第 2 案例（收款作廢還原）。
    /// </summary>
    [Fact]
    public async Task VoidARReceipt_AfterConfirm_RestoresStatusToOpen()
    {
        var (invId, _) = SeedARInvoice("ARI-V01", totalAmount: 6000m);
        var receiptId  = await CreateAndConfirmARReceipt(invId, allocatedAmount: 3000m);

        // 確認後狀態
        var afterConfirm = await _context.ARInvoices.FindAsync(invId);
        Assert.Equal(ARInvoiceStatus.PartialReceived, afterConfirm!.Status);

        // 作廢收款
        await _paymentSvc.VoidARReceiptAsync(receiptId);

        var afterVoid = await _context.ARInvoices.FindAsync(invId);
        Assert.Equal(0m,                   afterVoid!.ReceivedAmount);
        Assert.Equal(ARInvoiceStatus.Open, afterVoid.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 輔助方法
    // ════════════════════════════════════════════════════════════════════════

    private GoodsShipment SeedGoodsShipment(
        string shipId, DateOnly shipDate,
        IEnumerable<(string ProductCode, decimal ShipQty, decimal UnitPrice, decimal TaxRate)> lines)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = shipId,
            ShipDate        = shipDate,
            CompanyCode     = CustomerCode,
            TypeCode        = "GS",
            PlantId         = PlantId,
            CreatedBy       = "test",
            Status          = SmartFactory.MES.Modules.INV.Models.DocStatus.Confirmed,
        };
        int no = 1;
        foreach (var (prod, qty, price, tax) in lines)
        {
            var lineAmt = qty * price;
            var taxAmt  = Math.Round(lineAmt * tax / 100m, 4, MidpointRounding.AwayFromZero);
            gs.Lines.Add(new GoodsShipmentLine
            {
                LineNo      = no++,
                ProductCode = prod,
                ShipQty     = qty,
                UnitPrice   = price,
                TaxRate     = tax,
                LineAmount  = lineAmt,
                TaxAmount   = taxAmt,
                TotalAmount = lineAmt + taxAmt,
            });
        }
        gs.TotalAmount = gs.Lines.Sum(l => l.TotalAmount);
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
        return gs;
    }

    private (string InvoiceId, ARInvoice Invoice) SeedARInvoice(string invoiceId, decimal totalAmount)
    {
        var inv = new ARInvoice
        {
            ARInvoiceId    = invoiceId,
            CustomerCode   = CustomerCode,
            InvoiceDate    = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate        = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            TotalAmount    = totalAmount,
            AmountLocal    = totalAmount,
            Status         = ARInvoiceStatus.Open,
            PlantId        = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        _context.ARInvoices.Add(inv);
        _context.SaveChanges();
        return (invoiceId, inv);
    }

    private ARInvoice BuildManualARInvoice(decimal qty, decimal unitPrice)
    {
        var inv = new ARInvoice
        {
            CustomerCode   = CustomerCode,
            InvoiceDate    = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate        = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            PlantId        = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        var lineAmt = qty * unitPrice;
        inv.Lines.Add(new ARInvoiceLine
        {
            LineNo    = 1,
            Qty       = qty,
            UnitPrice = unitPrice,
            LineAmount = lineAmt,
        });
        inv.TotalAmount = lineAmt;
        inv.AmountLocal = lineAmt;
        return inv;
    }

    private ARReceiptRequest BuildARReceiptRequest(string invoiceId, decimal allocatedAmount)
        => new ARReceiptRequest(
            CustomerCode: CustomerCode,
            ReceiptDate:  DateOnly.FromDateTime(DateTime.UtcNow),
            Method:       PaymentMethod.Wire,
            CurrencyCode: null,
            ExchangeRate: 1m,
            Amount:       allocatedAmount,
            BankAccountId: null,
            Reference:    null,
            Remarks:      null,
            Allocations:  new List<PaymentAllocationLine>
            {
                new PaymentAllocationLine(invoiceId, allocatedAmount),
            });

    private async Task<string> CreateAndConfirmARReceipt(string invoiceId, decimal allocatedAmount)
    {
        var req = BuildARReceiptRequest(invoiceId, allocatedAmount);
        var rcp = await _paymentSvc.CreateARReceiptAsync(req, "U1", PlantId);
        await _paymentSvc.ConfirmARReceiptAsync(rcp.ARReceiptId);
        return rcp.ARReceiptId;
    }
}
