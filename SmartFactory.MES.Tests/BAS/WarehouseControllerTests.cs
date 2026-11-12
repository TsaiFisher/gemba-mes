using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class WarehouseControllerTests : TestBase
{
    private WarehouseController CreateController(Mock<IPermissionService>? mockPerm = null, bool ajax = false)
    {
        mockPerm ??= CreateFullPermissionMock();
        var mockApproval = new Mock<IApprovalService>();
        var mockUserCtx = GetMockUserContext();
        var mockExport = GetMockExportService();

        var ctrl = new WarehouseController(_context, mockApproval.Object, mockUserCtx.Object, mockExport.Object, mockPerm.Object, GetMockLocalizerFactory().Object);
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
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "Main Warehouse", IsActive = true });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "Secondary",      IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Warehouse>>(view.Model);
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
        var model = new Warehouse { WhCode = "WH01", WhName = "Main Warehouse", IsActive = true };

        var result = await ctrl.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "Main Warehouse", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var model = new Warehouse { WhCode = "WH01", WhName = "Duplicate", IsActive = true };

        var result = await ctrl.Create(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.True(ctrl.ModelState.ContainsKey("WhCode"));
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "Main Warehouse", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Edit("WH01");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Warehouse>(view.Model);
        Assert.Equal("WH01", model.WhCode);
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
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "Main Warehouse", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var updated = new Warehouse { WhCode = "WH01", WhName = "Updated Name", IsActive = false };

        var result = await ctrl.Edit("WH01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "DEL", WhName = "ToDelete", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("DEL");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.Warehouses.AnyAsync(w => w.WhCode == "DEL"));
    }

    // 10b. Delete_WarehouseUsedInStorageLocation_ReturnsError
    [Fact]
    public async Task Delete_WarehouseUsedInStorageLocation_ReturnsError()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "In Use", IsActive = true });
        _context.StorageLocations.Add(new SmartFactory.MES.Modules.BAS.Models.StorageLocation
        {
            WhCode = "WH01", LocationCode = "LOC01", LocationName = "Loc 1",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("WH01");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Warehouses.AnyAsync(w => w.WhCode == "WH01"));
    }

    // 10c. Delete_WarehouseUsedInStockTransfer_ReturnsError
    [Fact]
    public async Task Delete_WarehouseUsedInStockTransfer_ReturnsError()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "In Use 2", IsActive = true });
        _context.StockTransfers.Add(new SmartFactory.MES.Modules.INV.Models.StockTransfer
        {
            StockTransferId = "ST001", FromWhCode = "WH02",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("WH02");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Warehouses.AnyAsync(w => w.WhCode == "WH02"));
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

    // 12. GetLocations_ReturnsJsonArray
    [Fact]
    public async Task GetLocations_ReturnsJsonArray()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "Main", IsActive = true });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A01", LocationName = "Rack A01", IsActive = true });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A02", LocationName = "Rack A02", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.GetLocations("WH01");

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(json.Value);
        Assert.Equal(2, list.Count());
    }

    // 13. SaveLocation_AddsNewRecord
    [Fact]
    public async Task SaveLocation_AddsNewRecord()
    {
        var ctrl = CreateController(ajax: true);
        var result = await ctrl.SaveLocation("WH01", "A01", "Rack A01", null, null, true);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.StorageLocations.AnyAsync(l => l.WhCode == "WH01" && l.LocationCode == "A01"));
    }

    // 14. DeleteLocation_RemovesRecord
    [Fact]
    public async Task DeleteLocation_RemovesRecord()
    {
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A01", LocationName = "Rack A01", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteLocation("WH01", "A01");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.StorageLocations.AnyAsync(l => l.WhCode == "WH01" && l.LocationCode == "A01"));
    }
}
