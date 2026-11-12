using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.BAS;

public class ShiftGroupControllerTests : TestBase
{
    private ShiftGroupController CreateController(Mock<IPermissionService>? mockPerm = null, bool ajax = false)
    {
        mockPerm ??= CreateFullPermissionMock();
        var mockApproval = new Mock<IApprovalService>();
        var mockUserCtx = GetMockUserContext();
        var mockExport = GetMockExportService();

        var ctrl = new ShiftGroupController(_context, mockApproval.Object, mockUserCtx.Object, mockExport.Object, mockPerm.Object, GetMockLocalizerFactory().Object);
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
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift",   IsActive = true });
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG02", GroupName = "Night Shift", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Index(null, null, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<ShiftGroup>>(view.Model);
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
        var model = new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true };

        var result = await ctrl.Create(model);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 5b. Create_POST_ValidModel_InitializesSevenDays
    [Fact]
    public async Task Create_POST_ValidModel_InitializesSevenDays()
    {
        var ctrl = CreateController(ajax: true);
        var model = new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true };

        await ctrl.Create(model);

        var dayCount = await _context.ShiftGroupDays.CountAsync(d => d.GroupCode == "SG01");
        Assert.Equal(7, dayCount);
    }

    // 6. Create_POST_DuplicateCode_ReturnsError
    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var model = new ShiftGroup { GroupCode = "SG01", GroupName = "Duplicate", IsActive = true };

        var result = await ctrl.Create(model);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.True(ctrl.ModelState.ContainsKey("GroupCode"));
    }

    // 7. Edit_GET_ExistingRecord_ReturnsView
    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.Edit("SG01");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ShiftGroup>(view.Model);
        Assert.Equal("SG01", model.GroupCode);
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
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var updated = new ShiftGroup { GroupCode = "SG01", GroupName = "Updated Shift", IsActive = false };

        var result = await ctrl.Edit("SG01", updated);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // 10. Delete_AjaxReturnsSuccess
    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "DEL", GroupName = "ToDelete", IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("DEL");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.False(await _context.ShiftGroups.AnyAsync(g => g.GroupCode == "DEL"));
    }

    // 10b. Delete_ShiftGroupUsedInWorkCenter_ReturnsError
    [Fact]
    public async Task Delete_ShiftGroupUsedInWorkCenter_ReturnsError()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG1", GroupName = "In Use", IsActive = true });
        _context.WorkCenters.Add(new SmartFactory.MES.Modules.BAS.Models.WorkCenter
        {
            WorkCenterCode = "WC001", WorkCenterName = "Uses SG1", ShiftGroupCode = "SG1",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("SG1");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.ShiftGroups.AnyAsync(g => g.GroupCode == "SG1"));
    }

    // 10c. Delete_ShiftGroupUsedInWorkCalendar_ReturnsError
    [Fact]
    public async Task Delete_ShiftGroupUsedInWorkCalendar_ReturnsError()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG2", GroupName = "In Use 2", IsActive = true });
        _context.WorkCalendars.Add(new SmartFactory.MES.Modules.MFG.Models.WorkCalendar
        {
            Date = DateOnly.FromDateTime(DateTime.Today), ShiftGroupCode = "SG2",
        });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.DeleteConfirmed("SG2");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.True(await _context.ShiftGroups.AnyAsync(g => g.GroupCode == "SG2"));
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

    // 12. GetDays_ReturnsJsonArray
    [Fact]
    public async Task GetDays_ReturnsJsonArray()
    {
        _context.ShiftGroups.Add(new ShiftGroup { GroupCode = "SG01", GroupName = "Day Shift", IsActive = true });
        for (int day = 1; day <= 7; day++)
        {
            _context.ShiftGroupDays.Add(new ShiftGroupDay { GroupCode = "SG01", DayOfWeek = day });
        }
        await _context.SaveChangesAsync();

        var ctrl = CreateController();
        var result = await ctrl.GetDays("SG01");

        var json = Assert.IsType<JsonResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(json.Value);
        Assert.Equal(7, list.Count());
    }

    // 13. SaveDay_AddsNewRecord — 正班工時/開班/收班由所選班別伺服端推算
    [Fact]
    public async Task SaveDay_AddsNewRecord_DerivesHoursFromShifts()
    {
        _context.Shifts.Add(new Shift { ShiftCode = "S1", ShiftName = "AM",
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(12, 0), WorkMinutes = 240, IsActive = true });
        _context.Shifts.Add(new Shift { ShiftCode = "S2", ShiftName = "PM",
            StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(17, 0), WorkMinutes = 240, IsActive = true });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.SaveDay("SG01", 1, "S1", "S2", null, null, dailyOvertimeHours: 2m);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

        var rec = await _context.ShiftGroupDays.FindAsync("SG01", 1);
        Assert.NotNull(rec);
        Assert.Equal(8m, rec!.DailyRegularHours);          // (240+240)/60
        Assert.Equal(2m, rec.DailyOvertimeHours);          // 手動填入
        Assert.Equal(new TimeOnly(8, 0), rec.WorkStart);   // 最早班別開始
        Assert.Equal(new TimeOnly(17, 0), rec.WorkEnd);    // 最晚班別結束
    }

    // 14. DeleteDetail (SaveDay updating existing record — verifies upsert path)
    [Fact]
    public async Task SaveDay_UpdatesExistingRecord()
    {
        _context.ShiftGroupDays.Add(new ShiftGroupDay { GroupCode = "SG01", DayOfWeek = 2, Shift1Code = "OLD" });
        await _context.SaveChangesAsync();

        var ctrl = CreateController(ajax: true);
        var result = await ctrl.SaveDay("SG01", 2, "NEW", null, null, null);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);

        var rec = await _context.ShiftGroupDays.FindAsync("SG01", 2);
        Assert.Equal("NEW", rec!.Shift1Code);
    }
}
