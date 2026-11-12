using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;
using Seg = SmartFactory.MES.Modules.MFG.Services.WorkCalendarSegmentBuilder.Segment;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 跨當地午夜的派工時段（manual-test-plan.md TZ-6：「派工時段不得被裁切或重疊」）。
///
/// <para><b>驗收語句原本的前提是錯的。</b>規格寫「可用時段以 UTC 午夜切日」，
/// 實際不是：MFG 排程鏈的 DateTime 數字本身就是**廠別當地時間**，<c>Kind=Utc</c>
/// 只是貼給 Npgsql 的標籤（mfg-domain.md §UTC 地雷）。所以日界一直都是當地午夜，
/// 寫規格的人是看到 <c>DateTimeKind.Utc</c> 才推論成 UTC 日界。</para>
///
/// <para>但「切日」這件事本身仍是真實風險，只是風險在**接縫**而不在時區：
/// 可用時段是逐日儲存的分鐘區間（0..1440），跨午夜的班別被
/// <see cref="WorkCalendarSegmentBuilder"/> 以 1440 切成兩半（今日尾段 + 隔日 carry），
/// 再由 <see cref="WorkingTimeWalker"/> 逐日走訪。這兩者對午夜的認知只要差一分鐘，
/// 派工時段就會**被裁切**（夜班做到一半被丟到隔天白天）或**重疊**
/// （carry 被重複計一次，同一機台的下一張派工壓在上一張身上）。</para>
///
/// <para>因此這裡刻意**不手寫時段常數**，而是讓班別經 <c>BuildDay</c> 產生日曆、
/// 再交給派工配置走完整條鏈——兩層之中任一層對午夜改了看法，這裡就會紅。
/// 既有的 <c>WorkCalendarSegmentBuilderTests</c> 與 <c>SchedulingEngineTests</c>
/// 各自只驗一層，接縫正是兩份測試都看不到的地方。</para>
/// </summary>
public class SchedulingCrossMidnightTests
{
    // ── 日曆：夜班 22:00–06:00，連續多日 ─────────────────────────────────────

    private static readonly DateOnly Day1 = new(2026, 6, 1);

    /// <summary>夜班 22:00–06:00（無午休）。</summary>
    private static Shift NightShift() => new()
    {
        ShiftCode = "N",
        ShiftName = "夜班",
        StartTime = new TimeOnly(22, 0),
        EndTime   = new TimeOnly(6, 0),
    };

    /// <summary>
    /// 自 <see cref="Day1"/> 起連續 <paramref name="days"/> 天排夜班，
    /// 把每日的 carryOut 接成隔日的 carryIn——這就是正式環境產生
    /// <c>WorkCalendarSegment</c> 的方式，不是測試自己編的時段。
    /// </summary>
    private static Dictionary<DateOnly, List<Seg>> NightShiftCalendar(int days = 6)
    {
        var result = new Dictionary<DateOnly, List<Seg>>();
        IReadOnlyList<Seg> carryIn = new List<Seg>();

        for (int i = 0; i < days; i++)
        {
            var today = WorkCalendarSegmentBuilder.BuildDay(
                new[] { NightShift() }, carryIn, out var carryOut);
            result[Day1.AddDays(i)] = today;
            carryIn = carryOut;
        }
        return result;
    }

    private static CalendarContext MakeCtx(IDictionary<DateOnly, List<Seg>> daySegments)
        => new(
            daySegments.ToDictionary(
                kv => ((string?)null, (string?)null, kv.Key),
                kv => (IReadOnlyList<Seg>)kv.Value),
            Array.Empty<PlannedDowntime>());

    private static Func<DateOnly, IReadOnlyList<(int Start, int End)>> SegsOf(
        IDictionary<DateOnly, List<Seg>> cal)
        => d => cal.TryGetValue(d, out var s)
            ? s.Select(x => (x.Start, x.End)).ToList()
            : new List<(int, int)>();

    // ── 派工素材 ─────────────────────────────────────────────────────────────

    /// <summary>單件工時 1 分鐘、無準備工時 → 需求工時 ＝ 數量，讀起來就是分鐘數。</summary>
    private static WorkOrderOperation MakeOp() => new()
    {
        WorkOrderId  = "WO001",
        OperationSeq = 1,
        WorkCenterId = "WC01",
        TimeUnit     = OperationTimeUnit.Minute,
        SetupTime    = 0,
        UnitTime     = 1,
        BatchMode    = OperationMode.WholeOrder,
        BatchQty     = 1,
    };

    private static WorkOrder MakeWo() => new()
    {
        WorkOrderId    = "WO001",
        ProductCode    = "P001",
        RoutingId      = "R001",
        Quantity       = 1000,
        Status         = WorkOrderStatus.Released,
        PlannedEndDate = new DateTime(2026, 6, 30),
        CreatedAt      = new DateTime(2026, 6, 1),
    };

