using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 驗證 PurchaseService 在儲存 PO/GR/GRT 時，後端會以權威匯率覆寫前端傳入的 ExchangeRate。
/// 規則：依 CurrencyCode + 單據日期，查 ≤ documentDate 最近一筆 RateType.Buy。
/// </summary>
public class PurchaseServiceRateResolveTests : TestBase
{
    private readonly Mock<IDocNumberService> _mockDocNo;
    private readonly Mock<IDateTimeService>  _mockDt;
    private readonly Mock<IUomService>       _mockUom;
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private readonly PurchaseService         _service;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public PurchaseServiceRateResolveTests()
    {
        _mockDocNo   = new Mock<IDocNumberService>();
        _mockDt      = new Mock<IDateTimeService>();
        _mockUom     = new Mock<IUomService>();
        _mockCurrency = GetMockCurrencyService();

        // UOM 直通：保持 qty 不換算
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, decimal, CancellationToken, IUomService, decimal>((u, q, ct) => q);
        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync<string, string, string, decimal, CancellationToken, IUomService, decimal>((p, f, t, q, ct) => q);

        _service = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));

        _context.CurrentPlantId = PlantId;
        _context.Products.Add(new Product { ProductCode = "P001", ProductName = "P", PurchaseUom = "PC", StockUom = "PC" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Companies.Add(new Company { CompanyCode = "S001", CompanyName = "Supplier", IsSupplier = true });
        _context.SaveChanges();
    }

    private static PurchaseOrderFormVm BuildPoVm(string currencyCode, decimal frontendRate, DateOnly orderDate)
        => new()
        {
            OrderDate = orderDate,
            SupplierCode = "S001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new()
                {
                    LineNo = 1, ProductCode = "P001", DeliveryDate = orderDate,
                    OrderQty = 10, UomCode = "PC", StockQty = 10, StockUomCode = "PC",
                    UnitPrice = 100m,
                    CurrencyCode = currencyCode,
                    ExchangeRate = frontendRate,  // 前端傳入的舊匯率
                    LineAmount = 1000m, TotalAmount = 1000m,
                }
            }
        };

    [Fact]
    public async Task CreatePurchaseOrder_OverwritesFrontendRateWithBackendRate()
    {
        var orderDate = new DateOnly(2026, 5, 20);
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, orderDate, It.IsAny<CancellationToken>()))
                  .ReturnsAsync("PO-001");

        // 後端 ResolvePurchaseRateAsync 會呼叫 ICurrencyService.GetRateAsync(USD, orderDate, Buy)
        _mockCurrency.Setup(c => c.GetRateAsync("USD", orderDate, RateType.Buy))
                     .ReturnsAsync(31.5m);

        var vm = BuildPoVm("USD", frontendRate: 999m /* 故意給錯誤值 */, orderDate);

        var po = await _service.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        // 後端應以權威匯率覆寫
        Assert.Equal(31.5m, po.Lines.Single().ExchangeRate);
        _mockCurrency.Verify(c => c.GetRateAsync("USD", orderDate, RateType.Buy), Times.Once);
    }

    [Fact]
    public async Task CreatePurchaseOrder_BaseCurrency_ReturnsRateOne()
    {
        var orderDate = new DateOnly(2026, 5, 20);
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, orderDate, It.IsAny<CancellationToken>()))
                  .ReturnsAsync("PO-002");

        // CurrencyService.GetRateAsync 對本幣會回 1（這裡用 Mock 模擬）
        _mockCurrency.Setup(c => c.GetRateAsync("TWD", orderDate, RateType.Buy)).ReturnsAsync(1m);

        var vm = BuildPoVm("TWD", frontendRate: 5m, orderDate);

        var po = await _service.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        Assert.Equal(1m, po.Lines.Single().ExchangeRate);
    }

    [Fact]
    public async Task CreatePurchaseOrder_RateNotFound_FallsBackToClientValue()
    {
        var orderDate = new DateOnly(2026, 5, 20);
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, orderDate, It.IsAny<CancellationToken>()))
                  .ReturnsAsync("PO-003");

        // 後端找不到匯率
        _mockCurrency.Setup(c => c.GetRateAsync("ZWL", orderDate, RateType.Buy))
                     .ReturnsAsync((decimal?)null);

        var vm = BuildPoVm("ZWL", frontendRate: 42m, orderDate);

        var po = await _service.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        // Fallback 保留前端傳入值，避免覆寫為 1 造成更糟錯亂
        Assert.Equal(42m, po.Lines.Single().ExchangeRate);
    }

    [Fact]
    public async Task CreatePurchaseOrder_EmptyCurrency_KeepsClientRate()
    {
        var orderDate = new DateOnly(2026, 5, 20);
        _mockDocNo.Setup(d => d.NextAsync("PurchaseOrder", PlantId, orderDate, It.IsAny<CancellationToken>()))
                  .ReturnsAsync("PO-004");

        var vm = BuildPoVm("", frontendRate: 7m, orderDate);

        var po = await _service.CreatePurchaseOrderAsync(vm, UserId, PlantId);

        // CurrencyCode 為空時直接保留 client rate，且不呼叫 service
        Assert.Equal(7m, po.Lines.Single().ExchangeRate);
        _mockCurrency.Verify(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()), Times.Never);
    }
}
