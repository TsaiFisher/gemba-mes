using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// Phase C 例外調度 API 單元測試：換機台、拆分、轉委外（mfg-domain.md §10-7/§10-8）。
/// 驗證狀態守門、資料轉換、DB 寫入結果。
/// </summary>
public class DispatchControllerAdjustmentTests : TestBase
{
    private readonly DispatchController _ctrl;
    private readonly Mock<IPermissionService> _mockPerm;
    private const string PlantId = "P001";

    public DispatchControllerAdjustmentTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        var mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        mockLoc.Setup(x => x[It.IsAny<string>()])
               .Returns<string>(k => new LocalizedString(k, k));
        mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
               .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        _ctrl = new DispatchController(
            mockLoc.Object,
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
            ControllerContext = GetControllerContextWithServices(permService: _mockPerm.Object)
        };
    }

    private async Task<Dispatch> SeedDispatch(
        string id, DispatchStatus status, decimal qty = 10m, string? splitGroupId = null)
    {
        var d = new Dispatch
        {
            DispatchId     = id,
            WorkOrderId    = "WO001",
            OperationSeq   = 1,
            PlantId        = PlantId,
            Quantity       = qty,
            Status         = status,
            ScheduledStart = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            ScheduledEnd   = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            MachineCode    = "MC01",
            SplitGroupId   = splitGroupId,
            MaterialRisk   = true,
        };
        _context.Dispatches.Add(d);
        await _context.SaveChangesAsync();
        return d;
    }

    private async Task SeedMachine(string machineCode, MachineStatus status = MachineStatus.Available, string wc = "WC1")
    {
        _context.WorkCenterMachines.Add(new WorkCenterMachine
        {
            WorkCenterCode = wc, MachineCode = machineCode,
            MachineName = machineCode, MachineStatus = status,
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(JsonResult json)
    {
        dynamic data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    // ── ReassignMachine ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(DispatchStatus.InProgress)]
    [InlineData(DispatchStatus.Closed)]
    [InlineData(DispatchStatus.OnHold)]
    public async Task ReassignMachine_InvalidStatus_ReturnsFail(DispatchStatus status)
    {
        await SeedDispatch("D001", status);

        var result = await _ctrl.ReassignMachine("D001", "MC02");

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Fact]
    public async Task ReassignMachine_BlankCode_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled);

        var result = await _ctrl.ReassignMachine("D001", "  ");

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Theory]
    [InlineData(DispatchStatus.Pending)]
    [InlineData(DispatchStatus.Scheduled)]
    public async Task ReassignMachine_ValidStatus_UpdatesMachineAndClearsSchedule(DispatchStatus status)
    {
        await SeedDispatch("D001", status);
        await SeedMachine("MC99");   // 目標機台須存在（跨工作中心允許，僅驗證有效性）

        var result = await _ctrl.ReassignMachine("D001", " MC99 ");

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));

        var updated = await _context.Dispatches.FindAsync("D001");
        Assert.Equal("MC99", updated!.MachineCode);      // trimmed
        Assert.Null(updated.ScheduledStart);
        Assert.Null(updated.ScheduledEnd);
        Assert.False(updated.MaterialRisk);
        Assert.Equal(DispatchStatus.Pending, updated.Status);
        Assert.True(updated.IsMachineLocked);             // 人指定的機台：重排不得改回
    }

    [Fact]
    public async Task ReassignMachine_UnknownMachine_ReturnsFail_MachineUnchanged()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled);   // 不種任何機台

        var result = await _ctrl.ReassignMachine("D001", "GHOST");

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal("MC01", d!.MachineCode);                  // 未被改成不存在代碼
        Assert.Equal(DispatchStatus.Scheduled, d.Status);
    }

    [Fact]
    public async Task ReassignMachine_RetiredMachine_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled);
        await SeedMachine("MC-RET", MachineStatus.Retired);     // 廢棄機台不可指派

        var result = await _ctrl.ReassignMachine("D001", "MC-RET");

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal("MC01", d!.MachineCode);
    }

    // ── ToOutsource ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DispatchStatus.InProgress)]
    [InlineData(DispatchStatus.Closed)]
    public async Task ToOutsource_InvalidStatus_ReturnsFail(DispatchStatus status)
    {
        await SeedDispatch("D001", status);

        var result = await _ctrl.ToOutsource("D001");

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Theory]
    [InlineData(DispatchStatus.Pending)]
    [InlineData(DispatchStatus.Scheduled)]
    public async Task ToOutsource_ValidStatus_SetsOutsourceAndClearsSlot(DispatchStatus status)
    {
        await SeedDispatch("D001", status);

        var result = await _ctrl.ToOutsource("D001");

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));

        var updated = await _context.Dispatches.FindAsync("D001");
        Assert.True(updated!.IsOutsource);
        Assert.Null(updated.MachineCode);
        Assert.Null(updated.ScheduledStart);
        Assert.Null(updated.ScheduledEnd);
        Assert.False(updated.MaterialRisk);
        Assert.Equal(DispatchStatus.Pending, updated.Status);
    }

    // ── Split ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DispatchStatus.InProgress)]
    [InlineData(DispatchStatus.Closed)]
    public async Task Split_InvalidStatus_ReturnsFail(DispatchStatus status)
    {
        await SeedDispatch("D001", status, qty: 10m);

        var result = await _ctrl.Split("D001",
            new List<SplitItemDto> { new() { Qty = 6m }, new() { Qty = 4m } });

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Fact]
    public async Task Split_SingleItem_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled, qty: 10m);

        var result = await _ctrl.Split("D001",
            new List<SplitItemDto> { new() { Qty = 10m } });

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Fact]
    public async Task Split_QtySumMismatch_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled, qty: 10m);

        // sum=9 ≠ 10
        var result = await _ctrl.Split("D001",
            new List<SplitItemDto> { new() { Qty = 6m }, new() { Qty = 3m } });

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Fact]
    public async Task Split_NegativeQtyItem_ReturnsFail()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled, qty: 10m);

        // sum=10 but one item is negative
        var result = await _ctrl.Split("D001",
            new List<SplitItemDto> { new() { Qty = 12m }, new() { Qty = -2m } });

        Assert.False(IsSuccess(Assert.IsType<JsonResult>(result)));
    }

    [Fact]
    public async Task Split_TwoWayValid_UpdatesOriginalAndCreatesChild()
    {
        await SeedDispatch("D001", DispatchStatus.Scheduled, qty: 10m);

        var result = await _ctrl.Split("D001", new List<SplitItemDto>
        {
            new() { MachineCode = "MC01", Qty = 6m },
            new() { MachineCode = "MC02", Qty = 4m },
        });

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));

        // 原派工
        var original = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(6m, original!.Quantity);
        Assert.Equal("MC01", original.MachineCode);
        Assert.Equal("D001", original.SplitGroupId);    // groupId = 自身 DispatchId（首次拆分）
        Assert.Equal(DispatchStatus.Pending, original.Status);
        Assert.Null(original.ScheduledStart);

        // 子派工
        var child = await _context.Dispatches.FindAsync("D001-s1");
        Assert.NotNull(child);
        Assert.Equal(4m, child!.Quantity);
        Assert.Equal("MC02", child.MachineCode);
        Assert.Equal("D001", child.SplitGroupId);
        Assert.Equal("WO001", child.WorkOrderId);
        Assert.Equal(1, child.OperationSeq);
        Assert.Equal(DispatchStatus.Pending, child.Status);
    }

    [Fact]
    public async Task Split_ThreeWay_CreatesTwoChildren()
    {
        await SeedDispatch("D001", DispatchStatus.Pending, qty: 12m);

        var result = await _ctrl.Split("D001", new List<SplitItemDto>
        {
            new() { Qty = 4m },
            new() { Qty = 4m },
            new() { Qty = 4m },
        });

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));

        Assert.NotNull(await _context.Dispatches.FindAsync("D001"));
        Assert.NotNull(await _context.Dispatches.FindAsync("D001-s1"));
        Assert.NotNull(await _context.Dispatches.FindAsync("D001-s2"));
    }

    [Fact]
    public async Task Split_AlreadySplit_PreservesExistingSplitGroupId()
    {
        // 此派工已屬於 split group "D000"，再次拆分時 groupId 應沿用，不得覆蓋
        await SeedDispatch("D001-s1", DispatchStatus.Pending, qty: 10m, splitGroupId: "D000");

        var result = await _ctrl.Split("D001-s1", new List<SplitItemDto>
        {
            new() { Qty = 5m },
            new() { Qty = 5m },
        });

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));

        var updated = await _context.Dispatches.FindAsync("D001-s1");
        Assert.Equal("D000", updated!.SplitGroupId);    // preserved
    }
}
