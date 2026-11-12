using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>多階料表 / 多階途程 查詢畫面測試（權限、searched 守門、匯出、列印）。</summary>
public class MultiLevelBomControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IExportService>     _mockExport;
    private readonly Mock<IUomService>        _mockUom = new();
    private readonly Mock<IDateTimeService>   _mockDt = new();

    private const string PlantId = "P001";
    private static readonly DateTime Now = new(2026, 7, 20, 8, 0, 0);

    public MultiLevelBomControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _mockExport = GetMockExportService();
        _mockDt.Setup(d => d.Now).Returns(Now);
        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["PC"] = 0 });

        Seed();
    }

    /// <summary>FG → (MAT×2 + SFG×1)；SFG → MAT-B×3。</summary>
    private void Seed()
    {
        _context.Products.AddRange(
            new Product { ProductCode = "FG",    ProductName = "成品",  StockUom = "PC", RoutingId = "R-FG",  IsActive = true },
            new Product { ProductCode = "SFG",   ProductName = "半成品", StockUom = "PC", RoutingId = "R-SFG", IsActive = true },
            new Product { ProductCode = "MAT",   ProductName = "原料A", StockUom = "PC", IsActive = true },
            new Product { ProductCode = "MAT-B", ProductName = "原料B", StockUom = "PC", IsActive = true });
        _context.Routings.AddRange(
            new Routing { RoutingId = "R-FG",  IsActive = true, PlantId = PlantId },
            new Routing { RoutingId = "R-SFG", IsActive = true, PlantId = PlantId });

        var opFg = new RoutingOperation
        {
            RoutingId = "R-FG", OperationSeq = 10, OperationName = "組裝",
            WorkCenterId = "WC1", UomCode = "PC", TM_UNIT = 1,
        };
        opFg.Boms.Add(new RoutingBom { RoutingId = "R-FG", OperationSeq = 10, BomSeq = 1, ProductCode = "MAT", Quantity = 2, UomCode = "PC" });
        opFg.Boms.Add(new RoutingBom { RoutingId = "R-FG", OperationSeq = 10, BomSeq = 2, ProductCode = "SFG", Quantity = 1, UomCode = "PC" });

        var opSfg = new RoutingOperation
        {
            RoutingId = "R-SFG", OperationSeq = 10, OperationName = "加工",
            WorkCenterId = "WC1", UomCode = "PC", TM_UNIT = 1,
        };
        opSfg.Boms.Add(new RoutingBom { RoutingId = "R-SFG", OperationSeq = 10, BomSeq = 1, ProductCode = "MAT-B", Quantity = 3, UomCode = "PC" });

        _context.Set<RoutingOperation>().AddRange(opFg, opSfg);
        _context.Set<WorkCenter>().Add(new WorkCenter { WorkCenterCode = "WC1", WorkCenterName = "組裝線", PlantId = PlantId });
        _context.SaveChanges();
    }

    private MultiLevelBomController BuildBomController()
    {
        var ctrl = new MultiLevelBomController(
            _context, _mockUser.Object, _mockPerm.Object, _mockExport.Object,
            _mockDt.Object, _mockUom.Object, new MultiLevelBomService(_context));
        ctrl.ControllerContext = GetControllerContextWithServices(
            permService: _mockPerm.Object, userContext: _mockUser.Object,
            dateTimeService: _mockDt.Object);
        return ctrl;
    }

    private MultiLevelRoutingController BuildRoutingController()
    {
        var ctrl = new MultiLevelRoutingController(
            _context, _mockUser.Object, _mockPerm.Object, _mockExport.Object,
            _mockDt.Object, _mockUom.Object, new MultiLevelBomService(_context));
        ctrl.ControllerContext = GetControllerContextWithServices(
            permService: _mockPerm.Object, userContext: _mockUser.Object,
            dateTimeService: _mockDt.Object);
        return ctrl;
    }

    private static void MockUrl(Controller ctrl)
    {
        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/x");
        ctrl.Url = url.Object;
    }

    // ── 多階料表 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bom_Index_NotSearched_ReturnsNullModel()
    {
        var view = Assert.IsType<ViewResult>(await BuildBomController().Index(null, searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Bom_Index_NoReadPermission_ReturnsForbid()
    {
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);
        Assert.IsType<ForbidResult>(await BuildBomController().Index("FG", searched: true));
    }

    [Fact]
    public async Task Bom_Index_Searched_ReturnsExplosionRows()
    {
        var view = Assert.IsType<ViewResult>(await BuildBomController().Index("FG", searched: true));
        var model = Assert.IsType<MultiLevelExplosionResult>(view.Model);

        Assert.Equal(3, model.BomRows.Count);                       // MAT, SFG, MAT-B
        Assert.Equal(2, model.BomRows.Single(r => r.ProductCode == "MAT-B").Level);
    }

    [Fact]
    public async Task Bom_Export_NoPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        Assert.IsType<ForbidResult>(await BuildBomController().Export("FG"));
    }

    [Fact]
    public async Task Bom_Export_ReturnsXlsxFile()
    {
        var file = Assert.IsType<FileContentResult>(await BuildBomController().Export("FG"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
    }

    [Fact]
    public async Task Bom_Print_ReturnsViewWithModel()
    {
        var ctrl = BuildBomController();
        MockUrl(ctrl);
        var view = Assert.IsType<ViewResult>(await ctrl.Print("FG"));
        var model = Assert.IsType<MultiLevelExplosionResult>(view.Model);
        Assert.Equal("FG", model.RootProductCode);
    }

    // ── 多階途程 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Routing_Index_NotSearched_ReturnsNullModel()
    {
        var view = Assert.IsType<ViewResult>(await BuildRoutingController().Index(null, searched: false));
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Routing_Index_NoReadPermission_ReturnsForbid()
    {
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);
        Assert.IsType<ForbidResult>(await BuildRoutingController().Index("FG", searched: true));
    }

    [Fact]
    public async Task Routing_Index_Searched_ReturnsNodesWithOperationsAndWcNames()
    {
        var ctrl = BuildRoutingController();
        var view = Assert.IsType<ViewResult>(await ctrl.Index("FG", searched: true));
        var model = Assert.IsType<MultiLevelExplosionResult>(view.Model);

        Assert.Equal(2, model.RoutingNodes.Count);                  // FG + SFG
        Assert.Single(model.RoutingNodes[0].Operations);
        Assert.Equal(2, model.RoutingNodes[0].Operations[0].Boms.Count);

        var wcNames = Assert.IsType<Dictionary<string, string>>(view.ViewData["WorkCenterNames"]);
        Assert.Equal("組裝線", wcNames["WC1"]);
    }

    [Fact]
    public async Task Routing_Export_ReturnsXlsxFile()
    {
        var file = Assert.IsType<FileContentResult>(await BuildRoutingController().Export("FG"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
    }

    [Fact]
    public async Task Routing_Print_ReturnsViewWithModel()
    {
        var ctrl = BuildRoutingController();
        MockUrl(ctrl);
        var view = Assert.IsType<ViewResult>(await ctrl.Print("FG"));
        var model = Assert.IsType<MultiLevelExplosionResult>(view.Model);
        Assert.Equal(2, model.RoutingNodes.Count);
    }
}
