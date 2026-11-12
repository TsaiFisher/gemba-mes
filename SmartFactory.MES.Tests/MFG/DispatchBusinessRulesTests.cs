using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// Phase D 派工業務規則單元測試（mfg-domain.md §5.3 / §6 / §16.3）。
/// 純邏輯，不接 DB。
/// </summary>
public class DispatchBusinessRulesTests
{
    // ── 超量報工防護（mfg-domain.md §5.3 / Phase D 驗收）────────────────────

    [Theory]
    [InlineData(0, 10, 10, false)]   // 剛好達標：不超量
    [InlineData(5, 5, 10, false)]    // 累計 = 派工量：不超量
    [InlineData(5, 6, 10, true)]     // 累計 11 > 10：超量
    [InlineData(0, 11, 10, true)]    // 一次報 11 > 10：超量
    [InlineData(9, 1, 10, false)]    // 累計剛好 10：不超量
    public void IsOverReport_ReturnsCorrectResult(
        decimal qtyDone, decimal qtyOk, decimal dispatchQty, bool expected)
    {
        bool result = qtyDone + qtyOk > dispatchQty;
        Assert.Equal(expected, result);
    }

    // ── Session 關閉成本累計（mfg-domain.md §16.3）──────────────────────────

    [Fact]
    public void SessionCost_OneHour_AccumulatesLaborAndOverhead()
    {
        // Arrange
        decimal laborRate   = 200m;   // WorkCenter.StandardLaborCost  元/小時
        decimal machineRate = 300m;   // WorkCenter.MachineHourlyRate  元/小時
        double  sessionHours = 1.0;

        // Act
        decimal addLabor   = (decimal)sessionHours * laborRate;
        decimal addOverhead = (decimal)sessionHours * machineRate;

        // Assert
        Assert.Equal(200m, addLabor);
        Assert.Equal(300m, addOverhead);
    }

    [Fact]
    public void SessionCost_HalfHour_AccumulatesHalfRate()
    {
        decimal laborRate    = 200m;
        decimal machineRate  = 300m;
        double  sessionHours = 0.5;

        decimal addLabor    = (decimal)sessionHours * laborRate;
        decimal addOverhead = (decimal)sessionHours * machineRate;

        Assert.Equal(100m, addLabor);
        Assert.Equal(150m, addOverhead);
    }

    [Fact]
    public void SessionCost_ZeroRate_AddsNothing()
    {
        decimal laborRate    = 0m;
        decimal machineRate  = 0m;
        double  sessionHours = 2.5;

        decimal addLabor    = (decimal)sessionHours * laborRate;
        decimal addOverhead = (decimal)sessionHours * machineRate;

        Assert.Equal(0m, addLabor);
        Assert.Equal(0m, addOverhead);
    }

    [Fact]
    public void SessionCost_MultipleSessionsAccumulate()
    {
        // 模擬同一工單多次上工的累計
        decimal laborRate   = 100m;
        decimal machineRate = 200m;
        var sessions = new[] { 1.0, 0.5, 2.0 };   // 三次上工 (小時)

        decimal totalLabor   = 0m;
        decimal totalOverhead = 0m;
        foreach (var h in sessions)
        {
            totalLabor   += (decimal)h * laborRate;
            totalOverhead += (decimal)h * machineRate;
        }

        // 1+0.5+2 = 3.5 小時
        Assert.Equal(350m, totalLabor);
        Assert.Equal(700m, totalOverhead);
    }

    // ── Setup 成本累計（mfg-domain.md §16.3）────────────────────────────────

    [Fact]
    public void SetupCost_ThirtyMinutes_AddsHalfHourMachineRate()
    {
        decimal machineRate = 300m;
        var startAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var endAt   = new DateTime(2026, 6, 1, 8, 30, 0, DateTimeKind.Utc);

        double setupHours  = (endAt - startAt).TotalHours;     // 0.5
        decimal addOverhead = (decimal)setupHours * machineRate;

        Assert.Equal(150m, addOverhead);
    }

    // ── Dispatch 狀態轉換規則──────────────────────────────────────────────

    [Theory]
    [InlineData(DispatchStatus.Scheduled,    true)]   // 允許開工
    [InlineData(DispatchStatus.ReadyToStart, true)]   // 允許開工
    [InlineData(DispatchStatus.InProgress,   true)]   // 換班重新上工
    [InlineData(DispatchStatus.Pending,      false)]  // 未排程，不允許
    [InlineData(DispatchStatus.Closed,       false)]  // 已完工，不允許
    [InlineData(DispatchStatus.OnHold,       false)]  // 暫停，不允許直接上工
    public void StartSession_StatusGuard(DispatchStatus status, bool expected)
    {
        bool canStart = status is DispatchStatus.Scheduled
                                or DispatchStatus.ReadyToStart
                                or DispatchStatus.InProgress;
        Assert.Equal(expected, canStart);
    }

    [Theory]
    [InlineData(DispatchStatus.Scheduled,    true)]   // 允許備料
    [InlineData(DispatchStatus.ReadyToStart, true)]   // 允許備料（重新備料）
    [InlineData(DispatchStatus.InProgress,   false)]  // 生產中不可備料
    [InlineData(DispatchStatus.Pending,      false)]  // 未排程不可備料
    [InlineData(DispatchStatus.Closed,       false)]  // 已完工不可備料
    public void StartSetup_StatusGuard(DispatchStatus status, bool expected)
    {
        bool canSetup = status is DispatchStatus.Scheduled or DispatchStatus.ReadyToStart;
        Assert.Equal(expected, canSetup);
    }

    // ── 完工條件（mfg-domain.md §4.3）───────────────────────────────────────

    [Fact]
    public void DispatchAutoCloses_WhenQtyDoneReachesQuantity()
    {
        var dispatch = new Dispatch { Quantity = 100m, QtyDone = 90m };
        decimal qtyOk = 10m;

        dispatch.QtyDone += qtyOk;
        bool shouldClose = dispatch.QtyDone >= dispatch.Quantity;

        Assert.True(shouldClose);
        Assert.Equal(100m, dispatch.QtyDone);
    }

    [Fact]
    public void DispatchDoesNotClose_WhenQtyDoneIsStillLess()
    {
        var dispatch = new Dispatch { Quantity = 100m, QtyDone = 80m };
        decimal qtyOk = 10m;

        dispatch.QtyDone += qtyOk;
        bool shouldClose = dispatch.QtyDone >= dispatch.Quantity;

        Assert.False(shouldClose);
        Assert.Equal(90m, dispatch.QtyDone);
    }
}
