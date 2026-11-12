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

public class SpecialDayControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IUserContext> _mockUserCtx;
    private readonly SpecialDayController _controller;

    public SpecialDayControllerTests()
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

        _controller = new SpecialDayController(
            _context,
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
        var result = await _controller.Index(null, null, null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Test 2: Index_Searched_ReturnsPaginatedList ───────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        var year = 2025;
        _context.SpecialDays.AddRange(
            new SpecialDay { Date = new DateOnly(2025, 1, 1), DayType = "Holiday", Year = year },
            new SpecialDay { Date = new DateOnly(2025, 2, 28), DayType = "Holiday", Year = year });
        await _context.SaveChangesAsync();

        var result = await _controller.Index(year, null, null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<SpecialDay>>(view.Model);
        Assert.True(model.Count >= 1);
    }

    // ── Test 3: Index_SetsCanCreateInViewBag ─────────────────────────────────

    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, null, null, searched: false);

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
        var model = new SpecialDay
        {
            Date    = new DateOnly(2025, 12, 25),
            DayType = "Holiday",
            Year    = 2025
        };

        var result = await _controller.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Single(_context.SpecialDays);
    }

    // ── Test 6: Create_POST_DuplicateDate_ReturnsError ───────────────────────

    [Fact]
    public async Task Create_POST_DuplicateDate_ReturnsError()
    {
        var existingDate = new DateOnly(2025, 12, 25);
        _context.SpecialDays.Add(new SpecialDay { Date = existingDate, DayType = "Holiday", Year = 2025 });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var model = new SpecialDay { Date = existingDate, DayType = "Overtime", Year = 2025 };

        var result = await _controller.Create(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Test 7: Edit_GET_ExistingRecord_ReturnsView ───────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.SpecialDays.Add(new SpecialDay { Date = new DateOnly(2025, 1, 1), DayType = "Holiday", Year = 2025 });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("2025-01-01");

        Assert.IsType<ViewResult>(result);
    }

    // ── Test 8: Edit_GET_NotFound_Returns404 ─────────────────────────────────

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("2099-12-31");

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Test 9: Edit_POST_ValidModel_UpdatesRecord ────────────────────────────

    [Fact]
    public async Task Edit_POST_ValidModel_UpdatesRecord()
    {
        _context.SpecialDays.Add(new SpecialDay { Date = new DateOnly(2025, 1, 1), DayType = "Holiday", Year = 2025 });
        await _context.SaveChangesAsync();

        SetAjax(_controller);
        var updated = new SpecialDay
        {
            Date        = new DateOnly(2025, 1, 1),
            DayType     = "Overtime",
            Description = "Make-up day",
            Year        = 2025
        };

        var result = await _controller.Edit("2025-01-01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        var saved = _context.SpecialDays.First();
        Assert.Equal("Overtime", saved.DayType);
        Assert.Equal("Make-up day", saved.Description);
    }

    // ── Test 10: Delete_RemovesRecord_ReturnsSuccessJson ──────────────────────

    [Fact]
    public async Task Delete_RemovesRecord_ReturnsSuccessJson()
    {
        _context.SpecialDays.Add(new SpecialDay { Date = new DateOnly(2025, 6, 15), DayType = "Holiday", Year = 2025 });
        await _context.SaveChangesAsync();

        SetAjax(_controller);

        var result = await _controller.DeleteConfirmed("2025-06-15");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Empty(_context.SpecialDays);
    }

    // ── Test 11: Export_WithoutPrintPermission_ReturnsForbid ─────────────────

    [Fact]
    public async Task Export_WithoutPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null, null);

        Assert.IsType<ForbidResult>(result);
    }

    // ── Extra: CalendarDataYear_ReturnsJsonForFullYear ────────────────────────

    [Fact]
    public async Task CalendarDataYear_ReturnsJsonForFullYear()
    {
        _context.SpecialDays.AddRange(
            new SpecialDay { Date = new DateOnly(2025,  1,  1), DayType = "Holiday", Year = 2025 },
            new SpecialDay { Date = new DateOnly(2025,  6, 15), DayType = "Overtime", Year = 2025 },
            new SpecialDay { Date = new DateOnly(2025, 12, 25), DayType = "Holiday", Year = 2025 },
            new SpecialDay { Date = new DateOnly(2024, 12, 31), DayType = "Holiday", Year = 2024 }); // different year
        await _context.SaveChangesAsync();

        var result = await _controller.CalendarDataYear(2025);

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(json.Value);
        Assert.Equal(3, list.Count()); // only 2025 records
    }

    // ── Extra: CalendarData_ReturnsJsonForGivenMonthRange ─────────────────────

    [Fact]
    public async Task CalendarData_ReturnsJsonForGivenMonthRange()
    {
        _context.SpecialDays.AddRange(
            new SpecialDay { Date = new DateOnly(2025, 3, 1),  DayType = "Holiday", Year = 2025 },
            new SpecialDay { Date = new DateOnly(2025, 3, 15), DayType = "Overtime", Year = 2025 },
            new SpecialDay { Date = new DateOnly(2025, 4, 1),  DayType = "Holiday", Year = 2025 });
        await _context.SaveChangesAsync();

        var result = await _controller.CalendarData(2025, 3);

        Assert.IsType<JsonResult>(result);
    }
}
