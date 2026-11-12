using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 2026-10-14 自動重排補強：
///   - 排程紀錄：每次全量排程寫一筆紀錄＋派工異動明細（新排入／改時段／換機台／退回待排、影響明天）
///   - 凍結時界：自動重排時，已排且在時界內開工的派工保留原機台與時段；手動排程不套用
///   - 自動重排規則：依廠別參數（未設定＝Priority）
/// 排程引擎用真的（InMemory），只替換廠別參數與推播。
/// </summary>
public class ScheduleRunLogTests : TestBase
{
    private const string PlantId = "P001";
    // 時刻數字即廠別當地時間（Kind=Utc 只是標籤）
    private static readonly DateTime Today = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past = DateTime.UtcNow.AddHours(-1);

    private readonly Mock<IPlantParamService> _params = new();
    private readonly Dictionary<string, string?> _values = new();

    public ScheduleRunLogTests()
    {
        _context.CurrentPlantId = PlantId;
        _params.Setup(p => p.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((string k, string? _, CancellationToken _) => _values.GetValueOrDefault(k));

        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId });
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M1", MachineStatus = MachineStatus.Available });
        // 全天 24 小時的日曆：工時直接等於時鐘時間，預期值好算
        for (int i = -1; i < 70; i++)
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, Date = DateOnly.FromDateTime(Today).AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440
            });
        _context.SaveChanges();
    }

    private SchedulingEngine Engine() => new(_context, _params.Object);

    private AutoScheduleService Service()
    {
        var clock = new Mock<IDateTimeService>();
        clock.Setup(d => d.Now).Returns(Today);
        return new AutoScheduleService(_context, Engine(), _params.Object, new Mock<ISystemParamService>().Object,
            new Mock<IPushNotificationService>().Object, clock.Object, NullLogger<AutoScheduleService>.Instance);
    }

    /// <summary>一張工單一道工序一張派工；工時＝<paramref name="minutes"/> 分（單件 1 分 × 數量）。</summary>
    private Dispatch AddWo(string woId, int minutes, int priority, DateTime? due = null)
    {
        var batch = "B-" + woId;
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = batch, MrpRunId = "MRP", MrpRunLineSeq = 1, ProductCode = "P", Quantity = minutes, Priority = priority,
            PlannedEndDate = due ?? Today.AddDays(30), PlantId = PlantId, CreatedByRoleId = "R1", UpdatedAt = Past
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = woId, ProductionBatchId = batch, ProductCode = "P", RoutingId = "R", Quantity = minutes,
            Status = WorkOrderStatus.Released, PlantId = PlantId, PlannedStartDate = Today,
            PlannedEndDate = due ?? Today.AddDays(30), UpdatedAt = Past
        });
        var d = new Dispatch
        {
            DispatchId = woId + "-10", WorkOrderId = woId, OperationSeq = 10, Quantity = minutes,
            Status = DispatchStatus.Pending, PlantId = PlantId, UpdatedAt = Past, CreatedAt = Past
        };
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = woId, OperationSeq = 10, OperationName = "OP", WorkCenterId = "WC1",
            TimeUnit = OperationTimeUnit.Minute, UnitTime = 1, BatchQty = 1, Dispatches = { d }
        });
        _context.Dispatches.Add(d);
        _context.SaveChanges();
        return d;
    }

    private void SetPriority(string woId, int priority)
    {
        var b = _context.ProductionBatches.IgnoreQueryFilters().Single(b => b.BatchId == "B-" + woId);
        b.Priority = priority;
        b.UpdatedAt = DateTime.UtcNow.AddSeconds(1);   // 插單是異動：排程因此過時（派工前自動重排才會跑）
        _context.SaveChanges();
    }

    private Dispatch D(string woId) => _context.Dispatches.Single(d => d.WorkOrderId == woId);

    private ScheduleRunLog LastLog() =>
        _context.ScheduleRunLogs.OrderByDescending(l => l.RunLogId).First();

    private List<ScheduleRunChange> ChangesOf(ScheduleRunLog log) =>
        _context.ScheduleRunChanges.Where(c => c.RunLogId == log.RunLogId).ToList();

    // ── 排程紀錄 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualRun_WritesLog_WithNewlyScheduledChanges()
    {
        AddWo("A", 60, 1);
        AddWo("B", 60, 2);

        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today, runBy: "u1");

        var log = LastLog();
        Assert.Equal(nameof(ScheduleTrigger.Manual), log.Trigger);
        Assert.Equal("u1", log.RunBy);
        Assert.Equal(nameof(ScheduleRule.Priority), log.Rule);
        Assert.Null(log.FreezeHours);
        Assert.Equal(2, log.ScheduledCount);
        Assert.Equal(2, log.NewlyScheduledCount);
        var changes = ChangesOf(log);
        Assert.All(changes, c => Assert.Equal(ScheduleChangeKind.NewlyScheduled, c.Kind));
        var a = changes.Single(c => c.WorkOrderId == "A");
        Assert.Null(a.OldStart);
        Assert.Equal("M1", a.NewMachineCode);
        Assert.Equal(Today, a.NewStart);
    }

    [Fact]
    public async Task RerunWithoutChanges_WritesLog_ButNoChangeRows()
    {
        AddWo("A", 60, 1);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        Assert.Equal(2, _context.ScheduleRunLogs.Count());
        var log = LastLog();
        Assert.Equal(1, log.ScheduledCount);
        Assert.Equal(0, log.NewlyScheduledCount + log.MovedCount + log.MachineChangedCount + log.UnscheduledCount);
        Assert.Empty(ChangesOf(log));
    }

    [Fact]
    public async Task PriorityBump_RecordsMoved_AndCountsTomorrow()
    {
        // A 1000 分（08:00 → 隔天 00:40），B 60 分排在 A 之後＝明天 00:40
        AddWo("A", 1000, 1);
        AddWo("B", 60, 2);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        Assert.Equal(Today.AddMinutes(1000), D("B").ScheduledStart);

        // 插單：B 調到最優先 → B 08:00、A 09:00
        SetPriority("B", 0);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var log = LastLog();
        Assert.Equal(2, log.MovedCount);
        // 只有 B 原本落在明天（A 前後都是今天開工）
        Assert.Equal(1, log.TomorrowChangedCount);
        var b = ChangesOf(log).Single(c => c.WorkOrderId == "B");
        Assert.Equal(ScheduleChangeKind.Moved, b.Kind);
        Assert.Equal(Today.AddMinutes(1000), b.OldStart);
        Assert.Equal(Today, b.NewStart);
        Assert.Equal("M1", b.OldMachineCode);
    }

    [Fact]
    public async Task MachineDown_RecordsMachineChanged_OrUnscheduled()
    {
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M2", MachineStatus = MachineStatus.Available });
        _context.SaveChanges();
        AddWo("A", 60, 1);
        AddWo("B", 60, 2);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var onM1 = new[] { D("A"), D("B") }.Single(d => d.MachineCode == "M1").WorkOrderId;

        // M1 故障：M1 上那張改到 M2
        _context.WorkCenterMachines.Single(m => m.MachineCode == "M1").MachineStatus = MachineStatus.BreakDown;
        _context.SaveChanges();
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var moved = ChangesOf(LastLog()).Single(c => c.WorkOrderId == onM1);
        Assert.Equal(ScheduleChangeKind.MachineChanged, moved.Kind);
        Assert.Equal("M1", moved.OldMachineCode);
        Assert.Equal("M2", moved.NewMachineCode);

        // M2 也故障：沒有機台可排 → 兩張都退回待排
        _context.WorkCenterMachines.Single(m => m.MachineCode == "M2").MachineStatus = MachineStatus.BreakDown;
        _context.SaveChanges();
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var log = LastLog();
        Assert.Equal(2, log.UnscheduledCount);
        Assert.All(ChangesOf(log), c =>
        {
            Assert.Equal(ScheduleChangeKind.Unscheduled, c.Kind);
            Assert.Null(c.NewStart);
            Assert.Equal("M2", c.OldMachineCode);
        });
    }

    [Fact]
    public async Task PreserveAssignmentMode_DoesNotWriteLog()
    {
        AddWo("A", 60, 1);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today.AddHours(1),
            mode: ScheduleRunMode.PreserveAssignment);

        Assert.Single(_context.ScheduleRunLogs);
    }

    [Fact]
    public async Task OldLogs_ArePrunedAfterRetention()
    {
        _context.ScheduleRunLogs.Add(new ScheduleRunLog
        {
            PlantId = PlantId, StartedAt = DateTime.UtcNow.AddDays(-ScheduleRunLog.RetentionDays - 1),
            Trigger = "Manual", Rule = "Priority",
            Changes = { new ScheduleRunChange { DispatchId = "X", WorkOrderId = "X", Kind = ScheduleChangeKind.Moved } }
        });
        // 別廠的舊紀錄不歸本廠清
        _context.ScheduleRunLogs.Add(new ScheduleRunLog
        {
            PlantId = "P999", StartedAt = DateTime.UtcNow.AddDays(-ScheduleRunLog.RetentionDays - 1),
            Trigger = "Manual", Rule = "Priority"
        });
        _context.SaveChanges();

        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        Assert.Single(_context.ScheduleRunLogs.Where(l => l.PlantId == PlantId));
        Assert.Single(_context.ScheduleRunLogs.Where(l => l.PlantId == "P999"));
        Assert.DoesNotContain(_context.ScheduleRunChanges, c => c.DispatchId == "X");
    }

    // ── 凍結時界 ─────────────────────────────────────────────────────────────

    /// <summary>A 08–09、B 09–10、C 10–11，之後把 C 調到最優先。</summary>
    private async Task SeedThreeAndBumpC()
    {
        AddWo("A", 60, 1);
        AddWo("B", 60, 2);
        AddWo("C", 60, 3);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        Assert.Equal(Today.AddHours(2), D("C").ScheduledStart);
        SetPriority("C", 0);
    }

    [Fact]
    public async Task FreezeWindow_KeepsDispatchStartingInsideWindow()
    {
        await SeedThreeAndBumpC();

        // 時界 30 分（到 08:30）：只有 08:00 開工的 A 在時界內
        var result = await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today,
            trigger: ScheduleTrigger.OnChange, freezeWindow: TimeSpan.FromMinutes(30));

        Assert.Equal(1, result.Fenced);
        Assert.Equal(Today, D("A").ScheduledStart);             // 不動
        Assert.Equal(Today.AddHours(1), D("C").ScheduledStart); // 插單接在 A 之後
        Assert.Equal(Today.AddHours(2), D("B").ScheduledStart);

        var log = LastLog();
        Assert.Equal(1, log.FencedCount);
        Assert.Equal(0.5m, log.FreezeHours);
        Assert.DoesNotContain(ChangesOf(log), c => c.WorkOrderId == "A");
    }

    [Fact]
    public async Task NoFreezeWindow_InsertedOrderTakesTheSlot()
    {
        await SeedThreeAndBumpC();

        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);   // 手動：不傳時界

        Assert.Equal(Today, D("C").ScheduledStart);
        Assert.Equal(Today.AddHours(1), D("A").ScheduledStart);
        Assert.Equal(0, LastLog().FencedCount);
    }

    [Fact]
    public async Task FreezeWindow_DoesNotKeepDispatchOnUnavailableMachine()
    {
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M2", MachineStatus = MachineStatus.Available });
        _context.SaveChanges();
        AddWo("A", 60, 1);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var machine = D("A").MachineCode!;
        _context.WorkCenterMachines.Single(m => m.MachineCode == machine).MachineStatus = MachineStatus.BreakDown;
        _context.SaveChanges();

        var result = await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today,
            trigger: ScheduleTrigger.OnChange, freezeWindow: TimeSpan.FromHours(24));

        Assert.Equal(0, result.Fenced);
        Assert.NotEqual(machine, D("A").MachineCode);
    }

    [Fact]
    public async Task FreezeWindow_DoesNotKeepOverdueDispatch()
    {
        AddWo("A", 60, 1);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);   // 08–09

        // 10:00 再排：A 的時段已整段過去卻沒開工，留著只會讓甘特顯示一段過去的計畫
        var later = Today.AddHours(2);
        var result = await Engine().RunAsync(PlantId, ScheduleRule.Priority, later,
            trigger: ScheduleTrigger.OnChange, freezeWindow: TimeSpan.FromHours(24));

        Assert.Equal(0, result.Fenced);
        Assert.Equal(later, D("A").ScheduledStart);
    }

    // ── 自動重排帶入廠別設定 ─────────────────────────────────────────────────

    [Fact]
    public async Task AutoRun_UsesConfiguredRuleAndFreezeWindow()
    {
        await SeedThreeAndBumpC();
        _values[ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch] = "true";
        _values[ProjectZSystemParamKeys.MfgAutoScheduleRule] = "edd";
        _values[ProjectZSystemParamKeys.MfgAutoScheduleFreezeHours] = "0.5";

        Assert.True(await Service().EnsureFreshBeforeDispatchAsync(PlantId));

        var log = LastLog();
        Assert.Equal(nameof(ScheduleTrigger.BeforeDispatch), log.Trigger);
        Assert.Null(log.RunBy);
        Assert.Equal(nameof(ScheduleRule.EDD), log.Rule);
        Assert.Equal(0.5m, log.FreezeHours);
        Assert.Equal(1, log.FencedCount);
        Assert.Equal(Today, D("A").ScheduledStart);
    }

    [Fact]
    public async Task AutoRun_RuleNotSet_FallsBackToPriority()
    {
        await SeedThreeAndBumpC();
        _values[ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch] = "true";

        await Service().EnsureFreshBeforeDispatchAsync(PlantId);

        var log = LastLog();
        Assert.Equal(nameof(ScheduleRule.Priority), log.Rule);
        Assert.Null(log.FreezeHours);
        Assert.Equal(Today, D("C").ScheduledStart);   // 沒有時界：插單直接搶到 08:00
    }

    // ── 設定解析 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("EDD", ScheduleRule.EDD)]
    [InlineData(" spt ", ScheduleRule.SPT)]
    [InlineData("", ScheduleRule.Priority)]
    [InlineData(null, ScheduleRule.Priority)]
    [InlineData("Bogus", ScheduleRule.Priority)]
    [InlineData("9", ScheduleRule.Priority)]
    public void ParseRule(string? raw, ScheduleRule expected) =>
        Assert.Equal(expected, AutoScheduleSettings.ParseRule(raw));

    [Theory]
    [InlineData("4", "4")]
    [InlineData("1.5", "1.5")]
    [InlineData("200", "168")]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeFreezeHours(string? raw, string? expected) =>
        Assert.Equal(expected, AutoScheduleSettings.NormalizeFreezeHours(raw));

    [Theory]
    [InlineData("lifo", "LIFO")]
    [InlineData("", null)]
    [InlineData("Bogus", null)]
    public void NormalizeRule(string? raw, string? expected) =>
        Assert.Equal(expected, AutoScheduleSettings.NormalizeRule(raw));
}
