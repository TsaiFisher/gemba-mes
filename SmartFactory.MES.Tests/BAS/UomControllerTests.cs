using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class UomControllerTests : TestBase
{
    private UomController CreateController(Mock<IPermissionService>? mockPerm = null, bool ajax = false)
    {
        mockPerm ??= CreateFullPermissionMock();
        var mockApproval = new Mock<IApprovalService>();
        var mockUserCtx = GetMockUserContext();
        var mockExport = GetMockExportService();
        var mockUom    = new Mock<IUomService>();

        var ctrl = new UomController(_context, mockApproval.Object, mockUserCtx.Object, mockExport.Object, mockPerm.Object, mockUom.Object, GetMockLocalizerFactory().Object);
        ctrl.ControllerContext = GetControllerContext();
        if (ajax)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        return ctrl;
    }

    private static Mock<IPermissionService> CreateFullPermissionMock()
    {
        var mock = new Mock<IPermissionService>();
        mock.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        mock.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mock.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mock.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        mock.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        return mock;
    }

    // 1. Index_NotSearched_ReturnsNullModel
    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // 2. Index_Searched_ReturnsPaginatedList
    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.Uoms.Add(new Uom { UomCode = "KG", UomName = "Kilogram", IsActive = true });
        _context.Uoms.Add(new Uom { UomCode = "EA", UomName = "Each",     IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Uom>>(view.Model);
        Assert.Equal(2, model.Count);
    }

    // 3. Index_SetsCanCreateInViewBag
    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.True((bool)view.ViewData["CanCreate"]!);
    }

    // 4. Create_GET_ReturnsView
    [Fact]
    public async Task Create_GET_ReturnsView()
    {
        var ctrl = CreateController();
        var result = await ctrl.Create();
        Assert.IsType<ViewResult>(result);
    }

    // 5. Create_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Create_POST_ValidModel_AjaxReturnsSuccess()
    {
        var ctrl = CreateController(ajax: true);
        var model = new Uom { UomCode = "KG", UomName = "Kilogram", IsActive = true };

        var result = await ctrl.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Uoms.Add(new Uom { UomCode = "KG", UomName = "Kilogram", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var model = new Uom { UomCode = "KG", UomName = "Duplicate", IsActive = true };

        var result = await ctrl.Create(model);

        // Returns PartialView with model error (not JSON success)
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.True(ctrl.ModelState.ContainsKey("UomCode"));
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Uoms.Add(new Uom { UomCode = "EA", UomName = "Each", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Edit("EA");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Uom>(view.Model);
        Assert.Equal("EA", model.UomCode);
    }

    // 8. Edit_GET_NotFound_Returns404
    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var ctrl = CreateController();
        var result = await ctrl.Edit("NOTEXIST");
        Assert.IsType<NotFoundResult>(result);
    }

    // 9. Edit_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Edit_POST_ValidModel_AjaxReturnsSuccess()
    {
        _context.Uoms.Add(new Uom { UomCode = "EA", UomName = "Each", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var updated = new Uom { UomCode = "EA", UomName = "Each Updated", IsActive = false };

        var result = await ctrl.Edit("EA", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.Uoms.Add(new Uom { UomCode = "DEL", UomName = "ToDelete", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("DEL");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.Uoms.AnyAsync(u => u.UomCode == "DEL"));
    }

    // 10b. Delete_UomUsedAsProductStockUom_ReturnsError
    [Fact]
    public async Task Delete_UomUsedAsProductStockUom_ReturnsError()
    {
        _context.Uoms.Add(new Uom { UomCode = "KG", UomName = "In Use", IsActive = true });
        _context.Products.Add(new SmartFactory.MES.Modules.BAS.Models.Product
        {
            ProductCode = "P001", ProductName = "Uses KG", StockUom = "KG",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("KG");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Uoms.AnyAsync(u => u.UomCode == "KG"));
    }

    // 10c. Delete_UomUsedInUomConversion_ReturnsError
    [Fact]
    public async Task Delete_UomUsedInUomConversion_ReturnsError()
    {
        _context.Uoms.Add(new Uom { UomCode = "G", UomName = "In Use 2", IsActive = true });
        _context.UomConversions.Add(new SmartFactory.MES.Modules.BAS.Models.UomConversion
        {
            FromUomCode = "G", ToUomCode = "KG", Numerator = 1, Denominator = 1000,
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("G");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Uoms.AnyAsync(u => u.UomCode == "G"));
    }

    // 11. Export_NoPrintPermission_ReturnsForbid
    [Fact]
    public async Task Export_NoPrintPermission_ReturnsForbid()
    {
        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        mockPerm.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(false);
        mockPerm.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(false);
        mockPerm.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(false);

        var ctrl = CreateController(mockPerm);
        var result = await ctrl.Export("excel", null);
        Assert.IsType<ForbidResult>(result);
    }

    // 12. GetConversions_ReturnsJsonArray
    [Fact]
    public async Task GetConversions_ReturnsJsonArray()
    {
        _context.Uoms.Add(new Uom { UomCode = "KG", UomName = "Kilogram", IsActive = true });
        _context.Uoms.Add(new Uom { UomCode = "G",  UomName = "Gram",     IsActive = true });
        _context.UomConversions.Add(new UomConversion { FromUomCode = "KG", ToUomCode = "G", Numerator = 1000, Denominator = 1 });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.GetConversions("KG");

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(json.Value);
        Assert.Single(list);
    }

    // 13. SaveConversion_AddsNewRecord
    [Fact]
    public async Task SaveConversion_AddsNewRecord()
    {
        var ctrl = CreateController(ajax: true);
        var result = await ctrl.SaveConversion("KG", "G", 1000, 1);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.UomConversions.AnyAsync(c => c.FromUomCode == "KG" && c.ToUomCode == "G"));
    }

    // 14. DeleteConversion_RemovesRecord
    [Fact]
    public async Task DeleteConversion_RemovesRecord()
    {
        _context.UomConversions.Add(new UomConversion { FromUomCode = "KG", ToUomCode = "G", Numerator = 1000, Denominator = 1 });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConversion("KG", "G");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.UomConversions.AnyAsync(c => c.FromUomCode == "KG" && c.ToUomCode == "G"));
    }
}
