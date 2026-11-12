using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>InventoryBalanceController 查詢畫面測試（篩選、排序、搜尋狀態控制）。</summary>
public class InventoryBalanceControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IExportService>     _mockExport;
    private readonly Mock<IUomService>        _mockUom = new();
    private readonly Mock<ICurrencyService>   _mockCurrency;

    private const string PlantId = "P001";

    public InventoryBalanceControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _mockExport = GetMockExportService();
        _mockCurrency = GetMockCurrencyService();
        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["PC"] = 0 });

        Seed();
    }

    private void Seed()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "測試料 A", StockUom = "PC" });
        _context.Products.Add(new Product { ProductCode = "MAT002", ProductName = "測試料 B", StockUom = "PC" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "成品倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH02", LocationCode = "B-01", LocationName = "B-01" });

        _context.Inventories.Add(new Inventory {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 100, QtyReserved = 10, UnitCost = 5m,   // QtyReserved 是沒人維護的舊欄位，畫面不再讀它
        });
        _context.Inventories.Add(new Inventory {
            PlantId = PlantId, ProductCode = "MAT002", LocationCode = "B-01", LotNo = "LOT01",
            QtyOnHand = 50, QtyReserved = 0, UnitCost = 8m,
        });
        _context.Inventories.Add(new Inventory {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "B-01", LotNo = "",
            QtyOnHand = 0, QtyReserved = 0, UnitCost = 0m,   // 零庫存（預設排除）
        });
        _context.SaveChanges();
    }

    private InventoryBalanceController BuildController()
    {
        var ctrl = new InventoryBalanceController(_context, _mockUser.Object, _mockPerm.Object, _mockExport.Object, _mockUom.Object, _mockCurrency.Object,
            new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));
        ctrl.ControllerContext = GetControllerContext(userId: "user1");
        return ctrl;
    }

    [Fact]
    public async Task Index_NotSearched_ShouldReturnEmptyView()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ShouldExcludeZeroQty_ByDefault()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Equal(2, list.TotalItems);   // MAT001@A-01, MAT002@B-01
    }

    [Fact]
    public async Task Index_IncludeZero_ShouldReturnAllRows()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, includeZero: true);

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Equal(3, list.TotalItems);
    }

    [Fact]
    public async Task Index_FilterByProductCode_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, productCode: "MAT001");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("MAT001", list.First().ProductCode);
    }

    [Fact]
    public async Task Index_FilterByWhCode_ShouldOnlyReturnMatchingWarehouse()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, whCode: "WH02");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("WH02", list.First().WhCode);
    }

    [Fact]
    public async Task Index_FilterByLotNo_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, lotNo: "LOT01");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("LOT01", list.First().LotNo);
    }

    [Fact]
    public async Task Index_SortByQtyAsc_ShouldOrder()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, sortOrder: "qty_asc");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Equal(50m, list.First().QtyOnHand);
        Assert.Equal(100m, list.Last().QtyOnHand);
    }

    [Fact]
    public async Task Index_Pagination_ShouldApply()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, pageSize: 1, pageNumber: 2, sortOrder: "qty_asc");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<InventoryBalanceRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal(100m, list.First().QtyOnHand);
        Assert.Equal(2, list.PageIndex);
        Assert.Equal(2, list.TotalPages);
    }

    [Fact]
    public async Task Export_ShouldReturnFile()
    {
        var ctrl = BuildController();
        var result = await ctrl.Export("xlsx");

        Assert.IsType<FileContentResult>(result);
    }

    [Fact]
    public async Task Export_NoPermission_ShouldForbid()
    {
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var ctrl = BuildController();
        var result = await ctrl.Export("xlsx");

        Assert.IsType<ForbidResult>(result);
    }

    /// <summary>
    /// 預留只在品號彙總（全廠）顯示，取 InventoryReservation（與 MRP／投產同口徑），不取逐儲位的 Inventory.QtyReserved。
    /// 2026-10-27：原本逐儲位列顯示 QtyReserved（從沒人寫入，恆 0），可用量恆等於在手量。
    /// </summary>
    [Fact]
    public async Task Index_ProductSummary_UsesPlantReservations_NotInventoryQtyReserved()
    {
        _context.Set<SmartFactory.MES.Modules.INV.Models.InventoryReservation>().Add(new SmartFactory.MES.Modules.INV.Models.InventoryReservation
        {
            PlantId = PlantId, ProductCode = "MAT001", ReservedQty = 30m, SourceDocId = "WO-1",
            Category = SmartFactory.MES.Modules.INV.Models.ReservationCategory.OnHand,
            Status = SmartFactory.MES.Modules.INV.Models.ReservationStatus.Active, RequiredDate = DateTime.UtcNow,
        });
        _context.SaveChanges();

        var view = Assert.IsType<ViewResult>(await BuildController().Index(searched: true));
        var summary = Assert.IsType<List<InventoryBalanceProductSummaryVm>>(view.ViewData["ProductSummary"]);

        var mat1 = summary.Single(s => s.ProductCode == "MAT001");
        Assert.Equal(100m, mat1.PlantOnHand);
        Assert.Equal(30m, mat1.PlantReserved);     // 不是 Inventory.QtyReserved 的 10
        Assert.Equal(70m, mat1.PlantAvailable);
        Assert.Equal(0m, summary.Single(s => s.ProductCode == "MAT002").PlantReserved);
    }
}
