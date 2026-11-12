using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

public class TaxControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IUserContext> _mockUserCtx;
    private readonly Mock<IApprovalService> _mockApproval;
    private readonly TaxController _controller;

    public TaxControllerTests()
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

        _controller = new TaxController(
            _context,
            _mockApproval.Object,
            _mockUserCtx.Object,
            GetMockExportService().Object,
            _mockPerm.Object,
            GetMockLocalizerFactory().Object);

        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax(Controller ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    // ── Test 1: Index_NotSearched_ReturnsNullModel ────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Test 2: Index_Searched_ReturnsPaginatedList ───────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.Taxes.AddRange(
            new Tax { TaxCode = "T01", TaxName = "VAT 5%",  TaxRate = 0.05m },
            new Tax { TaxCode = "T02", TaxName = "VAT 10%", TaxRate = 0.10m });
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Tax>>(view.Model);
        Assert.True(model.Count >= 1);
    }

    // ── Test 3: Index_SetsCanCreateInViewBag ─────────────────────────────────

    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

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
        var model = new Tax { TaxCode = "TX01", TaxName = "Test Tax", TaxRate = 0.05m, IsActive = true };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Single(_context.Taxes);
        Assert.Equal(DocumentApprovalStatus.Approved, _context.Taxes.First().ApprovalStatus);
    }

    // ── Test 6: Create_POST_DuplicateCode_ReturnsError ────────────────────────

    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Taxes.Add(new Tax { TaxCode = "TX01", TaxName = "Existing", TaxRate = 0.05m });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var model = new Tax { TaxCode = "TX01", TaxName = "Duplicate", TaxRate = 0.05m };

        var result = await _controller.Create(model);

        // Duplicate returns PartialView with model error
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Test 7: Edit_GET_ExistingRecord_ReturnsView ───────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Taxes.Add(new Tax { TaxCode = "TX01", TaxName = "Tax One", TaxRate = 0.05m });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("TX01");

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
        _context.Taxes.Add(new Tax { TaxCode = "TX01", TaxName = "Old Name", TaxRate = 0.05m });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var updated = new Tax { TaxCode = "TX01", TaxName = "New Name", TaxRate = 0.10m, IsActive = true };

        var result = await _controller.Edit("TX01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        var saved = await _context.Taxes.FindAsync("TX01");
        Assert.Equal("New Name", saved!.TaxName);
        Assert.Equal(0.10m, saved.TaxRate);
    }

    // ── Test 10: Delete_RemovesRecord_ReturnsSuccessJson ──────────────────────

    [Fact]
    public async Task Delete_RemovesRecord_ReturnsSuccessJson()
    {
        _context.Taxes.Add(new Tax { TaxCode = "TX01", TaxName = "To Delete", TaxRate = 0.05m });
        await _context.SaveChangesAsync();

        SetAjax(_controller);

        var result = await _controller.DeleteConfirmed("TX01");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Empty(_context.Taxes);
    }

    // ── Test 10b: Delete_TaxUsedAsCompanyDefault_ReturnsError ──────────────────

    [Fact]
    public async Task Delete_TaxUsedAsCompanyDefault_ReturnsError()
    {
        _context.Taxes.Add(new Tax { TaxCode = "TX02", TaxName = "In Use", TaxRate = 0.05m });
        _context.Companies.Add(new SmartFactory.MES.Modules.BAS.Models.Company
        {
            CompanyCode = "C01", CompanyName = "Uses TX02", TaxCode = "TX02",
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("TX02");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Taxes.Any(t => t.TaxCode == "TX02"));
    }

    // ── Test 10c: Delete_TaxUsedInPurchaseOrderLine_ReturnsError ───────────────

    [Fact]
    public async Task Delete_TaxUsedInPurchaseOrderLine_ReturnsError()
    {
        _context.Taxes.Add(new Tax { TaxCode = "TX03", TaxName = "In Use 2", TaxRate = 0.05m });
        _context.PurchaseOrderLines.Add(new SmartFactory.MES.Modules.PUR.Models.PurchaseOrderLine
        {
            PurchaseOrderId = "PO001", LineNo = 1, ProductCode = "P001", TaxCode = "TX03",
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("TX03");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Taxes.Any(t => t.TaxCode == "TX03"));
    }

    // ── Test 11: Export_WithoutPrintPermission_ReturnsForbid ─────────────────

    [Fact]
    public async Task Export_WithoutPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null);

        Assert.IsType<ForbidResult>(result);
    }
}
