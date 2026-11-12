using Moq;
using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.BAS.Controllers;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Tests.BAS;

public class WorkCenterControllerTests : TestBase
{
    private readonly WorkCenterController _controller;
    private readonly Mock<IPermissionService> _mockPerm;

    public WorkCenterControllerTests()
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

        _context.CurrentPlantId = "P001";

        var mockCurrency = GetMockCurrencyService();
        _controller = new WorkCenterController(
            _context,
            mockApproval.Object,
            mockUserCtx.Object,
            GetMockExportService().Object,
            _mockPerm.Object,
            mockCurrency.Object,
            GetMockLocalizerFactory().Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    // ── Index ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        _context.WorkCenters.AddRange(
            new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Assembly Line 1", PlantId = "P001", IsActive = true },
            new WorkCenter { WorkCenterCode = "WC002", WorkCenterName = "Assembly Line 2", PlantId = "P001", IsActive = true }
        );
        await _context.SaveChangesAsync();

        var result = await _controller.Index(null, 1, 10, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<PaginatedList<WorkCenter>>(view.Model);
        Assert.Equal(2, model.Count);
    }

    [Fact]
    public async Task Index_SetsCanCreateInViewBag()
    {
        var result = await _controller.Index(null, null, null, null, searched: false);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True((bool)view.ViewData["CanCreate"]!);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_ReturnsView()
    {
        var result = await _controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<WorkCenter>(view.Model);
        Assert.True(model.IsActive);
    }

    [Fact]
    public async Task Create_POST_ValidModel_AjaxReturnsSuccess()
    {
        SetAjax();
        var model = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Assembly Line 1", IsActive = true };

        var result = await _controller.Create(model, null);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    [Fact]
    public async Task Create_POST_DuplicateCode_ReturnsError()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Existing", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var model = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Duplicate", IsActive = true };

        var result = await _controller.Create(model, null);

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Edit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsView()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Assembly Line 1", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("WC001");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<WorkCenter>(view.Model);
        Assert.Equal("WC001", model.WorkCenterCode);
    }

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var result = await _controller.Edit("NONEXISTENT");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_POST_ValidModel_AjaxReturnsSuccess()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Old Name", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "New Name", IsActive = true };

