using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class ProductCategoryControllerTests : TestBase
{
    private ProductCategoryController CreateController(Mock<IPermissionService>? perm = null, bool ajax = false)
    {
        perm ??= FullPermMock();
        var mockUserCtx = new Mock<IUserContext>();
        mockUserCtx.Setup(x => x.PlantId).Returns("P001");

        // IPlantEntity global query filter reads context.CurrentPlantId at query time
        _context.CurrentPlantId = "P001";

        var ctrl = new ProductCategoryController(
            _context, mockUserCtx.Object, GetMockExportService().Object, perm.Object, GetMockLocalizerFactory().Object);
        ctrl.ControllerContext = GetControllerContext();
        if (ajax)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return ctrl;
    }

    private static Mock<IPermissionService> FullPermMock()
    {
        var m = new Mock<IPermissionService>();
        m.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        m.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        m.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        m.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        m.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        return m;
    }

    /// <summary>
    /// 唯讀角色：進得了 Index（R23 讀取閘門走 CanRead），但不得新增/修改/刪除/列印。
    /// CanRead 若一併給 false，Index 會回 Forbid 而測不到 ViewBag 旗標本身。
    /// </summary>
    private static Mock<IPermissionService> NoPermMock()
    {
        var m = new Mock<IPermissionService>();
        m.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        m.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(false);
        m.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(false);
        m.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(false);
        m.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        return m;
    }

    // ── Index ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await CreateController().Index(null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "原料類",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await CreateController().Index(null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<PaginatedList<ProductCategory>>(view.Model);
        Assert.Equal(1, model.TotalItems);
    }

    [Fact]
    public async Task Index_SearchTerm_FiltersCorrectly()
    {
        // Use ASCII names to avoid potential InMemory LINQ evaluation quirks with multi-byte chars
        _context.ProductCategories.AddRange(
            new ProductCategory { CategoryCode = "RAW", CategoryName = "RawMaterial", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new ProductCategory { CategoryCode = "FIN", CategoryName = "FinishedGoods", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _context.SaveChangesAsync();

        var result = await CreateController().Index("raw", null, null, null, searched: true);
        var model = Assert.IsAssignableFrom<PaginatedList<ProductCategory>>(
            Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(1, model.TotalItems);
        Assert.Equal("RAW", model.First().CategoryCode);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Get_ReturnsPartialViewWhenAjax()
    {
        var result = await CreateController(ajax: true).Create();
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_ProductCategoryForm", view.ViewName);
    }

    [Fact]
    public async Task Create_Post_AddsRecord_AndReturnsSuccessJson()
    {
        var ctrl = CreateController(ajax: true);  // sets _context.CurrentPlantId = "P001"
        var model = new ProductCategory
        {
            CategoryCode = "NEW01", CategoryName = "NewCategory",
            CostMethod = CostMethod.Fifo, IsActive = true
        };

        var result = await ctrl.Create(model);
        var json = Assert.IsType<JsonResult>(result);
        dynamic data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data));
        Assert.Equal(1, _context.ProductCategories.Count());
    }

    [Fact]
    public async Task Create_Post_DuplicateCode_ReturnsValidationError()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "DUP", CategoryName = "已存在",            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.Create(new ProductCategory { CategoryCode = "DUP", CategoryName = "重複" });

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Edit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_Get_ExistingRecord_ReturnsForm()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "E01", CategoryName = "編輯類別",            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await CreateController(ajax: true).Edit("E01");
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_ProductCategoryForm", view.ViewName);
    }

    [Fact]
    public async Task Edit_Get_NotFound_Returns404()
    {
        var result = await CreateController().Edit("NONE");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_UpdatesRecord()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "U01", CategoryName = "舊名稱",            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var updated = new ProductCategory { CategoryCode = "U01", CategoryName = "新名稱", IsActive = true };
        var result = await CreateController(ajax: true).Edit("U01", updated);

        var json = Assert.IsType<JsonResult>(result);
        dynamic data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data));

        var saved = await _context.ProductCategories.FindAsync("U01");
        Assert.Equal("新名稱", saved!.CategoryName);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_NotInUse_RemovesRecord()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "DEL01", CategoryName = "可刪除",            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await CreateController(ajax: true).DeleteConfirmed("DEL01");
        var json = Assert.IsType<JsonResult>(result);
        dynamic data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data));
        Assert.Equal(0, _context.ProductCategories.Count());
    }

    [Fact]
    public async Task Delete_InUseByProduct_ReturnsFailure()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "USED", CategoryName = "使用中",            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _context.Products.Add(new Product
        {
            ProductCode = "P01", ProductName = "產品",            CategoryCode = "USED", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var result = await CreateController(ajax: true).DeleteConfirmed("USED");
        var json = Assert.IsType<JsonResult>(result);
        dynamic data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data));
        Assert.Equal(1, _context.ProductCategories.Count()); // 未刪除
    }

    // ── Permission gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task Index_SetsCanCreate_FromPermissionService()
    {
        var perm = NoPermMock();
        var result = await CreateController(perm).Index(null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.False((bool)view.ViewData["CanCreate"]!);
    }
}