    private static Dispatch MakeDispatch(string id, decimal minutes) => new()
    {
        DispatchId   = id,
        WorkOrderId  = "WO001",
        OperationSeq = 1,
        Quantity     = minutes,
        Status       = DispatchStatus.Pending,
    };

    private static readonly IReadOnlyList<WorkCenterMachine> OneMachine = new List<WorkCenterMachine>
    {
        new() { WorkCenterCode = "WC01", MachineCode = "M01", MachineName = "M01",
                MachineStatus = MachineStatus.Available },
    };

    private static DispatchSlot Allocate(
        string id, decimal minutes, CalendarContext ctx,
        Dictionary<string, DateTime> machineAvail, DateTime today)
    {
        var slot = SchedulingEngine.AllocateDispatchSlot(
            MakeDispatch(id, minutes), MakeOp(), MakeWo(),
            prevEnd: today, OneMachine, ctx, machineAvail, today);
        Assert.NotNull(slot);
        return slot!;
    }

    /// <summary>
    /// 一段絕對時間區間裡，日曆真正給了多少可用分鐘。
    /// 用來檢查「派工時段沒有被裁切」——若中途穿過沒有產能的空白，這個數字會小於需求工時。
    /// </summary>
    private static double CapacityWithin(
        IDictionary<DateOnly, List<Seg>> cal, DateTime from, DateTime to)
    {
        double total = 0;
        for (var d = DateOnly.FromDateTime(from); d <= DateOnly.FromDateTime(to); d = d.AddDays(1))
        {
            if (!cal.TryGetValue(d, out var segs)) continue;
            var midnight = d.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc);
            foreach (var seg in segs)
            {
                var s = midnight.AddMinutes(seg.Start);
                var e = midnight.AddMinutes(seg.End);
                if (s < from) s = from;
                if (e > to)   e = to;
                if (e > s) total += (e - s).TotalMinutes;
            }
        }
        return total;
    }

    // ── 接縫本身：午夜不是「非上班時段」 ─────────────────────────────────────

    /// <summary>
    /// 夜班的午夜兩側必須接得起來：00:00 本身要是可用時刻，
    /// 而不是「今天做到 24:00、明天從 00:00 重新找一次」中間掉一分鐘。
    /// </summary>
    [Fact]
    public void LocalMidnight_IsInsideTheNightShift_NotOffShift()
    {
        var cal = NightShiftCalendar();
        var midnight = Day1.AddDays(1).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc);

        var snapped = WorkingTimeWalker.SnapToAvailable(midnight, SegsOf(cal));

        Assert.Equal(midnight, snapped);
    }

    /// <summary>
    /// 但白天的空檔要是真的空檔——否則上一支只是「什麼時刻都可用」的假綠。
    /// 06:00 下班後的下一個可用時刻是當晚 22:00。
    /// </summary>
    [Fact]
    public void AfterTheNightShiftEnds_TheDaytimeGapIsReal()
    {
        var cal = NightShiftCalendar();
        var d2 = Day1.AddDays(1);
        var morning = d2.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc);

        var snapped = WorkingTimeWalker.SnapToAvailable(morning, SegsOf(cal));

        Assert.Equal(d2.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc), snapped);
    }

    /// <summary>
    /// 連續穿過兩個午夜，一分鐘都不能掉也不能多算：
    /// 兩個整夜 ＝ 2×480 分鐘，自第一晚 22:00 起算應落在第三天 06:00。
    /// carry 若漏傳，會少掉每晚的後半段而落得更晚；carry 若被重複計，會落得更早。
    /// </summary>
    [Fact]
    public void TwoWholeNights_LandExactlyOnTheSecondMorning()
    {
        var cal = NightShiftCalendar();
        var start = Day1.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc);

        var end = WorkingTimeWalker.AddWorkingMinutes(start, 960, SegsOf(cal));

        Assert.Equal(Day1.AddDays(2).ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc), end);
    }

    // ── 派工層：不得被裁切 ───────────────────────────────────────────────────

    /// <summary>
    /// TZ-6 前半：跨午夜的派工時段不得被裁切。
    /// 22:00 起做 240 分鐘，應該一路做到隔日 02:00——整段都在夜班內，
    /// 因此牆鐘長度恰好等於需求工時，中間沒有被午夜插入任何空白。
    /// </summary>
    [Fact]
    public void DispatchSpanningLocalMidnight_IsNotCutAtTheDayBoundary()
    {
        var cal = NightShiftCalendar();
        var ctx = MakeCtx(cal);
        var today = Day1.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);   // 上班前

        var slot = Allocate("D1", 240, ctx, new Dictionary<string, DateTime>(), today);

        Assert.Equal(Day1.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc), slot.Start);
        Assert.Equal(Day1.AddDays(1).ToDateTime(new TimeOnly(2, 0), DateTimeKind.Utc), slot.End);
        // 真的跨過了當地午夜（否則本測試等於什麼都沒驗）
        Assert.NotEqual(slot.Start.Date, slot.End.Date);
        // 沒有被裁切：牆鐘長度 ＝ 需求工時
        Assert.Equal(240, (slot.End - slot.Start).TotalMinutes);
    }

    /// <summary>
    /// 做不完一晚的派工要接到**下一晚**，不得被塞進白天沒有產能的時段。
    /// 600 分鐘：第一晚 22:00–06:00 給 480，剩 120 分鐘落在第二晚 22:00–24:00。
    /// 這裡用「區間內的實際產能」斷言，而不是只看起訖——起訖對了但中間穿過空白，
    /// 正是「被裁切」的另一種樣子。
    /// </summary>
    [Fact]
    public void DispatchLongerThanOneNight_ContinuesOnTheNextNight_NotInTheDaytimeGap()
    {
        var cal = NightShiftCalendar();
        var ctx = MakeCtx(cal);
        var today = Day1.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc);

        var slot = Allocate("D1", 600, ctx, new Dictionary<string, DateTime>(), today);

        Assert.Equal(Day1.AddDays(2).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc), slot.End);
        Assert.Equal(600, CapacityWithin(cal, slot.Start, slot.End));
    }

    // ── 派工層：不得重疊 ─────────────────────────────────────────────────────

    /// <summary>
    /// TZ-6 後半：同一機台的連續派工不得重疊，跨午夜也一樣。
    /// 三張各 240 分鐘：D1 = 第一晚 22:00–02:00（跨午夜）、
    /// D2 = 02:00–06:00（接在後面把第一晚用完）、D3 = 第二晚 22:00–02:00（又跨一次午夜）。
    /// 機台可用時刻若在午夜接縫上被重設，D2 會被排回 D1 之中。
    /// </summary>
    [Fact]
    public void ConsecutiveDispatchesOnOneMachine_NeverOverlap_AcrossMidnight()
    {
        var cal = NightShiftCalendar();
        var ctx = MakeCtx(cal);
        var today = Day1.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);
        var machineAvail = new Dictionary<string, DateTime>();

        var a = Allocate("D1", 240, ctx, machineAvail, today);
        var b = Allocate("D2", 240, ctx, machineAvail, today);
        var c = Allocate("D3", 240, ctx, machineAvail, today);

        Assert.Equal("M01", a.MachineCode);
        Assert.Equal(a.MachineCode, b.MachineCode);
        Assert.Equal(b.MachineCode, c.MachineCode);

        Assert.True(b.Start >= a.End, $"D2 起點 {b.Start:o} 壓在 D1 終點 {a.End:o} 之前");
        Assert.True(c.Start >= b.End, $"D3 起點 {c.Start:o} 壓在 D2 終點 {b.End:o} 之前");

        // 兩張跨午夜、一張貼著午夜之後開工——三張都真的碰到接縫
        Assert.Equal(Day1.AddDays(1).ToDateTime(new TimeOnly(2, 0), DateTimeKind.Utc), a.End);
        Assert.Equal(Day1.AddDays(1).ToDateTime(new TimeOnly(2, 0), DateTimeKind.Utc), b.Start);
        Assert.Equal(Day1.AddDays(1).ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc), b.End);
        Assert.Equal(Day1.AddDays(1).ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc), c.Start);
        Assert.Equal(Day1.AddDays(2).ToDateTime(new TimeOnly(2, 0), DateTimeKind.Utc), c.End);
    }

    /// <summary>
    /// 三張派工合計 720 分鐘，占用的機台時間裡實際產能也必須是 720 分鐘——
    /// 少了代表有人被裁切，多了代表有人重疊（同一段產能被算了兩次）。
    /// </summary>
    [Fact]
    public void TheThreeDispatches_ConsumeExactlyTheCapacityTheyNeed()
    {
        var cal = NightShiftCalendar();
        var ctx = MakeCtx(cal);
        var today = Day1.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);
        var machineAvail = new Dictionary<string, DateTime>();

        var slots = new[]
        {
            Allocate("D1", 240, ctx, machineAvail, today),
            Allocate("D2", 240, ctx, machineAvail, today),
            Allocate("D3", 240, ctx, machineAvail, today),
        };

        var each = slots.Select(s => CapacityWithin(cal, s.Start, s.End)).ToList();
        Assert.Equal(new List<double> { 240, 240, 240 }, each);

        // 三張合起來的涵蓋區間內，日曆給的產能不多於它們的總和（沒有被重複占用）
        var span = CapacityWithin(cal, slots.Min(s => s.Start), slots.Max(s => s.End));
        Assert.Equal(720, span);
    }
}
