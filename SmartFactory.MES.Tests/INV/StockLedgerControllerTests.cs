using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>StockLedgerController：期初結存、逐筆入/出分欄、逐筆與期末結存。</summary>
public class StockLedgerControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IExportService>     _mockExport;

    private const string PlantId = "P001";

    public StockLedgerControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _mockExport = GetMockExportService();
        Seed();
    }

    private void Seed()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "測試料 A", StockUom = "PC" });

        void Mv(DateOnly date, string typeCode, int dir, decimal qty)
            => _context.StockMovements.Add(new StockMovement
            {
                PlantId = PlantId, TypeCode = typeCode, DocNo = $"D{date:yyyyMMdd}", DocLineNo = 1,
                MovementDate = date, CreatedAt = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
                UomCode = "PC", Qty = qty, StockUomCode = "PC", StockQty = qty,
                Direction = dir, UnitCost = 5m, TotalCost = qty * 5m, CreatedBy = "user1",
            });

        Mv(new DateOnly(2026, 4, 1), "PI", 1, 100);   // 期初前 → 計入期初
        Mv(new DateOnly(2026, 4, 10), "PF", -1, 30);  // 期間內 出庫
        Mv(new DateOnly(2026, 4, 20), "PI", 1, 50);   // 期間內 入庫
        _context.SaveChanges();
    }

    private StockLedgerController BuildController()
    {
        var ctrl = new StockLedgerController(_context, _mockUser.Object, _mockPerm.Object, _mockExport.Object);
        ctrl.ControllerContext = GetControllerContext(userId: "user1");
        return ctrl;
    }

    [Fact]
    public async Task Index_ComputesOpeningRunningAndClosingBalance()
    {
        var ctrl = BuildController();

        var result = await ctrl.Index(
            productCode: "MAT001",
            dateFrom: new DateOnly(2026, 4, 5),
            dateTo: new DateOnly(2026, 4, 30),
            locationCode: null, lotNo: null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<StockLedgerVm>(view.Model);

        Assert.Equal(100m, vm.OpeningBalance);           // 期初 = 起始日前累計
        Assert.Equal(2, vm.Rows.Count);                  // 期間內 2 筆

        Assert.Equal(30m, vm.Rows[0].OutQty);            // 04-10 出庫 30
        Assert.Equal(0m,  vm.Rows[0].InQty);
        Assert.Equal(70m, vm.Rows[0].Balance);           // 100 - 30

        Assert.Equal(50m,  vm.Rows[1].InQty);            // 04-20 入庫 50
        Assert.Equal(0m,   vm.Rows[1].OutQty);
        Assert.Equal(120m, vm.Rows[1].Balance);          // 70 + 50

        Assert.Equal(50m,  vm.TotalIn);
        Assert.Equal(30m,  vm.TotalOut);
        Assert.Equal(120m, vm.ClosingBalance);
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_NoProduct_ReturnsNullModelEvenIfSearched()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(productCode: null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }
}
