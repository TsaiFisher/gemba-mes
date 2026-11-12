using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 派工作業「暫停處理」三個動作的守門（2026-10-06）：恢復／換機續做／剩餘量結案。
/// 服務本身的狀態轉換見 <see cref="DispatchLifecycleServiceTests"/>，這裡只驗控制器擋下的情況與接線。
/// </summary>
public class DispatchHoldControllerTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IPermissionService> _perm = new();

    public DispatchHoldControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _perm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId });
        _context.WorkCenterMachines.AddRange(
            new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M1", MachineName = "M1", MachineStatus = MachineStatus.Available },
            new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M2", MachineName = "M2", MachineStatus = MachineStatus.Available },
            new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M3", MachineName = "M3", MachineStatus = MachineStatus.BreakDown });
        _context.SaveChanges();
    }

    private DispatchController MakeController()
    {
        var loc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        loc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        loc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
           .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        return new DispatchController(
            loc.Object,
            new Mock<IInventoryReservationService>().Object,
            new Mock<IPlantParamService>().Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            new FpiGateService(_context),
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()))
        {
            ControllerContext = GetControllerContextWithServices(permService: _perm.Object,
                configureServices: s => s.AddSingleton<IDispatchLifecycleService>(
                    new DispatchLifecycleService(_context, new FpiGateService(_context),
                                                 new ProductionCostAccrualService(_context))))
        };
    }

    private Dispatch SeedHeld(DispatchHoldReason reason, string machine = "M1", decimal done = 0)
    {
        var d = new Dispatch
        {
            DispatchId = "D-1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = PlantId,
            Quantity = 100, QtyDone = done, Status = DispatchStatus.InProgress, MachineCode = machine
        };
        DispatchHold.Hold(d, reason, DateTime.UtcNow);
        _context.Dispatches.Add(d);
        _context.SaveChanges();
        return d;
    }

    private static (bool Success, string Message) Read(IActionResult r)
    {
        var v = Assert.IsType<JsonResult>(r).Value!;
        return ((bool)v.GetType().GetProperty("success")!.GetValue(v)!,
                (string)(v.GetType().GetProperty("message")?.GetValue(v) ?? ""));
    }

    [Fact]
    public async Task Resume_QualityHold_WithoutInspectionPermission_IsRejected()
    {
        SeedHeld(DispatchHoldReason.IpqcFailed);
        _perm.Setup(p => p.CanUpdate("InspectionOrder")).ReturnsAsync(false);

        var (ok, msg) = Read(await MakeController().Resume("D-1"));

        Assert.False(ok);
        Assert.Equal("DispatchResumeNeedsQuality", msg);
        Assert.Equal(DispatchStatus.OnHold, _context.Dispatches.Single().Status);
    }

    [Fact]
    public async Task Resume_MachineStillDown_IsRejected()
    {
        SeedHeld(DispatchHoldReason.MachineDown, machine: "M3");

        var (ok, msg) = Read(await MakeController().Resume("D-1"));

        Assert.False(ok);
        Assert.Equal("DispatchResumeMachineDown", msg);
    }

    [Fact]
    public async Task Resume_MachineBack_ReturnsToPreviousStatus()
    {
        SeedHeld(DispatchHoldReason.MachineDown, done: 10);

        var (ok, _) = Read(await MakeController().Resume("D-1"));

        Assert.True(ok);
        var d = _context.Dispatches.Single();
        Assert.Equal(DispatchStatus.InProgress, d.Status);
        Assert.Null(d.HoldReason);
    }

    [Fact]
    public async Task SwitchMachine_TargetDown_IsRejected()
    {
        SeedHeld(DispatchHoldReason.MachineDown);

        var (ok, msg) = Read(await MakeController().SwitchMachine("D-1", "M3"));

        Assert.False(ok);
        Assert.Equal("DispatchSwitchMachineDown", msg);
    }

    [Fact]
    public async Task SwitchMachine_SameMachine_IsRejected()
    {
        SeedHeld(DispatchHoldReason.MachineDown);

        var (ok, msg) = Read(await MakeController().SwitchMachine("D-1", "M1"));

        Assert.False(ok);
        Assert.Equal("DispatchSwitchSameMachine", msg);
    }

    [Fact]
    public async Task SwitchMachine_WithOutput_CreatesLockedContinuation()
    {
        SeedHeld(DispatchHoldReason.MachineDown, done: 30);

        var (ok, _) = Read(await MakeController().SwitchMachine("D-1", "M2"));

        Assert.True(ok);
        var cont = _context.Dispatches.Single(d => d.DispatchId == "D-1-s1");
        Assert.Equal("M2", cont.MachineCode);
        Assert.Equal(70, cont.Quantity);
        Assert.True(cont.IsMachineLocked);
        Assert.Equal(DispatchStatus.Closed, _context.Dispatches.Single(d => d.DispatchId == "D-1").Status);
    }

    [Fact]
    public async Task ShortClose_NotOnHold_IsRejected()
    {
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D-1", WorkOrderId = "WO1", OperationSeq = 10, PlantId = PlantId,
            Quantity = 100, Status = DispatchStatus.InProgress, MachineCode = "M1"
        });
        _context.SaveChanges();

        var (ok, msg) = Read(await MakeController().ShortClose("D-1"));

        Assert.False(ok);
        Assert.Equal("DispatchNotOnHold", msg);
    }
}
