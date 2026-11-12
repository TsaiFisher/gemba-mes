using Moq;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Tests.BAS;

public class ShiftControllerTests : TestBase
{
    private readonly ShiftController _controller;
    private readonly Mock<IPermissionService> _mockPerm;

    public ShiftControllerTests()
    {
        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(x => x.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        var mockApproval = new Mock<IApprovalService>();

        var mockUserCtx = new Mock<IUserContext>();
        mockUserCtx.Setup(x => x.PlantId).Returns("P001");
        mockUserCtx.Setup(x => x.UserId).Returns("User1");

        _controller = new ShiftController(
            _context,
            mockApproval.Object,
            mockUserCtx.Object,
            GetMockExportService().Object,
            _mockPerm.Object,
            GetMockLocalizerFactory().Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    // 1. Index_NotSearched_ReturnsNullModel
    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // 2. Index_Searched_ReturnsPaginatedList
    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.Shifts.AddRange(
            new Shift { ShiftCode = "S001", ShiftName = "Morning", IsActive = true },
            new Shift { ShiftCode = "S002", ShiftName = "Evening", IsActive = true }
        );
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, 1, 10, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<PaginatedList<Shift>>(view.Model);
        Assert.Equal(2, model.Count);
    }

    // 3. Index_SetsCanCreateInViewBag
    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True((bool)view.ViewData["CanCreate"]!);
    }

    // 4. Create_GET_ReturnsView
    [Fact]
    public async Task Create_GET_ReturnsView()
    {
        var result = await _controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Shift>(view.Model);
        Assert.True(model.IsActive);
    }

    // 5. Create_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Create_POST_ValidModel_AjaxReturnsSuccess()
    {
        SetAjax();
        var model = new Shift { ShiftCode = "S001", ShiftName = "Morning", IsActive = true };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Shifts.Add(new Shift { ShiftCode = "S001", ShiftName = "Existing", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var model = new Shift { ShiftCode = "S001", ShiftName = "Duplicate", IsActive = true };

        var result = await _controller.Create(model);

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Shifts.Add(new Shift { ShiftCode = "S001", ShiftName = "Morning", IsActive = true });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("S001");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Shift>(view.Model);
        Assert.Equal("S001", model.ShiftCode);
    }

    // 8. Edit_GET_NotFound_Returns404
    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("NONEXISTENT");

        Assert.IsType<NotFoundResult>(result);
    }

    // 9. Edit_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Edit_POST_ValidModel_AjaxReturnsSuccess()
    {
        _context.Shifts.Add(new Shift { ShiftCode = "S001", ShiftName = "Old Name", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new Shift { ShiftCode = "S001", ShiftName = "New Name", IsActive = true };

        var result = await _controller.Edit("S001", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.Shifts.Add(new Shift { ShiftCode = "S001", ShiftName = "To Delete", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("S001");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Null(await _context.Shifts.FindAsync("S001"));
    }

    // 11. Export_NoPrintPermission_ReturnsForbid
    [Fact]
    public async Task Export_NoPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null);

        Assert.IsType<ForbidResult>(result);
    }
}
