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
/// 自動重排排程（2026-10-06 第 1 期）：
///   - 排程是否過時由資料推導（ScheduleFreshness），排程引擎自己的寫入不得讓它又變過時
///   - 三種時機的開關、每日定時的當天補跑與不重跑、異動後的安靜期
///   - 17:00 準備提醒之後改到明天的派工要推播
/// 排程引擎用真的（InMemory），只替換廠別參數與推播。
/// </summary>
public class AutoScheduleTests : TestBase
{
    private const string PlantId = "P001";
    // 本機時刻與 UTC 時刻在這裡取同值：引擎與畫面把時刻數字當廠別當地時間（Kind=Utc 只是標籤）
    private static readonly DateTime Today = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>早於任何一次排程的真實時刻——引擎以真實時鐘記錄讀取起點，種子資料的異動時間必須在它之前。</summary>
    private static readonly DateTime Past = DateTime.UtcNow.AddHours(-1);

    private readonly Mock<IPlantParamService> _params = new();
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly Dictionary<string, string?> _values = new();

    public AutoScheduleTests()
    {
        _context.CurrentPlantId = PlantId;
        _params.Setup(p => p.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((string k, string? _, CancellationToken _) => _values.GetValueOrDefault(k));

        _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId });
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = "WC1", MachineCode = "M1", MachineStatus = MachineStatus.Available });
        for (int i = -1; i < 70; i++)
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, Date = DateOnly.FromDateTime(Today).AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440
            });
        _context.SaveChanges();
    }

    private SchedulingEngine Engine() => new(_context, _params.Object);

    private AutoScheduleService Service(IDateTimeService? clock = null)
    {
        var sys = new Mock<ISystemParamService>();
        var dt = clock ?? Clock(Today);
        return new AutoScheduleService(_context, Engine(), _params.Object, sys.Object, _push.Object, dt,
            NullLogger<AutoScheduleService>.Instance);
    }

    private static IDateTimeService Clock(DateTime now)
    {
        var m = new Mock<IDateTimeService>();
        m.Setup(d => d.Now).Returns(now);
        return m.Object;
    }

    private Dispatch AddPending(string woId, DateTime updatedAt, DispatchStatus status = DispatchStatus.Pending)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = woId, ProductionBatchId = "B-" + woId, ProductCode = "P", RoutingId = "R", Quantity = 60,
            Status = WorkOrderStatus.Released, PlantId = PlantId, PlannedStartDate = Today, PlannedEndDate = Today.AddDays(5),
            UpdatedAt = updatedAt
        });
        var d = new Dispatch
        {
            DispatchId = woId + "-10", WorkOrderId = woId, OperationSeq = 10, Quantity = 60, Status = status,
            PlantId = PlantId, UpdatedAt = updatedAt, CreatedAt = updatedAt
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

    private Task<ScheduleFreshnessInfo> Fresh() => ScheduleFreshness.GetAsync(_context, PlantId);

    // ── 過時判定 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Freshness_NeverRun_WithPending_IsStale()
    {
        AddPending("WO1", Past);
        Assert.True((await Fresh()).IsStale);
    }

    [Fact]
    public async Task Freshness_AfterRun_IsUpToDate_EngineWritesDoNotCount()
    {
        AddPending("WO1", Past);
        // 生產中派工：引擎每次重估它的計畫時段並更新異動時間——這不能算成「新異動」
        var running = AddPending("WO0", Past, DispatchStatus.InProgress);
        running.MachineCode = "M1"; running.ScheduledStart = Today; running.ScheduledEnd = Today.AddHours(2);
        _context.SaveChanges();
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var f = await Fresh();
        Assert.False(f.IsStale);
        Assert.Equal(nameof(ScheduleTrigger.Manual), f.LastTrigger);

        // 連跑第二次也不能讓自己變過時（否則「異動後自動」會無限重排）
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        Assert.False((await Fresh()).IsStale);
    }

    [Fact]
    public async Task Freshness_NewPendingAfterRun_IsStale_WithChangeTime()
    {
        AddPending("WO1", Past);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var at = DateTime.UtcNow.AddSeconds(5);
        AddPending("WO2", at);

        var f = await Fresh();
        Assert.True(f.IsStale);
        Assert.Equal(at, f.FirstChangeAt);
        Assert.Equal(at, f.LastChangeAt);
    }

    [Fact]
    public async Task Freshness_MachineStatusOrderAfterRun_IsStale()
    {
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        _context.MachineStatusOrders.Add(new MachineStatusOrder
        {
            OrderId = "MSO1", PlantId = PlantId, EventType = MachineEventType.DI,
            EventAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddSeconds(5), UpdatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();

        Assert.True((await Fresh()).IsStale);
    }

    [Fact]
    public async Task Freshness_CalendarChangedAfterRun_IsStale_WithoutChangeTime()
    {
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var seg = _context.WorkCalendarSegments.First(s => s.Date == DateOnly.FromDateTime(Today).AddDays(3));
        seg.EndMinute = 600;   // 縮短某天的產能——日曆沒有異動時間欄位，靠指紋判斷
        _context.SaveChanges();

        var f = await Fresh();
        Assert.True(f.IsStale);
        Assert.True(f.CalendarChanged);
        Assert.Null(f.LastChangeAt);
    }

    [Fact]
    public async Task Freshness_PlannedDowntimeDeletedAfterRun_IsStale()
    {
        _context.PlannedDowntimes.Add(new PlannedDowntime
        {
            PlantId = PlantId, MachineCode = "M1", StartTime = Today.AddDays(2), EndTime = Today.AddDays(2).AddHours(4), IsActive = true
        });
        _context.SaveChanges();
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        _context.PlannedDowntimes.RemoveRange(_context.PlannedDowntimes);
        _context.SaveChanges();

        Assert.True((await Fresh()).CalendarChanged);
    }

    // ── 設定解析與安靜期 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("16:00", "16:00")]
    [InlineData(" 7:05 ", "07:05")]
    [InlineData("25:00", null)]
    [InlineData("1600", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeDailyTime(string? raw, string? expected) =>
        Assert.Equal(expected, AutoScheduleSettings.NormalizeDailyTime(raw));

    [Fact]
    public void QuietPeriod_WaitsForQuiet_ButNotForever()
    {
        var now = Today.AddHours(2);
        ScheduleFreshnessInfo F(DateTime? first, DateTime? last) => new(null, null, true, first, last, false, 0, 0);

        Assert.False(AutoScheduleService.IsQuietEnough(F(now.AddSeconds(-60), now.AddSeconds(-30)), now));  // 剛有異動
        Assert.True(AutoScheduleService.IsQuietEnough(F(now.AddMinutes(-3), now.AddMinutes(-2)), now));       // 安靜 2 分
        Assert.True(AutoScheduleService.IsQuietEnough(F(now.AddMinutes(-11), now.AddSeconds(-5)), now));      // 持續異動已等 11 分
        Assert.True(AutoScheduleService.IsQuietEnough(F(null, null), now));                                     // 只有日曆變
    }

    // ── 背景輪詢 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tick_AllOff_DoesNothing()
    {
        AddPending("WO1", Past);

        Assert.Null(await Service().TickAsync(PlantId, Today, Today.AddHours(1)));
        Assert.False(_context.ScheduleRunStates.Any());
    }

    [Fact]
    public async Task Tick_Daily_RunsOncePerDay_AndCatchesUpAfterMissedTime()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleDailyTime] = "16:00";
        AddPending("WO1", Past);
        var svc = Service();

        Assert.Null(await svc.TickAsync(PlantId, Today.Date.AddHours(15).AddMinutes(59), DateTime.UtcNow));   // 還沒到
        Assert.Equal(ScheduleTrigger.Daily, await svc.TickAsync(PlantId, Today.Date.AddHours(21), DateTime.UtcNow)); // 錯過 16:00，當天補跑
        Assert.Null(await svc.TickAsync(PlantId, Today.Date.AddHours(22), DateTime.UtcNow));                    // 同一天不重跑

        var state = _context.ScheduleRunStates.Single();
        Assert.Equal(DateOnly.FromDateTime(Today), state.LastDailyRunDate);
        Assert.Equal(nameof(ScheduleTrigger.Daily), state.LastTrigger);
    }

    [Fact]
    public async Task Tick_OnChange_WaitsForQuietPeriod()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleOnChange] = "true";
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);
        var changedAt = DateTime.UtcNow;
        AddPending("WO1", changedAt);
        var svc = Service();

        Assert.Null(await svc.TickAsync(PlantId, Today, changedAt.AddSeconds(30)));
        Assert.Equal(ScheduleTrigger.OnChange, await svc.TickAsync(PlantId, Today, changedAt.AddMinutes(2)));
        Assert.Equal(DispatchStatus.Scheduled, _context.Dispatches.Single(d => d.WorkOrderId == "WO1").Status);
        Assert.False((await Fresh()).IsStale);
    }

    // ── 派工前 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeforeDispatch_Off_DoesNotRun()
    {
        AddPending("WO1", Past);

        Assert.False(await Service().EnsureFreshBeforeDispatchAsync(PlantId));
        Assert.Equal(DispatchStatus.Pending, _context.Dispatches.Single().Status);
    }

    [Fact]
    public async Task BeforeDispatch_OnAndStale_RunsFirst()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch] = "true";
        AddPending("WO1", Past);

        Assert.True(await Service().EnsureFreshBeforeDispatchAsync(PlantId));
        Assert.Equal(DispatchStatus.Scheduled, _context.Dispatches.Single().Status);
        Assert.Equal(nameof(ScheduleTrigger.BeforeDispatch), _context.ScheduleRunStates.Single().LastTrigger);
    }

    [Fact]
    public async Task BeforeDispatch_OnButFresh_DoesNotRunAgain()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleBeforeDispatch] = "true";
        AddPending("WO1", Past);
        await Engine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        Assert.False(await Service().EnsureFreshBeforeDispatchAsync(PlantId));
        Assert.Equal(nameof(ScheduleTrigger.Manual), _context.ScheduleRunStates.Single().LastTrigger);
    }

    // ── 17:00 之後改到明天的派工要推播 ─────────────────────────────────────────

    [Fact]
    public async Task AutoRunAfterAlertHour_MovingTomorrowsDispatch_Pushes()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleOnChange] = "true";
        var evening = Today.Date.AddHours(18);
        var tomorrow = Today.Date.AddDays(1).AddHours(9);
        // 明天 09:00 已排一張；全天日曆下引擎會把它提前到今晚 → 明天少一張＝有異動
        var d = AddPending("WO1", DateTime.UtcNow.AddSeconds(-300), DispatchStatus.Scheduled);
        d.MachineCode = "M1"; d.ScheduledStart = tomorrow; d.ScheduledEnd = tomorrow.AddHours(1);
        AddPending("WO2", DateTime.UtcNow.AddSeconds(-300));    // 讓排程過時
        _context.SaveChanges();

        await Service(Clock(evening)).TickAsync(PlantId, evening, DateTime.UtcNow);

        _push.Verify(p => p.SendToRoleAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), "/PreparationBoard"), Times.Once);
    }

    [Fact]
    public async Task AutoRunBeforeAlertHour_DoesNotPush()
    {
        _values[ProjectZSystemParamKeys.MfgAutoScheduleOnChange] = "true";
        var tomorrow = Today.Date.AddDays(1).AddHours(9);
        var d = AddPending("WO1", DateTime.UtcNow.AddSeconds(-300), DispatchStatus.Scheduled);
        d.MachineCode = "M1"; d.ScheduledStart = tomorrow; d.ScheduledEnd = tomorrow.AddHours(1);
        AddPending("WO2", DateTime.UtcNow.AddSeconds(-300));
        _context.SaveChanges();

        // 早上跑：17:00 的準備提醒還沒送，提醒本身就會反映最新排程
        await Service().TickAsync(PlantId, Today, DateTime.UtcNow);

        _push.Verify(p => p.SendToRoleAsync(It.IsAny<string>(), It.IsAny<Func<PushMessage>>(), It.IsAny<string?>()), Times.Never);
    }
}
