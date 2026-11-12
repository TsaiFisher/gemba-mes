using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 採購→進貨前後單拋轉數量控管（§5.9 Gap A）。
///
/// 涵蓋三大場景：
///   A1 — 累計進貨數量控管（多張 GR 對同一 PO，超收攔截）
///   A2 — 刪除草稿 GR → PO ReceivedQty 回補驗證
///   A3 — 作廢 APInvoice → 未結餘額歸零驗證
/// </summary>
public class P2PDocFlowTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";
    private const string UserId   = "user1";

    private readonly PurchaseService         _purchaseSvc;
    private readonly APInvoiceService        _apInvoiceSvc;

    private readonly Mock<IDocNumberService> _mockDocNo   = new();
    private readonly Mock<IDateTimeService>  _mockDt      = new();
    private readonly Mock<IUomService>       _mockUom     = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;

    private int _grSeq;
    private int _poSeq;
    private int _apSeq;

    public P2PDocFlowTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("GoodsReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"GR{++_grSeq:D6}"));

        _mockDocNo
            .Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"API{++_apSeq:D6}"));

        _mockUom
            .Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, decimal q, CancellationToken _) => q);

        _mockUom
            .Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal q, CancellationToken _) => q);

        _mockCurrency = GetMockCurrencyService();
        _mockCurrency
            .Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        _purchaseSvc  = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object,
                                             _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));
        _apInvoiceSvc = new APInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object);

        SeedBaseData();
    }

    private void SeedBaseData()
    {
        _context.Companies.Add(new Company { CompanyCode = SuppCode, CompanyName = "測試供應商", IsSupplier = true });
        _context.Products.Add(new Product  { ProductCode = "MAT001", ProductName = "原料A", PurchaseUom = "PC", StockUom = "PC" });
        _context.Uoms.Add(new Uom          { UomCode = "PC", UomName = "個" });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // A1：累計進貨數量控管（§5.9 EP Valid / EP Invalid / BVA）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.9 EP Valid: 分批進貨，兩批合計 = PO OrderQty（邊界允許）</summary>
    [Fact]
    public async Task GapA_TwoBatchReceipts_TotalEqualsOrderQty_BothAllowed()
    {
        SeedPo("PO001", orderQty: 100m);

        // 第一批 60
        var vm1 = BuildGrVm("PO001", receiptQty: 60m);
        var gr1 = await _purchaseSvc.CreateGoodsReceiptAsync(vm1, UserId, PlantId);

        // 第二批 40（剛好湊滿 100）
        var vm2 = BuildGrVm("PO001", receiptQty: 40m);
        var gr2 = await _purchaseSvc.CreateGoodsReceiptAsync(vm2, UserId, PlantId);

        var poLine = _context.PurchaseOrderLines.Find("PO001", 1)!;
        Assert.Equal(100m, poLine.ReceivedQty);
        Assert.NotNull(gr1);
        Assert.NotNull(gr2);
    }

    /// <summary>§5.9 BVA: 剩餘可收量 = OrderQty + 0.0001 → 拒絕（最小單位超收）</summary>
    [Fact]
    public async Task GapA_SecondBatch_ExceedsOrderQtyByMinUnit_Throws()
    {
        SeedPo("PO002", orderQty: 100m);
        await _purchaseSvc.CreateGoodsReceiptAsync(BuildGrVm("PO002", receiptQty: 50m), UserId, PlantId);

        // 第二批 50.0001 → 累計 100.0001 > 100 → 應拒絕
        var vm2 = BuildGrVm("PO002", receiptQty: 50.0001m);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _purchaseSvc.CreateGoodsReceiptAsync(vm2, UserId, PlantId));
    }

    /// <summary>§5.9 EP Invalid: 單批進貨直接超過 PO OrderQty → 拒絕</summary>
    [Fact]
    public async Task GapA_SingleBatch_ExceedsOrderQty_Throws()
    {
        SeedPo("PO003", orderQty: 50m);

        var vm = BuildGrVm("PO003", receiptQty: 60m);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _purchaseSvc.CreateGoodsReceiptAsync(vm, UserId, PlantId));
    }

    /// <summary>§5.9 EP Invalid: GenerateFromGoodsReceiptAsync 亦攔截累計超收的進貨單</summary>
    [Fact]
    public async Task GapA_GenerateAPInvoice_WhenCumulativeExceedsPO_Throws()
    {
        // 直接植入超收狀態（繞過 PurchaseService，模擬外部匯入或舊資料）
        SeedPoWithLine("PO004", orderQty: 50m);
        SeedGrLinkedToPo("GR-OVER", "PO004", receiptQty: 80m, status: DocStatus.Confirmed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-OVER"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // A2：刪除草稿 GR → PO ReceivedQty 回補（§5.9 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.9 EP Valid: 刪除草稿 GR 後，PO ReceivedQty 精確回補至刪除前的值</summary>
    [Fact]
    public async Task GapA_DeleteDraftGr_PoReceivedQtyRestored()
    {
        SeedPo("PO005", orderQty: 100m);

        var gr = await _purchaseSvc.CreateGoodsReceiptAsync(BuildGrVm("PO005", receiptQty: 70m), UserId, PlantId);
        var afterCreate = _context.PurchaseOrderLines.Find("PO005", 1)!.ReceivedQty;
        Assert.Equal(70m, afterCreate);

        await _purchaseSvc.DeleteGoodsReceiptAsync(gr.GoodsReceiptId);

        var afterDelete = _context.PurchaseOrderLines.Find("PO005", 1)!.ReceivedQty;
        Assert.Equal(0m, afterDelete, precision: 4);
    }

    /// <summary>§5.9 EP Valid: 刪除後可重新建立同量 GR，不受前次影響</summary>
    [Fact]
    public async Task GapA_DeleteAndRecreateGr_SecondCreateSucceeds()
    {
        SeedPo("PO006", orderQty: 100m);

        var gr1 = await _purchaseSvc.CreateGoodsReceiptAsync(BuildGrVm("PO006", receiptQty: 100m), UserId, PlantId);
        await _purchaseSvc.DeleteGoodsReceiptAsync(gr1.GoodsReceiptId);

        // 刪除後再建同量，應成功
        var gr2 = await _purchaseSvc.CreateGoodsReceiptAsync(BuildGrVm("PO006", receiptQty: 100m), UserId, PlantId);
        Assert.NotNull(gr2);

        var poLine = _context.PurchaseOrderLines.Find("PO006", 1)!;
        Assert.Equal(100m, poLine.ReceivedQty);
    }

    /// <summary>稽核查詢：PO ReceivedQty > OrderQty 的異常資料可被偵測（§1.1.B.1 等效 LINQ）</summary>
    [Fact]
    public void GapA_AuditQuery_ReceivedQtyExceedsOrderQty_Detectable()
    {
        SeedPoWithLine("PO007", orderQty: 100m);

        // 直接植入超收異常（模擬 bug 或外部匯入造成的資料污染）
        var poLine = _context.PurchaseOrderLines.Find("PO007", 1)!;
        poLine.ReceivedQty = 120m;
        _context.SaveChanges();

        var violations = _context.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == "PO007" && l.ReceivedQty > l.OrderQty)
            .ToList();

        Assert.Single(violations);
        Assert.Equal(20m, violations[0].ReceivedQty - violations[0].OrderQty, precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A3：作廢 APInvoice → 未結餘額歸零（§5.9 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.9 EP Valid: 作廢 APInvoice 後，GetOutstandingBalanceAsync 不再計入該發票</summary>
    [Fact]
    public async Task GapA_VoidAPInvoice_OutstandingBalanceDecreases()
    {
        SeedGr("GR-VOID01", receiptQty: 100m, unitPrice: 50m);
        var invoice = await _apInvoiceSvc.GenerateFromGoodsReceiptAsync("GR-VOID01");

        var before = await _apInvoiceSvc.GetOutstandingBalanceAsync(SuppCode, PlantId);
        Assert.True(before > 0m, "作廢前未結餘額應 > 0");

        await _apInvoiceSvc.VoidAsync(invoice.APInvoiceId);

        var after = await _apInvoiceSvc.GetOutstandingBalanceAsync(SuppCode, PlantId);
        Assert.Equal(0m, after, precision: 4);
    }

    /// <summary>§5.9 EP Valid: 作廢已付款的 APInvoice 應被拒絕（已付款保護）</summary>
    [Fact]
    public async Task GapA_VoidPaidAPInvoice_Throws()
    {
        var invoice = new APInvoice
        {
            APInvoiceId  = "API-PAID01",
            SupplierCode = SuppCode,
            InvoiceDate  = new DateOnly(2025, 1, 1),
            DueDate      = new DateOnly(2025, 2, 1),
            TotalAmount  = 500m,
            AmountLocal  = 500m,
            PaidAmount   = 500m,
            Status       = APInvoiceStatus.Paid,
            PlantId      = PlantId,
        };
        _context.APInvoices.Add(invoice);
        _context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _apInvoiceSvc.VoidAsync("API-PAID01"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 輔助方法
    // ════════════════════════════════════════════════════════════════════════

    private void SeedPo(string poId, decimal orderQty)
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = poId,
            OrderDate       = new DateOnly(2025, 1, 1),
            CreatedBy       = UserId,
            SupplierCode    = SuppCode,
            ApprovalStatus  = DocumentApprovalStatus.Approved,
            PlantId         = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = poId, LineNo = 1,
            ProductCode = "MAT001", OrderQty = orderQty, UomCode = "PC", UnitPrice = 100m,
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();
    }

    private void SeedPoWithLine(string poId, decimal orderQty)
        => SeedPo(poId, orderQty);

    private void SeedGrLinkedToPo(string grId, string poId, decimal receiptQty, DocStatus status = DocStatus.Confirmed)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId,
            ReceiptDate    = new DateOnly(2025, 1, 5),
            CreatedBy      = UserId,
            SupplierCode   = SuppCode,
            Status         = status,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId        = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId  = grId, LineNo = 1,
            PurchaseOrderId = poId, PoLineNo = 1,
            ProductCode     = "MAT001", ReceiptQty = receiptQty,
            UomCode         = "PC", StockQty = receiptQty, StockUomCode = "PC",
            UnitPrice       = 100m,
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
            CreatedBy      = UserId,
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
            UnitPrice      = unitPrice,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private GoodsReceiptFormVm BuildGrVm(string poId, decimal receiptQty) => new()
    {
        ReceiptDate  = new DateOnly(2025, 2, 1),
        CreatedBy    = UserId,
        SupplierCode = SuppCode,
        Lines =
        [
            new GoodsReceiptLineVm
            {
                LineNo          = 1,
                PurchaseOrderId = poId,
                PoLineNo        = 1,
                ProductCode     = "MAT001",
                ReceiptQty      = receiptQty,
                StockQty        = receiptQty,
                UomCode         = "PC",
                StockUomCode    = "PC",
                UnitPrice       = 100m,
                LineAmount      = receiptQty * 100m,
                TotalAmount     = receiptQty * 100m,
            }
        ]
    };
}
