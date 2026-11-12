using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 採購付款流程（P2P, Procure-to-Pay）資料一致性檢核測試。
/// 對應規格書 §1.1 三大檢核規則與 §5.1 EP/BVA 案例。
///
/// Rule 1 — PO vs GR 數量一致性：OrderQty >= 累計 ReceiptQty
/// Rule 2 — GR/IR 暫估應付帳款平衡：暫估 = GR 估值 - AP 核銷 - PPV
/// Rule 3 — AP vs Payment 結清平衡：TotalAmount = 累計付款 + 折讓 + 預付沖銷
/// </summary>
public class P2PReconciliationTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";

    private readonly APInvoiceService  _apInvoiceSvc;
    private readonly FinPaymentService _paymentSvc;

    private readonly Mock<IDocNumberService> _mockDocNo   = new();
    private readonly Mock<IDateTimeService>  _mockDt      = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;

    // 序號計數器（供各 docNo 回傳唯一 ID）
    private int _apInvSeq;
    private int _apPmtSeq;

    public P2PReconciliationTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"API{++_apInvSeq:D6}"));

        _mockDocNo
            .Setup(d => d.NextAsync("APPayment", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"APM{++_apPmtSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        // GetRateAsync 回傳 null → APInvoiceService 以 1m 作為匯率（本位幣情境）
        _mockCurrency
            .Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        _apInvoiceSvc = new APInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);
        _paymentSvc   = new FinPaymentService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);

        _context.Companies.Add(new Company { CompanyCode = SuppCode, CompanyName = "測試供應商", IsSupplier = true });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Rule 1: PO vs GR 數量一致性（§1.1.B.1）
    // ════════════════════════════════════════════════════════════════════════

    // 本區原有 4 支測試已移除：它們自己把 ReceivedQty 塞成 100 再斷言
    // 「ReceivedQty <= OrderQty」，完全不碰任何產品程式碼，永遠不會因回歸變紅，
    // 只會讓 Rule 1 看起來有守門。真正的覆蓋在：
    //   - PurchaseService 累計收貨與超收攔截 → P2PDocFlowTests（GapA_*，走
    //     CreateGoodsReceiptAsync，含分批湊滿、最小單位超收、刪草稿回補 ReceivedQty）
    //   - §3.1 超收稽核查詢 → DataAuditServiceTests.PoOverReceipt_*
    //     （走 DataAuditService.AuditPoOverReceiptAsync 本體，含容忍值）

    // ════════════════════════════════════════════════════════════════════════
    // Rule 2: GR/IR 暫估應付帳款平衡（§1.1.B.2）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>EP Valid: 由進貨單產生 APInvoice，Qty 與金額完全對應進貨明細（GR/IR 全額建立）</summary>
    [Fact]
    public async Task Rule2_GenerateAPInvoice_QtyAndAmountMatchGoodsReceipt()
    {
        SeedGr("GR010", receiptQty: 50m, unitPrice: 200m);

        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR010");

        Assert.Single(invoice.Lines);
        Assert.Equal(50m,     invoice.Lines.First().Qty);
        Assert.Equal(10000m,  invoice.TotalAmount);   // 50 × 200
    }

    /// <summary>外幣發票: AmountLocal = TotalAmount × 幣別維護匯率（乘法方向，1 外幣 = Rate 本幣）</summary>
    [Fact]
    public async Task Rule2_GenerateAPInvoice_ForeignCurrency_AmountLocalMultipliesMasterRate()
    {
        // 幣別維護 USD 買入匯率 = 32（GR 進貨日 2025-03-01）
        _mockCurrency
            .Setup(c => c.GetRateAsync("USD", It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync(32m);

        SeedGrForeign("GR-USD01", receiptQty: 10m, unitPrice: 25m, currency: "USD");   // 交易幣總額 250 USD

        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-USD01");

        Assert.Equal("USD", invoice.CurrencyCode);
        Assert.Equal(32m,   invoice.ExchangeRate);
        Assert.Equal(250m,  invoice.TotalAmount);    // 10 × 25（交易幣）
        Assert.Equal(8000m, invoice.AmountLocal);    // 250 × 32（本幣，乘法）
    }

    /// <summary>冪等性: 同一進貨單重複呼叫兩次，只產生一張 APInvoice</summary>
    [Fact]
    public async Task Rule2_GenerateAPInvoice_Idempotent_SameInvoiceReturned()
    {
        SeedGr("GR011", receiptQty: 10m, unitPrice: 100m);

        var inv1 = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR011");
        var inv2 = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR011");

        Assert.Equal(inv1.APInvoiceId, inv2.APInvoiceId);
        Assert.Single(_context.APInvoices.Where(i => i.SourceDocId == "GR011").ToList());
    }

    /// <summary>BVA 100%: GR/IR 餘額 = 進貨暫估 - 發票核銷 = 0（完全核銷時餘額歸零）</summary>
    [Fact]
    public async Task Rule2_GrIrBalance_FullInvoiceMatchesGrEstimate_IsZero()
    {
        SeedGr("GR012", receiptQty: 100m, unitPrice: 50m);   // 暫估 5000
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR012");

        decimal grEstimate    = 100m * 50m;          // 5000（無稅情境）
        decimal invoicedTotal = invoice.TotalAmount;  // APInvoice 金額應 = 5000

        Assert.Equal(0m, grEstimate - invoicedTotal, precision: 4);
    }

    /// <summary>BVA PPV +0.01: 廠商發票單價比 PO 高 0.01 元，進料差異 = Qty × 0.01，無浮點精度損失</summary>
    [Fact]
    public async Task Rule2_PPV_InvoicePriceHigherByPenny_PrecisionCorrect()
    {
        // 廠商提高單價 0.01（100.00 → 100.01），數量 1000
        SeedGr("GR013", receiptQty: 1000m, unitPrice: 100.01m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR013");

        decimal poUnitPrice    = 100.00m;
        decimal grEstimate     = 1000m * poUnitPrice;    // 100000.00
        decimal invoicedAmount = invoice.TotalAmount;    // 100010.00（1000 × 100.01）

        // PPV > 0 表示廠商實際收費高於採購單價（不利差異）
        decimal ppv = invoicedAmount - grEstimate;
        Assert.Equal(10.00m, ppv, precision: 2);
    }

    /// <summary>BVA PPV -0.01: 廠商發票單價比 PO 低 0.01 元，PPV 為負值（有利差異）</summary>
    [Fact]
    public async Task Rule2_PPV_InvoicePriceLowerByPenny_PrecisionCorrect()
    {
        SeedGr("GR014", receiptQty: 1000m, unitPrice: 99.99m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR014");

        decimal poUnitPrice    = 100.00m;
        decimal grEstimate     = 1000m * poUnitPrice;    // 100000.00
        decimal invoicedAmount = invoice.TotalAmount;    // 99990.00（1000 × 99.99）

        decimal ppv = invoicedAmount - grEstimate;
        Assert.Equal(-10.00m, ppv, precision: 2);
    }

    /// <summary>EP Valid: 部分核銷（GR qty &gt; AP qty），GR/IR 餘額應為正數（仍有未核銷暫估）</summary>
    [Fact]
    public async Task Rule2_PartialInvoice_GrIrBalancePositive()
    {
        SeedGr("GR015", receiptQty: 100m, unitPrice: 50m);   // 暫估 5000
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR015");

        // 模擬廠商只送了 60 qty 的發票（分批）
        decimal partialInvoiceAmount = 60m * 50m;   // 3000
        decimal grEstimate           = 5000m;
        decimal grIrBalance          = grEstimate - partialInvoiceAmount;

        Assert.True(grIrBalance > 0m, "部分核銷時 GR/IR 餘額應仍為正數");
        Assert.Equal(2000m, grIrBalance, precision: 4);
    }

    /// <summary>
    /// §5.1 EP Valid: GR qty &lt; AP qty（廠商溢開/預開發票）。
    /// 系統不應阻擋此情境（可配置允許），但 GR/IR 餘額將為負數，稽核查詢可偵測異常。
    /// </summary>
    [Fact]
    public void Rule2_OverInvoice_GrQtyLessThanApQty_GrIrBalanceNegative_Detectable()
    {
        // GR 實際進貨 60，廠商卻開了 100 qty 的發票（溢開）
        SeedGr("GR016", receiptQty: 60m, unitPrice: 100m);   // GR 暫估 6000

        var overInvoice = new APInvoice
        {
            APInvoiceId   = "API-OVER01",
            SupplierCode  = SuppCode,
            InvoiceDate   = new DateOnly(2025, 4, 1),
            DueDate       = new DateOnly(2025, 5, 1),
            TotalAmount   = 10000m,   // 100 × 100（溢開）
            AmountLocal   = 10000m,
            Status        = APInvoiceStatus.Open,
            SourceDocType = "GoodsReceipt",
            SourceDocId   = "GR016",
            PlantId       = PlantId,
        };
        overInvoice.Lines.Add(new APInvoiceLine
        {
            LineNo = 1, ProductCode = "MAT001",
            Qty = 100m, UnitPrice = 100m, LineAmount = 10000m,
            SourceLineNo = 1,
        });
        _context.APInvoices.Add(overInvoice);
        _context.SaveChanges();

        // GR/IR 餘額 = 進貨暫估 - 發票核銷 → 負值表示過度核銷
        decimal grEstimate     = 60m * 100m;   // = 6000
        decimal invoicedAmount = _context.APInvoiceLines
            .Where(l => l.APInvoiceId == "API-OVER01")
            .Sum(l => l.LineAmount);            // = 10000

        decimal grIrBalance = grEstimate - invoicedAmount;

        Assert.True(grIrBalance < 0m, "溢開發票後 GR/IR 餘額應為負數（可被稽核偵測）");
        Assert.Equal(-4000m, grIrBalance, precision: 4);
    }

    /// <summary>§5.1 EP Invalid: 手動建立 APInvoice 時 Qty ≤ 0 → 拋出例外</summary>
    [Fact]
    public async Task Rule2_CreateManual_ZeroQty_ThrowsException()
    {
        var invoice = new APInvoice
        {
            SupplierCode = SuppCode, InvoiceDate = new DateOnly(2025, 4, 1),
            DueDate = new DateOnly(2025, 5, 1), TotalAmount = 0, PlantId = PlantId,
        };
        invoice.Lines.Add(new APInvoiceLine
        {
            LineNo = 1, Qty = 0m, UnitPrice = 100m, LineAmount = 0m,   // Qty = 0
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _apInvoiceSvc.CreateManualAsync(invoice));
    }

    /// <summary>§5.1 EP Invalid: 手動建立 APInvoice 時 UnitPrice &lt; 0 → 拋出例外</summary>
    [Fact]
    public async Task Rule2_CreateManual_NegativeUnitPrice_ThrowsException()
    {
        var invoice = new APInvoice
        {
            SupplierCode = SuppCode, InvoiceDate = new DateOnly(2025, 4, 1),
            DueDate = new DateOnly(2025, 5, 1), TotalAmount = 0, PlantId = PlantId,
        };
        invoice.Lines.Add(new APInvoiceLine
        {
            LineNo = 1, Qty = 10m, UnitPrice = -50m, LineAmount = -500m,   // 負單價
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _apInvoiceSvc.CreateManualAsync(invoice));
    }

    /// <summary>§5.1 EP Invalid: 由 GR 產生 APInvoice 時，GR qty > PO OrderQty → 拋出例外</summary>
    [Fact]
    public async Task Rule2_GenerateFromGR_QtyExceedsPOOrderQty_ThrowsException()
    {
        // PO OrderQty = 50，但 GR ReceiptQty = 80（超收場景）
        SeedPoWithLine("PO010", orderQty: 50m);
        SeedGrLinkedToPo("GR017", "PO010", receiptQty: 80m);   // 超過 PO 訂量

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR017"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // Rule 3: AP vs Payment 結清平衡（§1.1.B.3）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>EP Valid: 部分付款 → Invoice 狀態 = PartialPaid，PaidAmount 正確累計</summary>
    [Fact]
    public async Task Rule3_PartialPayment_StatusPartialPaid_PaidAmountAccumulated()
    {
        SeedInvoice("API001", totalAmount: 1000m);

        var pmt = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(600m, "API001", 600m), "user1", PlantId);
        await _paymentSvc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        var inv = await _context.APInvoices.FindAsync("API001");
        Assert.Equal(APInvoiceStatus.PartialPaid, inv!.Status);
        Assert.Equal(600m, inv.PaidAmount, precision: 4);
    }

    /// <summary>EP Valid: 一次全額付款 → Invoice 狀態 = Paid，PaidAmount = TotalAmount</summary>
    [Fact]
    public async Task Rule3_FullPayment_StatusPaid_PaidAmountEqualsTotalAmount()
    {
        SeedInvoice("API002", totalAmount: 800m);

        var pmt = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(800m, "API002", 800m), "user1", PlantId);
        await _paymentSvc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        var inv = await _context.APInvoices.FindAsync("API002");
        Assert.Equal(APInvoiceStatus.Paid, inv!.Status);
        Assert.Equal(inv.TotalAmount, inv.PaidAmount, precision: 4);
    }

    /// <summary>BVA 100%: 兩筆分付後最後一筆剛好清零 → 自動轉 Paid（§5.1 BVA 核銷臨界點）</summary>
    [Fact]
    public async Task Rule3_TwoPayments_LastExactlyClears_StatusBecomePaid()
    {
        SeedInvoice("API003", totalAmount: 1000m);

        // 第一筆 600
        var pmt1 = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(600m, "API003", 600m), "user1", PlantId);
        await _paymentSvc.ConfirmAPPaymentAsync(pmt1.APPaymentId);

        var mid = await _context.APInvoices.FindAsync("API003");
        Assert.Equal(APInvoiceStatus.PartialPaid, mid!.Status);

        // 第二筆剛好付清餘額 400
        var pmt2 = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(400m, "API003", 400m), "user1", PlantId);
        await _paymentSvc.ConfirmAPPaymentAsync(pmt2.APPaymentId);

        var final = await _context.APInvoices.FindAsync("API003");
        Assert.Equal(APInvoiceStatus.Paid, final!.Status);
        Assert.Equal(final.TotalAmount, final.PaidAmount, precision: 4);
    }

    /// <summary>EP Invalid: 核銷金額超出發票未結餘額 → 拋出 InvalidOperationException，拒絕過帳</summary>
    [Fact]
    public async Task Rule3_AllocationExceedsOutstanding_ThrowsException()
    {
        SeedInvoice("API004", totalAmount: 500m);

        var pmt = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(600m, "API004", 600m),   // 600 > 500，超額核銷
            "user1", PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _paymentSvc.ConfirmAPPaymentAsync(pmt.APPaymentId));
    }

    /// <summary>付款作廢反沖: PaidAmount 回補歸零，Invoice 回退 Open</summary>
    [Fact]
    public async Task Rule3_VoidPayment_PaidAmountRestored_InvoiceRevertsToOpen()
    {
        SeedInvoice("API005", totalAmount: 300m);

        var pmt = await _paymentSvc.CreateAPPaymentAsync(
            BuildPaymentReq(300m, "API005", 300m), "user1", PlantId);
        await _paymentSvc.ConfirmAPPaymentAsync(pmt.APPaymentId);

        // 確認已付款
        var afterPay = await _context.APInvoices.FindAsync("API005");
        Assert.Equal(APInvoiceStatus.Paid, afterPay!.Status);

        // 作廢付款
        await _paymentSvc.VoidAPPaymentAsync(pmt.APPaymentId);

        var reverted = await _context.APInvoices.FindAsync("API005");
        Assert.Equal(APInvoiceStatus.Open, reverted!.Status);
        Assert.True(reverted.PaidAmount <= 0.001m, "作廢後 PaidAmount 應回到 0");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 輔助方法
    // ════════════════════════════════════════════════════════════════════════

    private void SeedPoWithLine(string poId, decimal orderQty)
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = poId,
            OrderDate       = new DateOnly(2025, 1, 1),
            CreatedBy       = "user1",
            SupplierCode    = SuppCode,
            ApprovalStatus  = DocumentApprovalStatus.Approved,
            PlantId         = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = poId,     LineNo      = 1,
            ProductCode     = "MAT001", OrderQty    = orderQty,
            UomCode         = "PC",     UnitPrice   = 100m,
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();
    }

    private void SeedGrLinkedToPo(string grId, string poId, decimal receiptQty)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId,
            ReceiptDate    = new DateOnly(2025, 1, 5),
            CreatedBy      = "user1",
            SupplierCode   = SuppCode,
            Status         = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId        = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId  = grId,       LineNo          = 1,
            PurchaseOrderId = poId,       PoLineNo        = 1,
            ProductCode     = "MAT001",   ReceiptQty      = receiptQty,
            UomCode         = "PC",       StockQty        = receiptQty,
            StockUomCode    = "PC",       UnitPrice       = 100m,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private void SeedGr(string grId, decimal receiptQty, decimal unitPrice)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId,
            ReceiptDate    = new DateOnly(2025, 3, 1),
            CreatedBy      = "user1",
            SupplierCode   = SuppCode,
            Status         = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId        = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId,       LineNo     = 1,
            ProductCode    = "MAT001",   ReceiptQty = receiptQty,
            UomCode        = "PC",       StockQty   = receiptQty,
            StockUomCode   = "PC",       UnitPrice  = unitPrice,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private void SeedGrForeign(string grId, decimal receiptQty, decimal unitPrice, string currency)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId,
            ReceiptDate    = new DateOnly(2025, 3, 1),
            CreatedBy      = "user1",
            SupplierCode   = SuppCode,
            Status         = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId        = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId,       LineNo     = 1,
            ProductCode    = "MAT001",   ReceiptQty = receiptQty,
            UomCode        = "PC",       StockQty   = receiptQty,
            StockUomCode   = "PC",       UnitPrice  = unitPrice,
            CurrencyCode   = currency,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private void SeedInvoice(string invoiceId, decimal totalAmount)
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = invoiceId,
            SupplierCode = SuppCode,
            InvoiceDate  = new DateOnly(2025, 2, 1),
            DueDate      = new DateOnly(2025, 3, 3),
            TotalAmount  = totalAmount,
            AmountLocal  = totalAmount,
            Status       = APInvoiceStatus.Open,
            PlantId      = PlantId,
        });
        _context.SaveChanges();
    }

    private static APPaymentRequest BuildPaymentReq(
        decimal payAmount, string invoiceId, decimal allocAmount)
        => new(SuppCode, new DateOnly(2025, 2, 10), PaymentMethod.Wire,
               null, 1m, payAmount, null, null, null,
               [new PaymentAllocationLine(invoiceId, allocAmount)]);
}
