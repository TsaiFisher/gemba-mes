using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 派工暫停生命週期（2026-10-06）：
///   - 暫停一律記下原因與暫停前狀態，恢復回到原狀態（首件不合格回等首件）
///   - 機台停用：已排退回待排、已投入（準備中／準備完成／等首件／生產中）轉暫停；恢復可用自動解除
///   - 換機續做：無產出整張改掛、有產出原派工以完成量結案＋剩餘量開續做派工；續做鎖定新機台
///   - 剩餘量結案、工單作廢連帶取消
///   - 工單自動完工：暫停不算完成（舊規則會帶著沒做完的量靜默完工）
/// </summary>
public class DispatchLifecycleServiceTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateTime Now = new(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);

    public DispatchLifecycleServiceTests() => _context.CurrentPlantId = PlantId;

    private DispatchLifecycleService MakeService() =>
        new(_context, new FpiGateService(_context), new ProductionCostAccrualService(_context));

    private Dispatch AddDispatch(string id, DispatchStatus status, string machine = "M1",
        decimal qty = 100, decimal done = 0, string woId = "WO1", bool requiresFpi = false)
    {
        if (!_context.WorkOrders.Local.Any(w => w.WorkOrderId == woId))
        {
            _context.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = woId, ProductionBatchId = "B1", ProductCode = "P-1", RoutingId = "R1",
                Quantity = qty, Status = WorkOrderStatus.InProgress, PlantId = PlantId,
                PlannedStartDate = Now, PlannedEndDate = Now.AddDays(3)
            });
            _context.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = woId, OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1",
                RequiresFpi = requiresFpi
            });
        }
        var d = new Dispatch
        {
            DispatchId = id, WorkOrderId = woId, OperationSeq = 10, PlantId = PlantId,
            Quantity = qty, QtyDone = done, Status = status, MachineCode = machine,
            ToolingCode = "T-1",
            ScheduledStart = Now, ScheduledEnd = Now.AddHours(4)
        };
        _context.Dispatches.Add(d);
        return d;
    }

    // ── DispatchHold 純函式 ─────────────────────────────────────────────────

    [Fact]
    public void Hold_RecordsReasonAndPreviousStatus()
    {
        var d = new Dispatch { Status = DispatchStatus.InProgress };

        Assert.True(DispatchHold.Hold(d, DispatchHoldReason.IpqcFailed, Now));

        Assert.Equal(DispatchStatus.OnHold, d.Status);
        Assert.Equal(DispatchHoldReason.IpqcFailed, d.HoldReason);
        Assert.Equal(DispatchStatus.InProgress, d.HoldFromStatus);
        Assert.Equal(Now, d.HoldAt);
    }

    [Theory]
    [InlineData(DispatchStatus.Closed)]
    [InlineData(DispatchStatus.Cancelled)]
    [InlineData(DispatchStatus.OnHold)]
    public void Hold_FinishedOrAlreadyHeld_IsNoOp(DispatchStatus status)
    {
        var d = new Dispatch { Status = status, HoldReason = null };

        Assert.False(DispatchHold.Hold(d, DispatchHoldReason.MachineDown, Now));
        Assert.Equal(status, d.Status);
        Assert.Null(d.HoldReason);
    }

    [Fact]
    public void IsWorkOrderDone_OnHoldIsNotDone()
    {
        // 舊規則把暫停當完成——工單會帶著暫停派工的剩餘量靜默完工
        var list = new[]
        {
            new Dispatch { Status = DispatchStatus.Closed },
            new Dispatch { Status = DispatchStatus.OnHold }
        };
        Assert.False(DispatchHold.IsWorkOrderDone(list));
    }

    [Fact]
    public void IsWorkOrderDone_ClosedAndCancelled_IsDone()
    {
        var list = new[]
        {
            new Dispatch { Status = DispatchStatus.Closed },
            new Dispatch { Status = DispatchStatus.Cancelled }
        };
        Assert.True(DispatchHold.IsWorkOrderDone(list));
    }

    [Fact]
    public void IsWorkOrderDone_AllCancelled_IsNotDone()
    {
        var list = new[] { new Dispatch { Status = DispatchStatus.Cancelled } };
        Assert.False(DispatchHold.IsWorkOrderDone(list));
    }

    // ── 機台停用／恢復 ─────────────────────────────────────────────────────

    [Fact]
    public async Task MachineDown_UnschedulesScheduled_AndHoldsEveryStartedState()
    {
        AddDispatch("D-SCH", DispatchStatus.Scheduled);
        AddDispatch("D-SET", DispatchStatus.SetupInProgress);
        AddDispatch("D-RDY", DispatchStatus.ReadyToStart);
        AddDispatch("D-FPI", DispatchStatus.PendingFPI);
        AddDispatch("D-RUN", DispatchStatus.InProgress, done: 30);
        AddDispatch("D-PEN", DispatchStatus.Pending);
        AddDispatch("D-OTHER", DispatchStatus.InProgress, machine: "M2");
        await _context.SaveChangesAsync();

        var effect = await MakeService().ApplyMachineStatusAsync("M1", PlantId, MachineStatus.BreakDown, Now);
        await _context.SaveChangesAsync();

        Assert.Equal(new MachineDispatchEffect(1, 4, 0), effect);
        Assert.Equal(DispatchStatus.Pending, Get("D-SCH").Status);
        foreach (var (id, from) in new[]
                 {
                     ("D-SET", DispatchStatus.SetupInProgress), ("D-RDY", DispatchStatus.ReadyToStart),
                     ("D-FPI", DispatchStatus.PendingFPI),      ("D-RUN", DispatchStatus.InProgress)
                 })
        {
            var d = Get(id);
            Assert.Equal(DispatchStatus.OnHold, d.Status);
            Assert.Equal(DispatchHoldReason.MachineDown, d.HoldReason);
            Assert.Equal(from, d.HoldFromStatus);
        }
        Assert.Equal(DispatchStatus.Pending, Get("D-PEN").Status);
        Assert.Equal(DispatchStatus.InProgress, Get("D-OTHER").Status);
    }

    [Fact]
    public async Task MachineAvailable_ResumesOnlyMachineDownHolds()
    {
        var a = AddDispatch("D-A", DispatchStatus.InProgress, done: 10);
        var b = AddDispatch("D-B", DispatchStatus.ReadyToStart);
        var q = AddDispatch("D-Q", DispatchStatus.InProgress, done: 10);
        DispatchHold.Hold(a, DispatchHoldReason.MachineDown, Now);
        DispatchHold.Hold(b, DispatchHoldReason.MachineDown, Now);
        DispatchHold.Hold(q, DispatchHoldReason.IpqcFailed, Now);
        await _context.SaveChangesAsync();

        var effect = await MakeService().ApplyMachineStatusAsync("M1", PlantId, MachineStatus.Available, Now);

        Assert.Equal(2, effect.Resumed);
        Assert.Equal(DispatchStatus.InProgress, a.Status);
        Assert.Equal(DispatchStatus.ReadyToStart, b.Status);
        Assert.Null(a.HoldReason);
        Assert.Equal(DispatchStatus.OnHold, q.Status);   // 品質暫停要品管放行，不隨機台恢復
    }

    // ── 恢復 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_FpiFailed_ReturnsToPendingFpi()
    {
        var d = AddDispatch("D-1", DispatchStatus.PendingFPI, requiresFpi: true);
        DispatchHold.Hold(d, DispatchHoldReason.FpiFailed, Now);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.PendingFPI, await MakeService().ResumeAsync(d, Now));
    }

    [Fact]
    public async Task Resume_LegacyHoldWithOutput_ReturnsToInProgress()
    {
        var d = AddDispatch("D-1", DispatchStatus.OnHold, done: 20);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.InProgress, await MakeService().ResumeAsync(d, Now));
    }

    [Fact]
    public async Task Resume_LegacyHoldWithoutOutput_FpiNotPassed_ReturnsToPendingFpi()
    {
        // 舊資料沒記暫停前狀態：無產出時推定為準備完成，但仍須過首件閘口，否則沒通過首件就能開工
        var d = AddDispatch("D-1", DispatchStatus.OnHold, requiresFpi: true);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.PendingFPI, await MakeService().ResumeAsync(d, Now));
    }

    // ── 換機續做 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SwitchMachine_NoOutput_MovesWholeDispatch_LockedAndPending()
    {
        var d = AddDispatch("D-1", DispatchStatus.SetupInProgress);
        DispatchHold.Hold(d, DispatchHoldReason.MachineDown, Now);
        _context.ProductionSetups.Add(new ProductionSetup
        {
            SetupId = "S-1", DispatchId = "D-1", WorkOrderId = "WO1", PlantId = PlantId,
            Status = ProductionSetupStatus.Open, SetupStartAt = Now.AddHours(-1)
        });
        await _context.SaveChangesAsync();

        var cont = await MakeService().SwitchMachineAsync(d, "M2", Now);
        await _context.SaveChangesAsync();

        Assert.Same(d, cont);
        Assert.Equal("M2", d.MachineCode);
        Assert.True(d.IsMachineLocked);
        Assert.Equal(DispatchStatus.Pending, d.Status);
        Assert.Null(d.ScheduledStart);
        Assert.Null(d.HoldReason);
        Assert.Equal(ProductionSetupStatus.Closed, _context.ProductionSetups.Single().Status);
    }

    [Fact]
    public async Task SwitchMachine_WithOutput_ClosesOriginalAtDone_AndContinuesRemainder()
    {
        var d = AddDispatch("D-1", DispatchStatus.InProgress, qty: 100, done: 40);
        DispatchHold.Hold(d, DispatchHoldReason.MachineDown, Now);
        _context.ProductionSessions.Add(new ProductionSession
        {
            SessionId = "SS-1", DispatchId = "D-1", WorkOrderId = "WO1", PlantId = PlantId,
            Status = ProductionSessionStatus.Open, StartAt = Now.AddHours(-2), MachineCode = "M1"
        });
        await _context.SaveChangesAsync();

        var cont = await MakeService().SwitchMachineAsync(d, "M2", Now);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.Closed, d.Status);
        Assert.Equal(40, d.Quantity);
        Assert.Equal("M1", d.MachineCode);

        Assert.Equal("D-1-s1", cont.DispatchId);
        Assert.Equal(60, cont.Quantity);
        Assert.Equal(0, cont.QtyDone);
        Assert.Equal("M2", cont.MachineCode);
        Assert.True(cont.IsMachineLocked);
        Assert.Equal(DispatchStatus.Pending, cont.Status);
        Assert.Equal("T-1", cont.ToolingCode);
        Assert.Equal("D-1", cont.SplitGroupId);
        Assert.Equal("D-1", d.SplitGroupId);

        // 機台停用時沒人按下工：上工紀錄要收掉，否則工時無限累計
        var session = _context.ProductionSessions.Single();
        Assert.Equal(ProductionSessionStatus.Closed, session.Status);
        Assert.Equal(Now, session.EndAt);
    }

    [Fact]
    public async Task SwitchMachine_NotOnHold_Throws()
    {
        var d = AddDispatch("D-1", DispatchStatus.InProgress);
        await _context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => MakeService().SwitchMachineAsync(d, "M2", Now));
    }

    // ── 剩餘量結案／作廢連帶取消 ────────────────────────────────────────────

    [Fact]
    public async Task ShortClose_WithOutput_ClosesDispatch_AndCompletesWorkOrder()
    {
        AddDispatch("D-DONE", DispatchStatus.Closed, done: 100);
        var d = AddDispatch("D-1", DispatchStatus.InProgress, done: 30);
        DispatchHold.Hold(d, DispatchHoldReason.IpqcFailed, Now);
        await _context.SaveChangesAsync();

        await MakeService().ShortCloseAsync(d, Now);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.Closed, d.Status);
        Assert.Null(d.HoldReason);
        Assert.Equal(WorkOrderStatus.Completed, _context.WorkOrders.Single().Status);
    }

    [Fact]
    public async Task ShortClose_WithoutOutput_Cancels_AndClearsSlot()
    {
        var d = AddDispatch("D-1", DispatchStatus.ReadyToStart);
        DispatchHold.Hold(d, DispatchHoldReason.MachineDown, Now);
        await _context.SaveChangesAsync();

        await MakeService().ShortCloseAsync(d, Now);
        await _context.SaveChangesAsync();

        Assert.Equal(DispatchStatus.Cancelled, d.Status);
        Assert.Null(d.ScheduledStart);
        Assert.Null(d.ScheduledEnd);
        // 只有取消、沒有任何完工：工單不得自動完工
        Assert.Equal(WorkOrderStatus.InProgress, _context.WorkOrders.Single().Status);
    }

    [Fact]
    public async Task CancelOpenDispatches_CancelsEverythingNotFinished()
    {
        AddDispatch("D-CLOSED", DispatchStatus.Closed, done: 100);
        AddDispatch("D-PEN", DispatchStatus.Pending);
        AddDispatch("D-SCH", DispatchStatus.Scheduled);
        AddDispatch("D-RDY", DispatchStatus.ReadyToStart);
        await _context.SaveChangesAsync();

        var n = await MakeService().CancelOpenDispatchesAsync("WO1", Now);
        await _context.SaveChangesAsync();

        Assert.Equal(3, n);
        Assert.Equal(DispatchStatus.Closed, Get("D-CLOSED").Status);
        Assert.All(new[] { "D-PEN", "D-SCH", "D-RDY" }, id =>
        {
            Assert.Equal(DispatchStatus.Cancelled, Get(id).Status);
            Assert.Null(Get(id).ScheduledStart);
        });
    }

    private Dispatch Get(string id) => _context.Dispatches.Single(d => d.DispatchId == id);
}
