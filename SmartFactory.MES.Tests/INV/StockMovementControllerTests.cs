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

/// <summary>StockMovementController 查詢畫面測試（期間、類別、品號、單號、排序、分頁）。</summary>
public class StockMovementControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IExportService>     _mockExport;
    private readonly Mock<IUomService>        _mockUom = new();
    private readonly Mock<ICurrencyService>   _mockCurrency;

    private const string PlantId = "P001";

    public StockMovementControllerTests()
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
        // 倉庫必須一併建：儲位沒有 PlantId，廠別是由所屬倉庫（IPlantEntity）決定的，
        // 儲位查詢也一律經由倉庫收斂到本廠（StorageLocationQueryExtensions.ForCurrentPlant）。
        // 真實資料庫有 FK_StorageLocations_Warehouses_WhCode，孤兒儲位根本存不進去；
        // InMemory 不驗 FK，先前這裡只建儲位不建倉庫，是測試比真實資料寬鬆。
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "倉一", PlantId = PlantId });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "倉二", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH02", LocationCode = "B-01", LocationName = "B-01" });
        // PI and IJ are pre-seeded by HasData — no explicit Add needed

        _context.StockMovements.Add(new StockMovement {
            PlantId = PlantId, TypeCode = "PI", TypeCategory = "Receipt",
            DocNo = "PE202604120001", DocLineNo = 1,
            MovementDate = new DateOnly(2026, 4, 10),
            CreatedAt = new DateTime(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 100, StockUomCode = "PC", StockQty = 100,
            Direction = 1, UnitCost = 5m, TotalCost = 500m,
        });
        _context.StockMovements.Add(new StockMovement {
            PlantId = PlantId, TypeCode = "PI", TypeCategory = "Receipt",
            DocNo = "PE202604120002", DocLineNo = 1,
            MovementDate = new DateOnly(2026, 4, 11),
            CreatedAt = new DateTime(2026, 4, 11, 9, 0, 0, DateTimeKind.Utc),
            ProductCode = "MAT002", LocationCode = "B-01", LotNo = "LOT01",
            UomCode = "PC", Qty = 50, StockUomCode = "PC", StockQty = 50,
            Direction = 1, UnitCost = 8m, TotalCost = 400m,
        });
        _context.StockMovements.Add(new StockMovement {
            PlantId = PlantId, TypeCode = "IJ", TypeCategory = "Adjustment",
            DocNo = "IJ202604120001", DocLineNo = 1,
            MovementDate = new DateOnly(2026, 4, 12),
            CreatedAt = new DateTime(2026, 4, 12, 9, 0, 0, DateTimeKind.Utc),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 5, StockUomCode = "PC", StockQty = 5,
            Direction = -1, UnitCost = 5m, TotalCost = 25m,
        });
        _context.SaveChanges();
    }

    private StockMovementController BuildController()
    {
        var ctrl = new StockMovementController(_context, _mockUser.Object, _mockPerm.Object, _mockExport.Object, _mockUom.Object, _mockCurrency.Object);
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
    public async Task Index_Searched_ShouldReturnAllRows()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Equal(3, list.TotalItems);
    }

    [Fact]
    public async Task Index_FilterByDateRange_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true,
            dateFrom: new DateOnly(2026, 4, 11), dateTo: new DateOnly(2026, 4, 12));

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Equal(2, list.TotalItems);
        Assert.All(list, r => Assert.InRange(r.MovementDate, new DateOnly(2026, 4, 11), new DateOnly(2026, 4, 12)));
    }

    [Fact]
    public async Task Index_FilterByTypeCode_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, typeCode: "IJ");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("IJ", list.First().TypeCode);
        Assert.Equal(-1, list.First().Direction);
    }

    [Fact]
    public async Task Index_FilterByProductCode_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, productCode: "MAT001");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Equal(2, list.TotalItems);
        Assert.All(list, r => Assert.Equal("MAT001", r.ProductCode));
    }

    [Fact]
    public async Task Index_FilterByDocNo_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, docNo: "PE202604120002");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("MAT002", list.First().ProductCode);
    }

    [Fact]
    public async Task Index_FilterByLotNo_ShouldMatch()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, lotNo: "LOT01");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal("LOT01", list.First().LotNo);
    }

    [Fact]
    public async Task Index_DefaultSort_ShouldBeDateDesc()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Equal(new DateOnly(2026, 4, 12), list.First().MovementDate);
        Assert.Equal(new DateOnly(2026, 4, 10), list.Last().MovementDate);
    }

    [Fact]
    public async Task Index_SortByDateAsc_ShouldOrder()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, sortOrder: "date_asc");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Equal(new DateOnly(2026, 4, 10), list.First().MovementDate);
        Assert.Equal(new DateOnly(2026, 4, 12), list.Last().MovementDate);
    }

    [Fact]
    public async Task Index_Pagination_ShouldApply()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, pageSize: 2, pageNumber: 2, sortOrder: "date_asc");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        Assert.Single(list);
        Assert.Equal(new DateOnly(2026, 4, 12), list.First().MovementDate);
        Assert.Equal(2, list.PageIndex);
        Assert.Equal(2, list.TotalPages);
    }

    [Fact]
    public async Task Index_ShouldEnrichWithTypeAndProductAndWarehouse()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: true, productCode: "MAT002");

        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsType<PaginatedList<StockMovementRowVm>>(view.Model!);
        var row = list.First();
        Assert.Equal("進貨", row.TypeName);
        Assert.Equal("測試料 B", row.ProductName);
        Assert.Equal("WH02", row.WhCode);
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
}
