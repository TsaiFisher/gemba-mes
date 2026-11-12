using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

public class ProductControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IUserContext> _mockUserCtx;
    private readonly Mock<IApprovalService> _mockApproval;
    private readonly ProductController _controller;

    public ProductControllerTests()
    {
        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _mockUserCtx = new Mock<IUserContext>();
        _mockUserCtx.Setup(x => x.PlantId).Returns("P001");
        _mockUserCtx.Setup(x => x.UserId).Returns("User1");

        _mockApproval = new Mock<IApprovalService>();
        _mockApproval.Setup(x => x.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        // Set CurrentPlantId so the IPlantEntity global query filter matches seeded data
        _context.CurrentPlantId = "P001";

        // 品號的產品類別與單位現在會被伺服端檢核（存在／啟用；見 MasterReferenceGuard）。
        // 這幾支測試原本引用一個從未建立過的 CAT1，靠的是「當時沒人驗」——
        // 補上主檔才是它們真正想表達的前置條件：一張引用合法主檔的品號。
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT1", CategoryName = "測試類別", IsActive = true,
        });
        _context.SaveChanges();

        var mockUom = new Mock<IUomService>();
        mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal q, CancellationToken _) => q);
        mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());
        var mockCurrency = GetMockCurrencyService();

        _controller = new ProductController(
            _context,
            _mockApproval.Object,
            _mockUserCtx.Object,
            GetMockExportService().Object,
            _mockPerm.Object,
            mockUom.Object,
            mockCurrency.Object,
            GetMockLocalizerFactory().Object);

        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax(Controller ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    // ── Test 1: Index_NotSearched_ReturnsNullModel ────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Test 2: Index_Searched_ReturnsPaginatedList ───────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.Products.AddRange(
            new Product { ProductCode = "PRD1", ProductName = "Widget A" },
            new Product { ProductCode = "PRD2", ProductName = "Widget B" });
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Product>>(view.Model);
        Assert.True(model.TotalItems >= 1);
    }

    // ── Test 3: Index_SetsCanCreateInViewBag ─────────────────────────────────

    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True((bool)view.ViewData["CanCreate"]!);
    }

    // ── Test 4: Create_GET_ReturnsView ────────────────────────────────────────

    [Fact]
    public async Task Create_GET_ReturnsView()
    {
        var result = await _controller.Create();

        Assert.IsType<ViewResult>(result);
    }

    // ── Test 5: Create_POST_ValidModel_SavesRecord ────────────────────────────

    [Fact]
    public async Task Create_POST_ValidModel_SavesRecord()
    {
        SetAjax(_controller);
        var model = new Product { ProductCode = "NEW1", ProductName = "Widget A", IsActive = true, CategoryCode = "CAT1" };

        var result = await _controller.Create(model, null);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Products.Any());
        var saved = _context.Products.First(p => p.ProductCode == "NEW1");
        Assert.Equal(DocumentApprovalStatus.Approved, saved.ApprovalStatus);
    }

    // ── Test 6: Create_POST_DuplicateCode_ReturnsError ────────────────────────

    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Products.Add(new Product { ProductCode = "P001", ProductName = "Existing" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var model = new Product { ProductCode = "P001", ProductName = "Duplicate" };

        var result = await _controller.Create(model, null);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Test 7: Edit_GET_ExistingRecord_ReturnsView ───────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Products.Add(new Product { ProductCode = "P001", ProductName = "Widget A" });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("P001");

        Assert.IsType<ViewResult>(result);
    }

    // ── Test 8: Edit_GET_NotFound_Returns404 ─────────────────────────────────

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("NONEXISTENT");

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Test 9: Edit_POST_ValidModel_UpdatesRecord ────────────────────────────

    [Fact]
    public async Task Edit_POST_ValidModel_UpdatesRecord()
    {
        _context.Products.Add(new Product { ProductCode = "EDT1", ProductName = "Old Name", CategoryCode = "CAT1" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var updated = new Product { ProductCode = "EDT1", ProductName = "New Name", IsActive = true, CategoryCode = "CAT1" };

        var result = await _controller.Edit("EDT1", updated, null);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        var saved = _context.Products.First(p => p.ProductCode == "EDT1");
        Assert.Equal("New Name", saved.ProductName);
    }

    // ── Test 10: Delete_RemovesRecord_ReturnsSuccessJson ──────────────────────

    [Fact]
    public async Task Delete_RemovesRecord_ReturnsSuccessJson()
    {
        _context.Products.Add(new Product { ProductCode = "P001", ProductName = "To Delete" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);

        var result = await _controller.DeleteConfirmed("P001");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Empty(_context.Products);
    }

    // ── Test 10b: Delete_ProductUsedInRouting_ReturnsError ────────────────────

    [Fact]
    public async Task Delete_ProductUsedInRouting_ReturnsError()
    {
        _context.Products.Add(new Product { ProductCode = "RTP1", ProductName = "In Routing" });
        _context.Routings.Add(new Routing { RoutingId = "RT001", ProductCode = "RTP1" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("RTP1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Products.Any(p => p.ProductCode == "RTP1"));
    }

    // ── Test 10c: Delete_ProductUsedInRoutingBom_ReturnsError ─────────────────

    [Fact]
    public async Task Delete_ProductUsedInRoutingBom_ReturnsError()
    {
        _context.Products.Add(new Product { ProductCode = "BOMP1", ProductName = "In RoutingBom" });
        _context.Routings.Add(new Routing { RoutingId = "RT002", ProductCode = "OTHERP" });
        _context.RoutingBoms.Add(new RoutingBom
        {
            RoutingId = "RT002", OperationSeq = 1, BomSeq = 1,
            ProductCode = "BOMP1", Quantity = 1, UomCode = "PCS",
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("BOMP1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Products.Any(p => p.ProductCode == "BOMP1"));
    }

    // ── Test 10d: Delete_ProductWithInventoryOnHand_ReturnsError ──────────────

    [Fact]
    public async Task Delete_ProductWithInventoryOnHand_ReturnsError()
    {
        _context.Products.Add(new Product { ProductCode = "INVP1", ProductName = "Has Stock" });
        _context.Inventories.Add(new Inventory
        {
            PlantId = "P001", ProductCode = "INVP1", LocationCode = "LOC1", LotNo = "", QtyOnHand = 10,
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("INVP1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Products.Any(p => p.ProductCode == "INVP1"));
    }

    // ── Test 10e: Delete_ProductWithZeroInventoryBalance_Succeeds ─────────────

    [Fact]
    public async Task Delete_ProductWithZeroInventoryBalance_Succeeds()
    {
        _context.Products.Add(new Product { ProductCode = "ZEROP1", ProductName = "Zero Stock" });
        _context.Inventories.Add(new Inventory
        {
            PlantId = "P001", ProductCode = "ZEROP1", LocationCode = "LOC1", LotNo = "", QtyOnHand = 0, QtyReserved = 0,
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("ZEROP1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(_context.Products.Any(p => p.ProductCode == "ZEROP1"));
    }

    // ── Test 11: Export_WithoutPrintPermission_ReturnsForbid ─────────────────

    [Fact]
    public async Task Export_WithoutPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null, null);

        Assert.IsType<ForbidResult>(result);
    }

    // ── Extra: Create_POST_WithSelectedCategories_SavesCategories ────────────

    [Fact]
    public async Task Create_POST_WithSelectedCategories_SavesCategories()
    {
        _context.ProductCategories.AddRange(
            new ProductCategory { CategoryCode = "CAT_A", CategoryName = "A", IsActive = true },
            new ProductCategory { CategoryCode = "CAT_B", CategoryName = "B", IsActive = true });
        _context.SaveChanges();

        SetAjax(_controller);
        var model = new Product { ProductCode = "P001", ProductName = "Widget A", IsActive = true, CategoryCode = "CAT_A" };
        var categories = new[] { "CAT_A", "CAT_B" };

        var result = await _controller.Create(model, categories);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Equal(2, _context.ProductCategoryTags.Count());
    }
}
