using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 部門實際費用維護（DepartmentActualCostController）守門：
/// 廠別過濾、唯一鍵（期間+工作中心）防重、金額非負、CRUD JSON 契約。
/// </summary>
public class DepartmentActualCostControllerTests : TestBase
{
    private const string PlantId = "P001";

    private DepartmentActualCostController CreateController(bool canRead = true, bool canDelete = true)
    {
        _context.CurrentPlantId = PlantId;

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));

        var mockUser = new Mock<IUserContext>();
        mockUser.Setup(u => u.PlantId).Returns(PlantId);
        mockUser.Setup(u => u.UserId).Returns("User1");
        mockUser.Setup(u => u.ActiveRoleId).Returns("Admin");

        var mockPerm = new Mock<IPermissionService>();
        mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(canRead);
        mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(canDelete);
        mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        return new DepartmentActualCostController(
            mockLoc.Object,
            GetMockCurrencyService().Object,
            new Mock<IExportService>().Object,
            new Mock<IDateTimeService>().Object)
        {
            ControllerContext = GetControllerContextWithServices(
                permService: mockPerm.Object, userContext: mockUser.Object)
        };
    }

    private async Task Seed(string periodId, string wc, decimal labor = 100m, decimal overhead = 50m)
    {
        _context.Set<DepartmentActualCost>().Add(new DepartmentActualCost
        {
            PeriodId = periodId, WorkCenterCode = wc,
            ActualLaborCost = labor, ActualOverheadCost = overhead, PlantId = PlantId,
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(JsonResult json)
    {
        dynamic data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await CreateController().Index(null, null, searched: false) as ViewResult;
        Assert.NotNull(result);
        Assert.Null(result!.Model);
    }

    [Fact]
    public async Task Index_Searched_ReturnsOnlyCurrentPlant()
    {
        await Seed("202606", "WC1");
        var result = await CreateController().Index(null, null, searched: true) as ViewResult;
        var list = result?.Model as PaginatedList<DepartmentActualCost>;
        Assert.NotNull(list);
        Assert.Single(list!);
    }

    [Fact]
    public async Task Index_NoReadPermission_ReturnsForbid()
    {
        var result = await CreateController(canRead: false).Index(null, null, searched: true);
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Create_Valid_SavesToDatabase()
    {
        var ctrl = CreateController();
        var model = new DepartmentActualCost { PeriodId = "202606", WorkCenterCode = "WC1", ActualLaborCost = 1200m, ActualOverheadCost = 600m };

        var result = await ctrl.Create(model) as JsonResult;
        Assert.NotNull(result);
        Assert.True(IsSuccess(result!));

        var saved = _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Single();
        Assert.Equal(PlantId, saved.PlantId);
        Assert.Equal(1200m, saved.ActualLaborCost);
    }

    [Fact]
    public async Task Create_Duplicate_RejectedWithValidationError()
    {
        await Seed("202606", "WC1");
        var ctrl = CreateController();
        var model = new DepartmentActualCost { PeriodId = "202606", WorkCenterCode = "WC1", ActualLaborCost = 999m, ActualOverheadCost = 1m };

        var result = await ctrl.Create(model);

        Assert.IsNotType<JsonResult>(result);              // 非成功 JSON，而是退回表單
        Assert.False(ctrl.ModelState.IsValid);
        Assert.Equal(1, _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Count());
    }

    [Fact]
    public async Task Create_NegativeAmount_RejectedWithValidationError()
    {
        var ctrl = CreateController();
        var model = new DepartmentActualCost { PeriodId = "202606", WorkCenterCode = "WC1", ActualLaborCost = -1m, ActualOverheadCost = 0m };

        var result = await ctrl.Create(model);

        Assert.IsNotType<JsonResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.Empty(_context.Set<DepartmentActualCost>().IgnoreQueryFilters());
    }

    [Fact]
    public async Task Edit_Valid_UpdatesValues()
    {
        await Seed("202606", "WC1", labor: 100m, overhead: 50m);
        var id = _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Single().Id;
        var ctrl = CreateController();
        var model = new DepartmentActualCost { Id = id, PeriodId = "202606", WorkCenterCode = "WC1", ActualLaborCost = 800m, ActualOverheadCost = 400m };

        var result = await ctrl.Edit(id, model) as JsonResult;
        Assert.NotNull(result);
        Assert.True(IsSuccess(result!));

        var updated = _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Single();
        Assert.Equal(800m, updated.ActualLaborCost);
        Assert.Equal(400m, updated.ActualOverheadCost);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesRecord()
    {
        await Seed("202606", "WC1");
        var id = _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Single().Id;
        var ctrl = CreateController();

        var result = await ctrl.DeleteConfirmed(id) as JsonResult;
        Assert.NotNull(result);
        Assert.True(IsSuccess(result!));
        Assert.Empty(_context.Set<DepartmentActualCost>().IgnoreQueryFilters());
    }

    [Fact]
    public async Task DeleteConfirmed_NoPermission_ReturnsFailJson()
    {
        await Seed("202606", "WC1");
        var id = _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Single().Id;
        var ctrl = CreateController(canDelete: false);

        var result = await ctrl.DeleteConfirmed(id) as JsonResult;
        Assert.NotNull(result);
        Assert.False(IsSuccess(result!));
        Assert.Equal(1, _context.Set<DepartmentActualCost>().IgnoreQueryFilters().Count());
    }
}
