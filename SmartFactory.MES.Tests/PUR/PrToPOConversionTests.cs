using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// §5.14 PR→PO 轉換數量控管：累計 OrderedQty 回寫、超轉攔截、已關閉明細防護
/// </summary>
public class PrToPOConversionTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "S001";
    private const string UserId   = "U1";

    private readonly PurchaseService        _svc;
    private readonly Mock<IDocNumberService> _mockDocNo   = new();
    private readonly Mock<IDateTimeService>  _mockDt      = new();
    private readonly Mock<IUomService>       _mockUom     = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;

    private int _poSeq;
    private int _prSeq;

    public PrToPOConversionTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"PO{++_poSeq:D6}"));
        _mockDocNo.Setup(d => d.NextAsync("PurchaseRequest", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
                  .Returns(() => Task.FromResult($"PR{++_prSeq:D6}"));
        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, string _, decimal q, CancellationToken _) => q);
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, decimal q, CancellationToken _) => q);
        _mockCurrency = GetMockCurrencyService();
        _mockCurrency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                     .ReturnsAsync((decimal?)null);

        _svc = new PurchaseService(
            _context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));

        SeedBaseData();
    }

    private void SeedBaseData()
    {
        _context.Companies.Add(new Company
        {
            CompanyCode = SuppCode, CompanyName = "測試供應商",
            IsSupplier  = true,     IsActive    = true
        });
        _context.Products.Add(new Product { ProductCode = "P-001", IsActive = true });
        _context.Products.Add(new Product { ProductCode = "P-002", IsActive = true });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.SaveChanges();
    }

    private void SeedPr(string prId, decimal requestQty, string productCode = "P-001", bool lineIsClosed = false)
    {
        _context.PurchaseRequests.Add(new PurchaseRequest
        {
            PurchaseRequestId = prId,
            RequestDate       = new DateOnly(2026, 1, 1),
            RequestedBy       = UserId,
            ApprovalStatus    = DocumentApprovalStatus.Approved,
            PlantId           = PlantId,
        });
        _context.PurchaseRequestLines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = prId,
            LineNo            = 1,
            ProductCode       = productCode,
            RequestQty        = requestQty,
            UomCode           = "PC",
            OrderedQty        = 0m,
            IsClosed          = lineIsClosed,
        });
        _context.SaveChanges();
    }

    private PurchaseOrderFormVm BuildPoVm(string prId, decimal orderQty,
        string productCode = "P-001", int prLineNo = 1)
        => new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2026, 1, 10),
            SupplierCode = SuppCode,
            Lines        = new List<PurchaseOrderLineVm>
            {
                new PurchaseOrderLineVm
                {
                    LineNo            = 1,
                    PurchaseRequestId = prId,
                    PrLineNo          = prLineNo,
                    ProductCode       = productCode,
                    OrderQty          = orderQty,
                    UomCode           = "PC",
                    UnitPrice         = 100m,
                    LineAmount        = orderQty * 100m,
                    TotalAmount       = orderQty * 100m,
                }
            }
        };

    // ════════════════════════════════════════════════════════════════════════
    // B1：完整轉單 – OrderedQty 全量回寫
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Valid: 一次完整轉單，PR OrderedQty 應等於 RequestQty</summary>
    [Fact]
    public async Task Convert_FullQty_OrderedQtyEqualsRequestQty()
    {
        SeedPr("PR-001", requestQty: 100m);

        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-001", 100m), UserId, PlantId);

        var prLine = _context.PurchaseRequestLines.Find("PR-001", 1)!;
        Assert.Equal(100m, prLine.OrderedQty);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B2：多 PR 合併 PO – 各 OrderedQty 獨立回寫
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Valid: 兩筆 PR 明細合併一張 PO，各自 OrderedQty 獨立更新</summary>
    [Fact]
    public async Task Convert_TwoPrLinesMergedIntoPO_EachOrderedQtyUpdatedIndependently()
    {
        SeedPr("PR-010", requestQty: 50m,  productCode: "P-001");
        SeedPr("PR-011", requestQty: 80m,  productCode: "P-002");

        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2026, 1, 10),
            SupplierCode = SuppCode,
            Lines        = new List<PurchaseOrderLineVm>
            {
                new PurchaseOrderLineVm
                {
                    LineNo = 1, PurchaseRequestId = "PR-010", PrLineNo = 1,
                    ProductCode = "P-001", OrderQty = 30m, UomCode = "PC",
                    UnitPrice = 100m, LineAmount = 3000m, TotalAmount = 3000m,
                },
                new PurchaseOrderLineVm
                {
                    LineNo = 2, PurchaseRequestId = "PR-011", PrLineNo = 1,
                    ProductCode = "P-002", OrderQty = 40m, UomCode = "PC",
                    UnitPrice = 200m, LineAmount = 8000m, TotalAmount = 8000m,
                },
            }
        };

        await _svc.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        Assert.Equal(30m, _context.PurchaseRequestLines.Find("PR-010", 1)!.OrderedQty);
        Assert.Equal(40m, _context.PurchaseRequestLines.Find("PR-011", 1)!.OrderedQty);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B3：分批轉單 – 第二張 PO 可補足剩餘量
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Valid: 分兩批轉採購，第二批可補足至 RequestQty</summary>
    [Fact]
    public async Task Convert_TwoBatches_SecondBatchFillsRemainder()
    {
        SeedPr("PR-020", requestQty: 100m);

        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-020", 60m), UserId, PlantId);
        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-020", 40m), UserId, PlantId);

        var prLine = _context.PurchaseRequestLines.Find("PR-020", 1)!;
        Assert.Equal(100m, prLine.OrderedQty);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B4：刪除 PO → PR OrderedQty 回補
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Valid: 刪除草稿 PO 後，PR OrderedQty 應精確回補</summary>
    [Fact]
    public async Task Convert_DeletePO_OrderedQtyRestored()
    {
        SeedPr("PR-030", requestQty: 100m);

        var po = await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-030", 60m), UserId, PlantId);
        var afterCreate = _context.PurchaseRequestLines.Find("PR-030", 1)!.OrderedQty;
        Assert.Equal(60m, afterCreate);

        await _svc.DeletePurchaseOrderAsync(po.PurchaseOrderId);

        var afterDelete = _context.PurchaseRequestLines.Find("PR-030", 1)!.OrderedQty;
        Assert.Equal(0m, afterDelete);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B5：超轉可用量 → 拒絕
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Invalid: 單次轉採購超過 RequestQty → 拒絕</summary>
    [Fact]
    public async Task Convert_ExceedsRequestQty_Throws()
    {
        SeedPr("PR-040", requestQty: 100m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-040", 100.0001m), UserId, PlantId));
    }

    /// <summary>§5.14 EP Invalid: 已轉 60，再轉 41 → 累計 101 > 100 → 拒絕</summary>
    [Fact]
    public async Task Convert_CumulativeExceedsRequestQty_Throws()
    {
        SeedPr("PR-041", requestQty: 100m);
        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-041", 60m), UserId, PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-041", 41m), UserId, PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // B6：已關閉明細 → 拒絕
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 EP Invalid: IsClosed=true 的明細不可再轉採購</summary>
    [Fact]
    public async Task Convert_ClosedLine_Throws()
    {
        SeedPr("PR-050", requestQty: 100m, lineIsClosed: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-050", 50m), UserId, PlantId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // B7：BVA – 精確邊界
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.14 BVA: ReleasedQty + 本次 = RequestQty（60+40=100）→ 允許</summary>
    [Fact]
    public async Task Convert_BVA_ExactlyAtRequestQty_Allowed()
    {
        SeedPr("PR-060", requestQty: 100m);
        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-060", 60m), UserId, PlantId);

        // 40 → 累計 100 = 100，應允許
        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-060", 40m), UserId, PlantId);

        Assert.Equal(100m, _context.PurchaseRequestLines.Find("PR-060", 1)!.OrderedQty);
    }

    /// <summary>§5.14 BVA: ReleasedQty + 本次 = RequestQty + 0.0001（60+40.0001=100.0001）→ 拒絕</summary>
    [Fact]
    public async Task Convert_BVA_ExceedsBy0001_Throws()
    {
        SeedPr("PR-061", requestQty: 100m);
        await _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-061", 60m), UserId, PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreatePurchaseOrderAsync(BuildPoVm("PR-061", 40.0001m), UserId, PlantId));
    }
}
