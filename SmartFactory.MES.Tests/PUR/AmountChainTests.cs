using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// PO→GR→AP 金額鏈正確性（§5.11 Gap C）。
///
/// 稽核公式：
///   PO 暫估金額   = PO.UnitPrice × GRLine.ReceiptQty
///   GR LineAmount  = ReceiptQty × UnitPrice（無稅）
///   AP TotalAmount = Σ(LineAmount) + Σ(TaxAmount)
///   PPV            = AP.TotalAmount − PO 暫估金額
/// </summary>
public class AmountChainTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";

    private readonly APInvoiceService _apInvoiceSvc;

    private readonly Mock<IDocNumberService> _mockDocNo   = new();
    private readonly Mock<IDateTimeService>  _mockDt      = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;

    private int _apSeq;

    public AmountChainTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"API{++_apSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        _mockCurrency
            .Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        _apInvoiceSvc = new APInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);

        _context.Companies.Add(new Company { CompanyCode = SuppCode, CompanyName = "測試供應商", IsSupplier = true });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // C1：無稅同價，AP = GR = PO 暫估，PPV = 0（§5.11 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 EP Valid: 廠商報價與 PO 相同，無稅 → TotalAmount 三層一致，PPV = 0</summary>
    [Fact]
    public async Task GapC_NoTax_SamePrice_ThreeLayerAmountsConsistent_PpvZero()
    {
        const decimal poUnitPrice = 200m;
        const decimal receiptQty  = 50m;

        SeedGrWithPoEstimate("GR-C001", receiptQty, unitPrice: poUnitPrice, taxRate: 0m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C001");

        decimal poEstimate = poUnitPrice * receiptQty;   // = 10000
        decimal grTotal    = receiptQty * poUnitPrice;   // = 10000
        decimal apTotal    = invoice.TotalAmount;        // = 10000（無稅）

        Assert.Equal(grTotal,    poEstimate,  precision: 4);
        Assert.Equal(apTotal,    grTotal,     precision: 4);
        Assert.Equal(0m, apTotal - poEstimate, precision: 4);  // PPV = 0
    }

    // ════════════════════════════════════════════════════════════════════════
    // C2：含稅，TotalAmount = ΣLineAmount + ΣTaxAmount（§5.11 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 EP Valid: 5% 稅率，TotalAmount = LineAmount + TaxAmount，加總精確不截斷</summary>
    [Fact]
    public async Task GapC_WithTax_TotalAmountEqualsLinePlusTax()
    {
        const decimal receiptQty = 100m;
        const decimal unitPrice  = 300m;
        const decimal taxRate    = 0.05m;   // Tax.TaxRate 以小數存放（0.05 = 5%）

        SeedGrWithPoEstimate("GR-C002", receiptQty, unitPrice, taxRate);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C002");

        Assert.Single(invoice.Lines);
        var line = invoice.Lines.First();

        decimal expectedLineAmt = Math.Round(receiptQty * unitPrice, 4, MidpointRounding.AwayFromZero); // 30000
        decimal expectedTaxAmt  = Math.Round(expectedLineAmt * taxRate, 4, MidpointRounding.AwayFromZero); // 1500
        decimal expectedTotal   = expectedLineAmt + expectedTaxAmt; // 31500

        Assert.Equal(expectedLineAmt, line.LineAmount,      precision: 4);
        Assert.Equal(expectedTaxAmt,  line.TaxAmount,       precision: 4);
        Assert.Equal(expectedTotal,   invoice.TotalAmount,  precision: 4);
        Assert.Equal(invoice.Lines.Sum(l => l.LineAmount) + invoice.Lines.Sum(l => l.TaxAmount),
                     invoice.TotalAmount, precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C3：PPV 存在，差額精確至 2 位小數（§5.11 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 EP Valid: 廠商報價高於 PO → PPV = 正值，精確計算</summary>
    [Fact]
    public async Task GapC_PpvPositive_InvoicePriceHigherThanPoPrice_PrecisionCorrect()
    {
        const decimal poUnitPrice = 100m;
        const decimal apUnitPrice = 100.50m;  // 廠商高報 0.50
        const decimal receiptQty  = 200m;

        SeedGrWithPoEstimate("GR-C003", receiptQty, unitPrice: apUnitPrice, taxRate: 0m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C003");

        decimal poEstimate = poUnitPrice * receiptQty;   // = 20000
        decimal ppv        = invoice.TotalAmount - poEstimate;

        Assert.Equal(100m, ppv, precision: 2);   // (100.50 - 100) × 200 = 100.00
        Assert.True(ppv > 0m, "PPV 應為正值（廠商索價高於 PO → 不利差異）");
    }

    /// <summary>§5.11 EP Valid: 廠商報價低於 PO → PPV = 負值（有利差異）</summary>
    [Fact]
    public async Task GapC_PpvNegative_InvoicePriceLowerThanPoPrice_PrecisionCorrect()
    {
        const decimal poUnitPrice = 100m;
        const decimal apUnitPrice = 99.80m;   // 廠商低報 0.20
        const decimal receiptQty  = 500m;

        SeedGrWithPoEstimate("GR-C004", receiptQty, unitPrice: apUnitPrice, taxRate: 0m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C004");

        decimal poEstimate = poUnitPrice * receiptQty;   // = 50000
        decimal ppv        = invoice.TotalAmount - poEstimate;

        Assert.Equal(-100m, ppv, precision: 2);   // (99.80 - 100) × 500 = -100.00
        Assert.True(ppv < 0m, "PPV 應為負值（廠商索價低於 PO → 有利差異）");
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4：BVA 精度邊界（§5.11 BVA）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 BVA: UnitPrice = 100.001，Qty = 1000 → LineAmount = 100001.0000（4 位小數不截斷）</summary>
    [Fact]
    public async Task GapC_BVA_UnitPriceWithFourDecimals_LineAmountPrecise()
    {
        const decimal unitPrice  = 100.001m;
        const decimal receiptQty = 1000m;

        SeedGrWithPoEstimate("GR-C005", receiptQty, unitPrice, taxRate: 0m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C005");

        decimal expectedLineAmt = Math.Round(receiptQty * unitPrice, 4, MidpointRounding.AwayFromZero);
        // 1000 × 100.001 = 100001.0000

        Assert.Equal(expectedLineAmt,    invoice.Lines.First().LineAmount, precision: 4);
        Assert.Equal(expectedLineAmt,    invoice.TotalAmount,              precision: 4);
    }

    /// <summary>§5.11 BVA: LineAmount = 1，TaxRate = 5% → TaxAmount = 0.05，TotalAmount = 1.05（AwayFromZero 不截斷為 0）</summary>
    [Fact]
    public async Task GapC_BVA_TaxOnMinimalLineAmount_NotTruncatedToZero()
    {
        const decimal unitPrice  = 1m;
        const decimal receiptQty = 1m;
        const decimal taxRate    = 0.05m;   // Tax.TaxRate 以小數存放（0.05 = 5%）

        SeedGrWithPoEstimate("GR-C006", receiptQty, unitPrice, taxRate);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-C006");

        var line = invoice.Lines.First();
        Assert.Equal(1m,    line.LineAmount,  precision: 4);
        Assert.Equal(0.05m, line.TaxAmount,   precision: 4);   // 1 × 5% = 0.05，不截斷
        Assert.Equal(1.05m, invoice.TotalAmount, precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C5：多行 TotalAmount = 各行 LineAmount + TaxAmount 之和（§5.11 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 EP Valid: 多行 GR，Invoice.TotalAmount = 各行 LineAmount + TaxAmount 加總，無漏算</summary>
    [Fact]
    public async Task GapC_MultiLine_TotalAmountSumsAllLines()
    {
        const string grId = "GR-C007";
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

        // 行 1：100 × 50 = 5000，含稅 5% = 250，小計 5250
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId, LineNo = 1,
            ProductCode    = "MAT001", ReceiptQty = 100m, UomCode = "PC", StockQty = 100m, StockUomCode = "PC",
            UnitPrice = 50m, TaxRate = 0.05m, LineAmount = 5000m, TaxAmount = 250m, TotalAmount = 5250m,
        });

        // 行 2：200 × 30 = 6000，含稅 5% = 300，小計 6300
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId, LineNo = 2,
            ProductCode    = "MAT001", ReceiptQty = 200m, UomCode = "PC", StockQty = 200m, StockUomCode = "PC",
            UnitPrice = 30m, TaxRate = 0.05m, LineAmount = 6000m, TaxAmount = 300m, TotalAmount = 6300m,
        });

        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();

        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync(grId);

        decimal expectedTotal = invoice.Lines.Sum(l => l.LineAmount + l.TaxAmount);
        Assert.Equal(expectedTotal, invoice.TotalAmount, precision: 4);
        Assert.Equal(11550m, invoice.TotalAmount, precision: 4);  // 5250 + 6300
    }

    // ════════════════════════════════════════════════════════════════════════
    // C6：稽核查詢—金額鏈斷裂偵測（§5.11 EP Invalid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.11 EP Invalid: 外部植入 LineAmount ≠ Qty × UnitPrice 的 APInvoice，稽核查詢可偵測</summary>
    [Fact]
    public void GapC_AuditQuery_LineAmountMismatch_Detectable()
    {
        var invoice = new APInvoice
        {
            APInvoiceId  = "API-MISMATCH",
            SupplierCode = SuppCode,
            InvoiceDate  = new DateOnly(2025, 1, 1),
            DueDate      = new DateOnly(2025, 2, 1),
            TotalAmount  = 999m,   // 故意不正確
            AmountLocal  = 999m,
            Status       = APInvoiceStatus.Open,
            PlantId      = PlantId,
        };
        invoice.Lines.Add(new APInvoiceLine
        {
            LineNo      = 1,
            ProductCode = "MAT001",
            Qty         = 100m,
            UnitPrice   = 50m,
            LineAmount  = 4999m,  // 應為 5000，但植入錯誤值
        });
        _context.APInvoices.Add(invoice);
        _context.SaveChanges();

        // 稽核查詢：LineAmount ≠ Round(Qty × UnitPrice, 4)
        var mismatch = _context.APInvoiceLines
            .Where(l => l.APInvoiceId == "API-MISMATCH")
            .AsEnumerable()  // 轉客戶端，方便做 decimal Round
            .Where(l => Math.Abs(l.LineAmount - Math.Round(l.Qty * l.UnitPrice, 4, MidpointRounding.AwayFromZero)) > 0.0001m)
            .ToList();

        Assert.Single(mismatch);
        Assert.Equal(1m, mismatch[0].Qty * mismatch[0].UnitPrice - mismatch[0].LineAmount, precision: 4);  // 差 1 元
    }

    // ════════════════════════════════════════════════════════════════════════
    // 輔助方法
    // ════════════════════════════════════════════════════════════════════════

    private void SeedGrWithPoEstimate(string grId, decimal receiptQty, decimal unitPrice, decimal taxRate)
    {
        var lineAmt = Math.Round(receiptQty * unitPrice, 4, MidpointRounding.AwayFromZero);
        var taxAmt  = Math.Round(lineAmt * taxRate / 100m, 4, MidpointRounding.AwayFromZero);

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
            GoodsReceiptId = grId, LineNo = 1,
            ProductCode    = "MAT001", ReceiptQty = receiptQty,
            UomCode        = "PC", StockQty = receiptQty, StockUomCode = "PC",
            UnitPrice      = unitPrice, TaxRate = taxRate,
            LineAmount     = lineAmt, TaxAmount = taxAmt, TotalAmount = lineAmt + taxAmt,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }
}
