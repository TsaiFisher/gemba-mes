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

public class CompanyControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IUserContext> _mockUserCtx;
    private readonly Mock<IApprovalService> _mockApproval;
    private readonly CompanyController _controller;

    public CompanyControllerTests()
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

        _controller = new CompanyController(
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
        var result = await _controller.Index(null, null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Test 2: Index_Searched_ReturnsPaginatedList ───────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.Companies.AddRange(
            new Company { CompanyCode = "C01", CompanyName = "Acme Corp" },
            new Company { CompanyCode = "C02", CompanyName = "Beta Ltd" });
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Company>>(view.Model);
        Assert.True(model.Count >= 1);
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
        var model = new Company { CompanyCode = "C01", CompanyName = "Acme Corp", IsActive = true };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Single(_context.Companies);
        Assert.Equal(DocumentApprovalStatus.Approved, _context.Companies.First().ApprovalStatus);
    }

    // ── Test 6: Create_POST_DuplicateCode_ReturnsError ────────────────────────

    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Existing" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var model = new Company { CompanyCode = "C01", CompanyName = "Duplicate" };

        var result = await _controller.Create(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Test 7: Edit_GET_ExistingRecord_ReturnsView ───────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Acme Corp" });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("C01");

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
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Old Name" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var updated = new Company { CompanyCode = "C01", CompanyName = "New Name", IsActive = true };

        var result = await _controller.Edit("C01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        var saved = await _context.Companies.FindAsync("C01");
        Assert.Equal("New Name", saved!.CompanyName);
    }

    // ── Test 10: Delete_RemovesRecord_ReturnsSuccessJson ──────────────────────

    [Fact]
    public async Task Delete_RemovesRecord_ReturnsSuccessJson()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "To Delete" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);

        var result = await _controller.DeleteConfirmed("C01");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Empty(_context.Companies);
    }

    // ── Test 10b: Delete_CompanyUsedAsSupplierInPurchaseOrder_ReturnsError ────

    [Fact]
    public async Task Delete_CompanyUsedAsSupplierInPurchaseOrder_ReturnsError()
    {
        _context.Companies.Add(new Company { CompanyCode = "SUP1", CompanyName = "In Use Supplier" });
        _context.PurchaseOrders.Add(new SmartFactory.MES.Modules.PUR.Models.PurchaseOrder
        {
            PurchaseOrderId = "PO001", CreatedBy = "User1", SupplierCode = "SUP1",
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("SUP1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Companies.Any(c => c.CompanyCode == "SUP1"));
    }

    // ── Test 10c: Delete_CompanyUsedAsCustomerInSalesOrder_ReturnsError ───────

    [Fact]
    public async Task Delete_CompanyUsedAsCustomerInSalesOrder_ReturnsError()
    {
        _context.Companies.Add(new Company { CompanyCode = "CUST1", CompanyName = "In Use Customer" });
        _context.SalesOrders.Add(new SmartFactory.MES.Modules.ORD.Models.SalesOrder
        {
            SalesOrderId = "SO001", CompanyCode = "CUST1",
        });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var result = await _controller.DeleteConfirmed("CUST1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(_context.Companies.Any(c => c.CompanyCode == "CUST1"));
    }

    // ── Test 11: Export_WithoutPrintPermission_ReturnsForbid ─────────────────

    [Fact]
    public async Task Export_WithoutPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null, null);

        Assert.IsType<ForbidResult>(result);
    }

    // ── Extra: GetContacts_ReturnsJsonList ────────────────────────────────────

    [Fact]
    public async Task GetContacts_ReturnsJsonList()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Acme" });
        _context.CompanyContacts.AddRange(
            new CompanyContact { CompanyCode = "C01", SeqNo = 1, ContactName = "Alice" },
            new CompanyContact { CompanyCode = "C01", SeqNo = 2, ContactName = "Bob" });
        await _context.SaveChangesAsync();

        var result = await _controller.GetContacts("C01");

        Assert.IsType<JsonResult>(result);
    }

    // ── Extra: SaveContact_NewContact_ReturnsSuccess ──────────────────────────

    [Fact]
    public async Task SaveContact_NewContact_ReturnsSuccess()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Acme" });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var contact = new CompanyContact { Id = 0, CompanyCode = "C01", ContactName = "Charlie" };

        var result = await _controller.SaveContact(contact);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Single(_context.CompanyContacts);
    }

    // ── Extra: DeleteContact_RemovesContact_ReturnsSuccess ───────────────────

    [Fact]
    public async Task DeleteContact_RemovesContact_ReturnsSuccess()
    {
        _context.Companies.Add(new Company { CompanyCode = "C01", CompanyName = "Acme" });
        _context.CompanyContacts.Add(new CompanyContact { Id = 1, CompanyCode = "C01", ContactName = "Dave" });
        await _context.SaveChangesAsync();

        var result = await _controller.DeleteContact(1);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Empty(_context.CompanyContacts);
    }
}
