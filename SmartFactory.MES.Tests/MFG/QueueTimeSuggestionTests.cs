using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 途程工序等候時間建議值（2026-10-06 改寫，mfg-domain.md §23.2-2）。
/// 原作法：背景撈 0 筆從未生效；且首道工序以工單建立時間當到達、樣本記到後一道工序、
/// 搬運時間重複計入、夜間假日算成等候，又直接改寫途程主檔（違反規格「建議值＋人工確認」）。
/// </summary>
public class QueueTimeSuggestionTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateTime UtcNow = new(2026, 10, 6, 4, 0, 0, DateTimeKind.Utc);   // 台北 12:00

    // ── 純函式 ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<(int, int)> Shift(DateOnly _) => new[] { (480, 720), (780, 1020) };   // 08–12、13–17

    [Fact]
    public void WorkingMinutes_OvernightCountsOnlyShiftTime()
    {
        // 前一天 16:00 → 隔天 09:00：只有 16–17 與 08–09 兩小時
        var from = new DateTime(2026, 10, 1, 16, 0, 0);
        var to   = new DateTime(2026, 10, 2, 9, 0, 0);
        Assert.Equal(120m, QueueTimeSuggestionService.WorkingMinutesBetween(from, to, Shift));
    }

    [Fact]
    public void WorkingMinutes_LunchBreakAndOverlap()
    {
        Assert.Equal(60m, QueueTimeSuggestionService.WorkingMinutesBetween(
            new DateTime(2026, 10, 1, 11, 30, 0), new DateTime(2026, 10, 1, 13, 30, 0), Shift));   // 午休不算
        Assert.Equal(0m, QueueTimeSuggestionService.WorkingMinutesBetween(
            new DateTime(2026, 10, 1, 10, 0, 0), new DateTime(2026, 10, 1, 9, 0, 0), Shift));      // 工序重疊
    }

    // ── 統計（背景狀態：無廠別、無角色）─────────────────────────────────────────

    /// <summary>
    /// <paramref name="n"/> 張工單，每張兩道工序：OP10 前一天 16:00（台北）下工，OP20 隔天 09:00 上工
    /// → 工作時間間隔 120 分。OP10 搬運時間 20 分。
    /// </summary>
    private void Seed(int n, int daysAgo = 3, decimal moveMinutes = 20)
    {
        _context.CurrentPlantId = PlantId;
        _context.Plants.Add(new Plant { Id = PlantId, Name = "P", DefaultTimeZone = "Asia/Taipei" });
        _context.Routings.Add(new Routing { RoutingId = "R1", PlantId = PlantId, ProductCode = "P" });
        _context.Set<RoutingOperation>().Add(new RoutingOperation
        {
            RoutingId = "R1", OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1",
            TimeUnit = OperationTimeUnit.Minute, MoveTime = moveMinutes, QueueTime = 0
        });
        _context.Set<RoutingOperation>().Add(new RoutingOperation
        {
            RoutingId = "R1", OperationSeq = 20, OperationName = "OP20", WorkCenterId = "WC1",
            TimeUnit = OperationTimeUnit.Minute, QueueTime = 0
        });
        var localDay = DateOnly.FromDateTime(UtcNow).AddDays(-daysAgo);
        for (var d = -1; d <= daysAgo + 1; d++)
        {
            var date = localDay.AddDays(d);
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment { PlantId = PlantId, Date = date, Seq = 1, StartMinute = 480, EndMinute = 720 });
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment { PlantId = PlantId, Date = date, Seq = 2, StartMinute = 780, EndMinute = 1020 });
        }

        for (var i = 0; i < n; i++)
        {
            var wo = $"WO{i}";
            _context.WorkOrders.Add(new WorkOrder
            {
                WorkOrderId = wo, ProductionBatchId = "B", ProductCode = "P", RoutingId = "R1", Quantity = 1,
                Status = WorkOrderStatus.Closed, PlantId = PlantId, CreatedByRoleId = "R1",
                CreatedAt = UtcNow.AddDays(-60),   // 工單很早就建立——舊作法會把這段當成首道工序的等候
                PlannedStartDate = UtcNow, PlannedEndDate = UtcNow
            });
            foreach (var seq in new[] { 10, 20 })
                _context.WorkOrderOperations.Add(new WorkOrderOperation { WorkOrderId = wo, OperationSeq = seq, WorkCenterId = "WC1", OperationName = "OP" });
            _context.Dispatches.Add(new Dispatch
            {
                DispatchId = $"{wo}-10", WorkOrderId = wo, OperationSeq = 10, PlantId = PlantId, CreatedByRoleId = "R1",
                Quantity = 1, QtyDone = 1, Status = DispatchStatus.Closed, MachineCode = "M1"
            });
            _context.Dispatches.Add(new Dispatch
            {
                DispatchId = $"{wo}-20", WorkOrderId = wo, OperationSeq = 20, PlantId = PlantId, CreatedByRoleId = "R1",
                Quantity = 1, QtyDone = 1, Status = DispatchStatus.Closed, MachineCode = "M2", PrevDispatchId = $"{wo}-10"
            });
            // 台北 = UTC+8：前一天 16:00 = 08:00Z；隔天 09:00 = 01:00Z
            var day1 = localDay.ToDateTime(TimeOnly.MinValue);
            _context.ProductionSessions.Add(new ProductionSession
            {
                SessionId = $"S{i}-10", DispatchId = $"{wo}-10", WorkOrderId = wo, PlantId = PlantId, Status = ProductionSessionStatus.Closed,
                StartAt = DateTime.SpecifyKind(day1.AddHours(0), DateTimeKind.Utc), EndAt = DateTime.SpecifyKind(day1.AddHours(8), DateTimeKind.Utc)
            });
            _context.ProductionSessions.Add(new ProductionSession
            {
                SessionId = $"S{i}-20", DispatchId = $"{wo}-20", WorkOrderId = wo, PlantId = PlantId, Status = ProductionSessionStatus.Closed,
                StartAt = DateTime.SpecifyKind(day1.AddDays(1).AddHours(1), DateTimeKind.Utc), EndAt = DateTime.SpecifyKind(day1.AddDays(1).AddHours(3), DateTimeKind.Utc)
            });
        }
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        _context.CurrentPlantId = null;   // 背景的樣子
        _context.CurrentRoleId = null;
        _context.CurrentRolePath = null;
    }

    private Task<int> Run() => new QueueTimeSuggestionService(_context).RecalculateAsync(UtcNow);

    [Fact]
    public async Task Suggestion_GoesToPreviousOperation_WorkingTime_MinusMove_AndDoesNotTouchRouting()
    {
        Seed(5);

        Assert.Equal(1, await Run());

        var s = _context.RoutingQueueTimeSuggestions.Single();
        Assert.Equal(("R1", 10), (s.RoutingId, s.OperationSeq));   // 等候放在前一道之後（與排程語意一致）
        Assert.Equal(120m, s.MedianGapMinutes);                      // 夜間不算等候
        Assert.Equal(100m, s.SuggestedQueueMinutes);                 // 扣掉搬運 20 分
        Assert.Equal(5, s.SampleCount);
        // 首道工序不產生樣本（舊作法：以 60 天前的工單建立時間當到達，算出兩個月的「等候」）
        Assert.DoesNotContain(_context.RoutingQueueTimeSuggestions, x => x.OperationSeq == 20);
        // 不改途程主檔——由工藝人員確認後套用
        Assert.All(_context.Set<RoutingOperation>().AsNoTracking().ToList(), ro => Assert.Equal(0m, ro.QueueTime));
    }

    [Fact]
    public async Task FewerThanFiveSamples_NoSuggestion()
    {
        Seed(4);
        Assert.Equal(0, await Run());
    }

    [Fact]
    public async Task OlderThan90Days_NotSampled()
    {
        Seed(5, daysAgo: 100);
        Assert.Equal(0, await Run());
    }

    [Fact]
    public async Task Rerun_ReplacesPreviousSuggestions()
    {
        Seed(5);
        await Run();
        _context.RoutingQueueTimeSuggestions.Add(new RoutingQueueTimeSuggestion { RoutingId = "OLD", OperationSeq = 1 });
        _context.SaveChanges();

        await Run();

        Assert.Single(_context.RoutingQueueTimeSuggestions);
    }
}
