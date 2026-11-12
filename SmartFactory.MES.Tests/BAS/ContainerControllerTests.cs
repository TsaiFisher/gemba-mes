using Moq;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Tests.BAS;

public class ContainerControllerTests : TestBase
{
    private readonly ContainerController _controller;
    private readonly Mock<IPermissionService> _mockPerm;

    public ContainerControllerTests()
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

        _controller = new ContainerController(
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
        _context.Containers.AddRange(
            new Container { ContainerCode = "C001", ContainerName = "Container A", IsActive = true },
            new Container { ContainerCode = "C002", ContainerName = "Container B", IsActive = true }
        );
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, 1, 10, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<PaginatedList<Container>>(view.Model);
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
        var model = Assert.IsType<Container>(view.Model);
        Assert.True(model.IsActive);
    }

    // 5. Create_POST_ValidModel_AjaxReturnsSuccess
    [Fact]
    public async Task Create_POST_ValidModel_AjaxReturnsSuccess()
    {
        SetAjax();
        var model = new Container { ContainerCode = "C001", ContainerName = "Container A", IsActive = true };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Containers.Add(new Container { ContainerCode = "C001", ContainerName = "Existing", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var model = new Container { ContainerCode = "C001", ContainerName = "Duplicate", IsActive = true };

        var result = await _controller.Create(model);

        // Duplicate returns PartialView with model error, not a success JSON
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Containers.Add(new Container { ContainerCode = "C001", ContainerName = "Container A", IsActive = true });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("C001");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Container>(view.Model);
        Assert.Equal("C001", model.ContainerCode);
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
        _context.Containers.Add(new Container { ContainerCode = "C001", ContainerName = "Old Name", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new Container { ContainerCode = "C001", ContainerName = "New Name", IsActive = true };

        var result = await _controller.Edit("C001", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.Containers.Add(new Container { ContainerCode = "C001", ContainerName = "To Delete", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("C001");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Null(await _context.Containers.FindAsync("C001"));
    }

    // 10b. Delete_ContainerUsedInRoutingOperation_ReturnsError
    [Fact]
    public async Task Delete_ContainerUsedInRoutingOperation_ReturnsError()
    {
        _context.Containers.Add(new Container { ContainerCode = "C001", ContainerName = "In Use", IsActive = true });
        _context.RoutingOperations.Add(new SmartFactory.MES.Modules.MFG.Models.RoutingOperation
        {
            RoutingId = "RT001", OperationSeq = 1, WorkCenterId = "WC001", UomCode = "PCS",
            ContainerInCode = "C001",
        });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("C001");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.NotNull(await _context.Containers.FindAsync("C001"));
    }

    // 10c. Delete_ContainerUsedInProductionReportContainer_ReturnsError
    [Fact]
    public async Task Delete_ContainerUsedInProductionReportContainer_ReturnsError()
    {
        _context.Containers.Add(new Container { ContainerCode = "C002", ContainerName = "In Use 2", IsActive = true });
        _context.ProductionReportContainers.Add(new SmartFactory.MES.Modules.MFG.Models.ProductionReportContainer
        {
            ReportId = "PR001", ContainerSeq = 1, ContainerCode = "C002", ActualQty = 10,
        });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("C002");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.NotNull(await _context.Containers.FindAsync("C002"));
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