        var result = await _controller.Edit("WC001", updated, null);

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AjaxReturnsSuccess()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "To Delete", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("WC001");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.Null(await _context.WorkCenters.FindAsync("WC001"));
    }

    [Fact]
    public async Task Delete_WorkCenterUsedInRoutingOperation_ReturnsError()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC002", WorkCenterName = "In Use", PlantId = "P001", IsActive = true });
        _context.RoutingOperations.Add(new SmartFactory.MES.Modules.MFG.Models.RoutingOperation
        {
            RoutingId = "RT001", OperationSeq = 1, WorkCenterId = "WC002", UomCode = "PCS",
        });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("WC002");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.NotNull(await _context.WorkCenters.FindAsync("WC002"));
    }

    [Fact]
    public async Task Delete_WorkCenterUsedInWorkOrderOperation_ReturnsError()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC003", WorkCenterName = "In Use 2", PlantId = "P001", IsActive = true });
        _context.WorkOrderOperations.Add(new SmartFactory.MES.Modules.MFG.Models.WorkOrderOperation
        {
            WorkOrderId = "WO001", OperationSeq = 1, WorkCenterId = "WC003",
        });
        await _context.SaveChangesAsync();

        SetAjax();
        var result = await _controller.DeleteConfirmed("WC003");

        var json = Assert.IsType<JsonResult>(result);
        var data = json.Value!;
        Assert.False((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
        Assert.NotNull(await _context.WorkCenters.FindAsync("WC003"));
    }

    // ── Permission ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_NoPrintPermission_ReturnsForbid()
    {
        _mockPerm.Setup(x => x.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        var result = await _controller.Export("excel", null);

        Assert.IsType<ForbidResult>(result);
    }

    // ── Machine CRUD ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_POST_WithMachines_SavesMachines()
    {
        SetAjax();
        var model = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = false };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "M01", MachineName = "Machine 1", MachineStatus = MachineStatus.Available },
            new WorkCenterMachine { MachineCode = "M02", MachineName = "Machine 2", MachineStatus = MachineStatus.BreakDown }
        };

        var result = await _controller.Create(model, machines);

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);

        var saved = _context.WorkCenterMachines.Where(m => m.WorkCenterCode == "WC001").ToList();
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, m => m.MachineCode == "M01" && m.MachineStatus == MachineStatus.Available);
        Assert.Contains(saved, m => m.MachineCode == "M02" && m.MachineStatus == MachineStatus.BreakDown);
    }

    [Fact]
    public async Task Create_POST_ProductionLine_SavesLines()
    {
        // 生產線工作中心可登錄多條線別（每筆＝一條線，排程視為平行資源）
        SetAjax();
        var model = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = true };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "L1", MachineName = "Line A", MachineStatus = MachineStatus.Available },
            new WorkCenterMachine { MachineCode = "L2", MachineName = "Line B", MachineStatus = MachineStatus.Maintenance }
        };

        await _controller.Create(model, machines);

        var saved = _context.WorkCenterMachines.Where(m => m.WorkCenterCode == "WC001").OrderBy(m => m.MachineCode).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal("Line A", saved[0].MachineName);
        Assert.Equal(MachineStatus.Maintenance, saved[1].MachineStatus);
    }

    [Fact]
    public async Task Edit_POST_WithMachines_SavesMachines()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = false };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "M01", MachineName = "Machine A", MachineStatus = MachineStatus.Maintenance },
            new WorkCenterMachine { MachineCode = "M02", MachineName = "Machine B", MachineStatus = MachineStatus.Retired }
        };

        var result = await _controller.Edit("WC001", updated, machines);

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);

        var saved = _context.WorkCenterMachines.Where(m => m.WorkCenterCode == "WC001").OrderBy(m => m.MachineCode).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal("Machine A", saved[0].MachineName);
        Assert.Equal(MachineStatus.Maintenance, saved[0].MachineStatus);
        Assert.Equal(MachineStatus.Retired, saved[1].MachineStatus);
    }

    [Fact]
    public async Task Edit_POST_ReplacesExistingMachines()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.AddRange(
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "OLD1", MachineName = "Old Machine 1" },
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "OLD2", MachineName = "Old Machine 2" }
        );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = false };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "NEW1", MachineName = "New Machine 1" }
        };

        await _controller.Edit("WC001", updated, machines);

        var saved = _context.WorkCenterMachines.Where(m => m.WorkCenterCode == "WC001").ToList();
        Assert.Single(saved);
        Assert.Equal("NEW1", saved[0].MachineCode);
    }

    [Fact]
    public async Task Edit_POST_ProductionLine_SavesLines()
    {
        // 生產線工作中心可登錄多條線別；編輯採「全刪重建」與一般機台一致
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.Add(
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "OLD", MachineName = "Old Line" }
        );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = true };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "L1", MachineName = "Line A" },
            new WorkCenterMachine { MachineCode = "L2", MachineName = "Line B" }
        };

        await _controller.Edit("WC001", updated, machines);

        var saved = _context.WorkCenterMachines.Where(m => m.WorkCenterCode == "WC001").OrderBy(m => m.MachineCode).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal("L1", saved[0].MachineCode);
        Assert.Equal("L2", saved[1].MachineCode);
    }

    [Fact]
    public async Task Edit_POST_SameMachineCode_NoTrackingConflict()
    {
        // Regression: editing with same MachineCode as existing should not throw PK conflict
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.Add(
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M01", MachineName = "Old Name" }
        );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true, IsProductionLine = false };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "M01", MachineName = "Updated Name", MachineStatus = MachineStatus.Loaned }
        };

        var result = await _controller.Edit("WC001", updated, machines);

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);

        var saved = _context.WorkCenterMachines.First(m => m.WorkCenterCode == "WC001" && m.MachineCode == "M01");
        Assert.Equal("Updated Name", saved.MachineName);
        // 既有機台的狀態不再從工作中心畫面改（2026-10-06）：要經「機台狀態異動」才會處理派工
        Assert.Equal(MachineStatus.Available, saved.MachineStatus);
    }

    [Fact]
    public async Task Edit_POST_NewMachine_TakesInitialStatus()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true };
        var machines = new[] { new WorkCenterMachine { MachineCode = "M02", MachineName = "New", MachineStatus = MachineStatus.Standby } };

        await _controller.Edit("WC001", updated, machines);

        Assert.Equal(MachineStatus.Standby, _context.WorkCenterMachines.Single(m => m.MachineCode == "M02").MachineStatus);
    }

    [Fact]
    public async Task Edit_POST_RemoveMachineWithOpenDispatch_IsRejected_AndMachineKept()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.AddRange(
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M01", MachineName = "Keep" },
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M02", MachineName = "Busy" });
        _context.Dispatches.Add(new SmartFactory.MES.Modules.MFG.Models.Dispatch
        {
            DispatchId = "D-1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = "P001", Quantity = 1,
            MachineCode = "M02", Status = SmartFactory.MES.Modules.MFG.Models.DispatchStatus.Scheduled
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true };
        var machines = new[] { new WorkCenterMachine { MachineCode = "M01", MachineName = "Keep" } };

        var result = await _controller.Edit("WC001", updated, machines);

        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.True(_context.WorkCenterMachines.Any(m => m.MachineCode == "M02"));
    }

    [Fact]
    public async Task Edit_POST_RemoveMachineWhoseDispatchesAreFinished_IsAllowed()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M02", MachineName = "Old" });
        _context.Dispatches.Add(new SmartFactory.MES.Modules.MFG.Models.Dispatch
        {
            DispatchId = "D-1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = "P001", Quantity = 1,
            MachineCode = "M02", Status = SmartFactory.MES.Modules.MFG.Models.DispatchStatus.Closed
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        SetAjax();
        var updated = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true };

        var result = await _controller.Edit("WC001", updated, Array.Empty<WorkCenterMachine>());

        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.WorkCenterMachines.Any(m => m.MachineCode == "M02"));
    }

    [Fact]
    public async Task Create_POST_MachineModelStateIgnored_WhenWorkCenterCodeMissing()
    {
        // machines[0].WorkCenterCode is not sent by form — ModelState should still be valid
        SetAjax();
        _controller.ModelState.AddModelError("machines[0].WorkCenterCode", "The WorkCenterCode field is required.");

        var model = new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", IsActive = true };
        var machines = new[]
        {
            new WorkCenterMachine { MachineCode = "M01", MachineName = "Machine 1" }
        };

        var result = await _controller.Create(model, machines);

        // Should succeed despite machines ModelState error (controller clears it)
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Edit_GET_IncludesMachines()
    {
        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC001", WorkCenterName = "Line 1", PlantId = "P001", IsActive = true });
        _context.WorkCenterMachines.AddRange(
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M01", MachineName = "Machine 1" },
            new WorkCenterMachine { WorkCenterCode = "WC001", MachineCode = "M02", MachineName = "Machine 2" }
        );
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("WC001");

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<WorkCenter>(view.Model);
        Assert.Equal(2, model.Machines.Count);
    }
}
