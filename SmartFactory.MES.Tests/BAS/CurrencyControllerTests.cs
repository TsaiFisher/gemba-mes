using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class CurrencyControllerTests : TestBase
{
    private CurrencyController CreateController(Mock<IPermissionService>? mockPerm = null, bool ajax = false)
    {
        mockPerm ??= CreateFullPermissionMock();
        var mockApproval = new Mock<IApprovalService>();
        var mockUserCtx = GetMockUserContext();
        var mockExport = GetMockExportService();

        var mockCurrency = GetMockCurrencyService();
        var ctrl = new CurrencyController(_context, mockApproval.Object, mockUserCtx.Object, mockExport.Object, mockPerm.Object, mockCurrency.Object, GetMockLocalizerFactory().Object);
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
        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar",    IsActive = true });
        _context.Currencies.Add(new Currency { CurrencyCode = "EUR", CurrencyName = "Euro",         IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<Currency>>(view.Model);
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
        var model = new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true };

        var result = await ctrl.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var model = new Currency { CurrencyCode = "USD", CurrencyName = "Duplicate", IsActive = true };

        var result = await ctrl.Create(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.True(ctrl.ModelState.ContainsKey("CurrencyCode"));
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Edit("USD");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<Currency>(view.Model);
        Assert.Equal("USD", model.CurrencyCode);
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
        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var updated = new Currency { CurrencyCode = "USD", CurrencyName = "United States Dollar", IsActive = false };

        var result = await ctrl.Edit("USD", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.Currencies.Add(new Currency { CurrencyCode = "DEL", CurrencyName = "ToDelete", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("DEL");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.Currencies.AnyAsync(c => c.CurrencyCode == "DEL"));
    }

    // 10b. Delete_BaseCurrency_ReturnsError
    [Fact]
    public async Task Delete_BaseCurrency_ReturnsError()
    {
        // GetMockCurrencyService() defaults baseCurrency 為 "TWD"（見 TestBase）
        _context.Currencies.Add(new Currency { CurrencyCode = "TWD", CurrencyName = "New Taiwan Dollar", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("TWD");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Currencies.AnyAsync(c => c.CurrencyCode == "TWD"));
    }

    // 10c. Delete_CurrencyUsedInPurchaseOrderLine_ReturnsError
    [Fact]
    public async Task Delete_CurrencyUsedInPurchaseOrderLine_ReturnsError()
    {
        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO001", LineNo = 1, ProductCode = "P001", CurrencyCode = "USD",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("USD");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.Currencies.AnyAsync(c => c.CurrencyCode == "USD"));
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

    // 12. GetRates_ReturnsJsonArray
    [Fact]
    public async Task GetRates_ReturnsJsonArray()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var yesterday = today.AddDays(-1);

        _context.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "US Dollar", IsActive = true });
        _context.CurrencyRates.Add(new CurrencyRate { CurrencyCode = "USD", RateDate = today,     BuyRate = 30.0m, SellRate = 30.5m });
        _context.CurrencyRates.Add(new CurrencyRate { CurrencyCode = "USD", RateDate = yesterday, BuyRate = 29.9m, SellRate = 30.4m });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.GetRates("USD");

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(json.Value);
        Assert.Equal(2, list.Count());
    }

    // 13. SaveRate_AddsNewRecord
    [Fact]
    public async Task SaveRate_AddsNewRecord()
    {
        var ctrl = CreateController(ajax: true);
        var rateDateStr = DateTime.Today.ToString("yyyy-MM-dd");
        var result = await ctrl.SaveRate("USD", rateDateStr, 30.0m, 30.5m);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.True(await _context.CurrencyRates.AnyAsync(r => r.CurrencyCode == "USD" && r.RateDate == today));
    }

    // 14. DeleteRate_RemovesRecord
    [Fact]
    public async Task DeleteRate_RemovesRecord()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _context.CurrencyRates.Add(new CurrencyRate { CurrencyCode = "USD", RateDate = today, BuyRate = 30.0m, SellRate = 30.5m });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var rateDateStr = today.ToString("yyyy-MM-dd");
        var result = await ctrl.DeleteRate("USD", rateDateStr);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.CurrencyRates.AnyAsync(r => r.CurrencyCode == "USD" && r.RateDate == today));
    }
}
