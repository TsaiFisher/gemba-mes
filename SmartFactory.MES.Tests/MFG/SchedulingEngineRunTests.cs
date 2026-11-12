using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 排程引擎 RunAsync 正確性測試（scheduling-spec-review 第一階段 1 / mfg-domain.md §10-8、§24.7）：
///   - 連跑兩次不得雙重預約同一機台時段
///   - InProgress 家族凍結並佔用機台；其完成時刻接力給後續工序
///   - Scheduled 釋放重排
///   - §24.7：料況確認已上移至投產（硬閘），排程不再重查料況、不改寫 MaterialRisk
/// </summary>
public class SchedulingEngineRunTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateTime Today = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc); // 一 08:00

    private readonly Mock<IPlantParamService> _sysParam = new();

    public SchedulingEngineRunTests()
    {
        _context.CurrentPlantId = PlantId;
        _sysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.MfgStandbySchedulable, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync("false");
    }

    private SchedulingEngine MakeEngine()
        => new(_context, _sysParam.Object);

    // ── 測試資料 helpers ─────────────────────────────────────────────────────

    private void AddMachine(string machineCode, string wc = "WC1")
    {
        if (!_context.WorkCenters.Local.Any(w => w.WorkCenterCode == wc)
            && !_context.WorkCenters.Any(w => w.WorkCenterCode == wc))
            _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = wc, WorkCenterName = wc, PlantId = PlantId });

        _context.WorkCenterMachines.Add(new WorkCenterMachine
        {
            WorkCenterCode = wc, MachineCode = machineCode, MachineStatus = MachineStatus.Available
        });
    }

    /// <summary>整週（前後 30 天）全天可用的廠級日曆。</summary>
    private void AddFullDayCalendar()
    {
        for (int i = -1; i < 30; i++)
        {
            _context.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, MachineCode = null, WorkCenterId = null,
                Date = DateOnly.FromDateTime(Today).AddDays(i), Seq = 1,
                StartMinute = 0, EndMinute = 1440
            });
        }
    }

    /// <summary>含午休的班表日曆：08:00-12:00、13:00-17:00（淨 8 小時）。</summary>
    private void AddShiftCalendar()
    {
        for (int i = -1; i < 30; i++)
        {
            var date = DateOnly.FromDateTime(Today).AddDays(i);
            _context.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, MachineCode = null, WorkCenterId = null,
                Date = date, Seq = 1, StartMinute = 480, EndMinute = 720
            });
            _context.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, MachineCode = null, WorkCenterId = null,
                Date = date, Seq = 2, StartMinute = 780, EndMinute = 1020
            });
        }
    }

    /// <summary>斷言時刻落在某個可用時段內（起點含左界、終點含右界）。</summary>
    private static void AssertWithinCapacity(DateTime t, bool isEnd = false)
    {
        int min = t.Hour * 60 + t.Minute;
        bool ok = isEnd
            ? (min > 480 && min <= 720) || (min > 780 && min <= 1020)
            : (min >= 480 && min < 720) || (min >= 780 && min < 1020);
        Assert.True(ok, $"{t:yyyy-MM-dd HH:mm} 不在產能時段內（08:00-12:00 / 13:00-17:00）");
    }

    private (WorkOrder wo, Dispatch d) AddWo(
        string woId, string batchId,
        DispatchStatus status = DispatchStatus.Pending,
        decimal qty = 60, decimal unitMin = 1,
        DateTime? scheduledStart = null, DateTime? scheduledEnd = null,
        string? machineCode = null, int operationSeq = 10,
        DateTime? plannedStart = null, string wc = "WC1",
        decimal moveMin = 0, decimal queueMin = 0,
        OperationMode batchMode = OperationMode.WholeOrder, decimal batchQty = 1)
    {
        var wo = _context.WorkOrders.Local.FirstOrDefault(w => w.WorkOrderId == woId);
        if (wo == null)
        {
            wo = new WorkOrder
            {
                WorkOrderId = woId, ProductionBatchId = batchId, ProductCode = "P-1",
                RoutingId = "R1", Quantity = qty, Status = WorkOrderStatus.Released,
                PlannedStartDate = plannedStart ?? Today,
                PlannedEndDate = Today.AddDays(7), PlantId = PlantId
            };
            _context.WorkOrders.Add(wo);
        }

        var d = new Dispatch
        {
            DispatchId = $"{woId}-{operationSeq}", WorkOrderId = woId, OperationSeq = operationSeq,
            Quantity = qty, Status = status, PlantId = PlantId,
            MachineCode = machineCode,
            ScheduledStart = scheduledStart, ScheduledEnd = scheduledEnd
        };
        var op = new WorkOrderOperation
        {
            WorkOrderId = woId, OperationSeq = operationSeq, OperationName = $"OP{operationSeq}",
            WorkCenterId = wc, TimeUnit = OperationTimeUnit.Minute,
            SetupTime = 0, UnitTime = unitMin, BatchMode = batchMode, BatchQty = batchQty,
            MoveTime = moveMin, QueueTime = queueMin,
            Dispatches = { d }
        };
        wo.Operations.Add(op);
        _context.WorkOrderOperations.Add(op);
        _context.Dispatches.Add(d);
        return (wo, d);
    }

    private static void AssertNoOverlapOnSameMachine(IReadOnlyList<Dispatch> dispatches)
    {
        var byMachine = dispatches
            .Where(d => d.MachineCode != null && d.ScheduledStart.HasValue && d.ScheduledEnd.HasValue)
            .GroupBy(d => d.MachineCode);
        foreach (var g in byMachine)
        {
            var sorted = g.OrderBy(d => d.ScheduledStart).ToList();
            for (int i = 1; i < sorted.Count; i++)
                Assert.True(sorted[i].ScheduledStart >= sorted[i - 1].ScheduledEnd,
                    $"機台 {g.Key} 雙重預約：{sorted[i - 1].DispatchId}({sorted[i - 1].ScheduledStart:HH:mm}-{sorted[i - 1].ScheduledEnd:HH:mm}) vs {sorted[i].DispatchId}({sorted[i].ScheduledStart:HH:mm})");
        }
    }

    // ── 1. 連跑兩次不雙重預約 ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SecondRun_DoesNotDoubleBookMachine()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        AddWo("WO-A", "B-A");
        await _context.SaveChangesAsync();

        var engine = MakeEngine();
        await engine.RunAsync(PlantId, ScheduleRule.Priority, Today);

        // 第一次排完後新增第二張工單，再跑一次（舊版會把 WO-B 排進 WO-A 已佔時段）
        AddWo("WO-B", "B-B");
        await _context.SaveChangesAsync();
        await engine.RunAsync(PlantId, ScheduleRule.Priority, Today);

        var all = _context.Dispatches.ToList();
        Assert.All(all, d => Assert.Equal(DispatchStatus.Scheduled, d.Status));
        AssertNoOverlapOnSameMachine(all);
    }

    /// <summary>§18.2 投產前審核：送審中／被退回的工單不排；Draft（舊工單預設值）與 Approved 照排。</summary>
    [Fact]
    public async Task RunAsync_UnapprovedWorkOrders_NotScheduled()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        AddWo("WO-PEND", "B-1").wo.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
        AddWo("WO-REJ",  "B-2").wo.ApprovalStatus = DocumentApprovalStatus.Rejected;
        AddWo("WO-DRAFT", "B-3").wo.ApprovalStatus = DocumentApprovalStatus.Draft;
        AddWo("WO-OK",   "B-4").wo.ApprovalStatus = DocumentApprovalStatus.Approved;
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var status = _context.Dispatches.ToDictionary(d => d.WorkOrderId, d => d.Status);
        Assert.Equal(DispatchStatus.Pending,   status["WO-PEND"]);
        Assert.Equal(DispatchStatus.Pending,   status["WO-REJ"]);
        Assert.Equal(DispatchStatus.Scheduled, status["WO-DRAFT"]);
        Assert.Equal(DispatchStatus.Scheduled, status["WO-OK"]);
    }

    // ── 2. InProgress 凍結：佔用機台、不被移動 ──────────────────────────────

    [Fact]
    public async Task RunAsync_FrozenInProgress_OccupiesMachine_AndIsNotMoved()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 剩餘量重估（qty 240、unitMin 1、QtyDone 0 → 240 分鐘自 Today 起）恰等於帳面
        // ScheduledEnd 12:00 → 進度正常時凍結時段完全不動
        var frozenEnd = DateTime.SpecifyKind(Today.AddHours(4), DateTimeKind.Utc); // 12:00
        var frozenStart = DateTime.SpecifyKind(Today, DateTimeKind.Utc);
        var (_, frozen) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 240,
            scheduledStart: frozenStart,
            scheduledEnd: frozenEnd, machineCode: "M1");
        AddWo("WO-B", "B-B", qty: 60);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.DispatchId == frozen.DispatchId);
        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");

        Assert.Equal(DispatchStatus.InProgress, a.Status);          // 凍結未動
        Assert.Equal("M1", a.MachineCode);
        Assert.Equal(frozenStart, a.ScheduledStart);                 // 起點永不移動
        Assert.Equal(frozenEnd, a.ScheduledEnd);
        Assert.Equal(DispatchStatus.Scheduled, b.Status);
        Assert.True(b.ScheduledStart >= frozenEnd,                   // 排在凍結佔用之後
            $"WO-B 排在 {b.ScheduledStart:HH:mm}，應 ≥ 凍結結束 {frozenEnd:HH:mm}");
    }

    // ── 2a-1. 生產中進度超前：計畫結束依未完成量往前收，後續得以提早排入 ───────

    [Fact]
    public async Task RunAsync_InProgressAheadOfSchedule_PullsPlannedEndEarlier()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 帳面 08:00-12:00（240 分鐘），實際已完成 180 件 → 剩 60 分鐘 → 重算結束 09:00
        var frozenStart = DateTime.SpecifyKind(Today, DateTimeKind.Utc);
        var (_, frozen) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 240,
            scheduledStart: frozenStart,
            scheduledEnd: DateTime.SpecifyKind(Today.AddHours(4), DateTimeKind.Utc),
            machineCode: "M1");
        frozen.QtyDone = 180;
        AddWo("WO-B", "B-B", qty: 60);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.DispatchId == frozen.DispatchId);
        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");

        Assert.Equal(DispatchStatus.InProgress, a.Status);
        Assert.Equal(frozenStart, a.ScheduledStart);
        Assert.Equal(Today.AddHours(1), a.ScheduledEnd);              // 09:00
        Assert.Equal(Today.AddHours(1), b.ScheduledStart);            // 機台提早釋出
    }

    // ── 2a-2. 生產中進度落後：計畫結束依未完成量往後延 ────────────────────────

    [Fact]
    public async Task RunAsync_InProgressBehindSchedule_PushesPlannedEndLater()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 帳面 08:00-09:00，但整批 240 分鐘一件未完 → 重算結束 12:00
        var (_, frozen) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 240,
            scheduledStart: DateTime.SpecifyKind(Today, DateTimeKind.Utc),
            scheduledEnd: DateTime.SpecifyKind(Today.AddHours(1), DateTimeKind.Utc),
            machineCode: "M1");
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.DispatchId == frozen.DispatchId);
        Assert.Equal(Today.AddHours(4), a.ScheduledEnd);              // 12:00
    }

    // ── 2a-3. OnHold：重估只是「立即恢復」樂觀下界，不得把計畫結束往前拉 ───────

    [Fact]
    public async Task RunAsync_OnHold_DoesNotPullPlannedEndEarlier()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        var plannedEnd = DateTime.SpecifyKind(Today.AddHours(4), DateTimeKind.Utc); // 12:00
        var (_, held) = AddWo("WO-A", "B-A", DispatchStatus.OnHold, qty: 240,
            scheduledStart: DateTime.SpecifyKind(Today, DateTimeKind.Utc),
            scheduledEnd: plannedEnd, machineCode: "M1");
        held.QtyDone = 180;   // 剩 60 分鐘 → 樂觀重估 09:00，但暫停中不得提前
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.DispatchId == held.DispatchId);
        Assert.Equal(DispatchStatus.OnHold, a.Status);
        Assert.Equal(plannedEnd, a.ScheduledEnd);
    }

    // ── 2b. InProgress 超時：帳面 ScheduledEnd 已過但仍在生產 → 以剩餘量重估佔用 ──

    [Fact]
    public async Task RunAsync_OverdueInProgress_OccupiesUntilEstimatedCompletion()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 凍結派工：計畫 2 小時前就該完成，實際仍 InProgress、剩餘 120 分鐘
        // （qty 180、unitMin 1、QtyDone 60）→ 有效佔用應到 Today+120min（10:00），
        // 而非帳面 ScheduledEnd（06:00，已過 → 舊邏輯會讓新工作從 Today 08:00 排入撞機台）
        var (_, frozen) = AddWo("WO-A", "B-A", DispatchStatus.InProgress,
            qty: 180,
            scheduledStart: DateTime.SpecifyKind(Today.AddHours(-5), DateTimeKind.Utc),
            scheduledEnd:   DateTime.SpecifyKind(Today.AddHours(-2), DateTimeKind.Utc),
            machineCode: "M1");
        frozen.QtyDone = 60;
        AddWo("WO-B", "B-B", qty: 60);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");
        var occupiedUntil = Today.AddMinutes(120);   // 剩餘 120 分鐘自 Today 起（全天日曆）
        Assert.Equal(DispatchStatus.Scheduled, b.Status);
        Assert.True(b.ScheduledStart >= occupiedUntil,
            $"WO-B 排在 {b.ScheduledStart:HH:mm}，應 ≥ 超時凍結派工的重估完成 {occupiedUntil:HH:mm}");

        var a = _context.Dispatches.Single(d => d.DispatchId == frozen.DispatchId);
        Assert.Equal(occupiedUntil, a.ScheduledEnd);   // 已過期的帳面結束改寫成重估完成
    }

    // ── 2c. 凍結派工無排程時段（手動開工未排程）也要佔用機台 ─────────────────

    [Fact]
    public async Task RunAsync_FrozenWithoutScheduledEnd_StillOccupiesMachine()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // InProgress 但 ScheduledStart/End 皆 null（舊邏輯 ScheduledEnd != null 過濾 → 完全不佔用）
        var (_, frozen) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 90, machineCode: "M1");
        AddWo("WO-B", "B-B", qty: 60);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");
        var occupiedUntil = Today.AddMinutes(90);   // 全量 90 分鐘自 Today 起
        Assert.Equal(DispatchStatus.Scheduled, b.Status);
        Assert.True(b.ScheduledStart >= occupiedUntil,
            $"WO-B 排在 {b.ScheduledStart:HH:mm}，應 ≥ 無時段凍結派工的重估完成 {occupiedUntil:HH:mm}");

        // 手動開工未排程者：補上起點（甘特要求起訖成對）並寫入重算後的計畫結束
        var a = _context.Dispatches.Single(d => d.DispatchId == frozen.DispatchId);
        Assert.Equal(Today, a.ScheduledStart);
        Assert.Equal(occupiedUntil, a.ScheduledEnd);
    }

    // ── 3. 同工單前序 InProgress：完成時刻接力給後序 ─────────────────────────

    [Fact]
    public async Task RunAsync_FrozenPredecessor_RelaysPrevEndToNextOperation()
    {
        AddMachine("M1");
        AddMachine("M2");   // 後序有空機台可用，但仍不得早於前序完成
        AddFullDayCalendar();
        // 前序 qty 360、unitMin 1、QtyDone 0 → 剩餘量重估恰等於帳面 14:00（進度正常）
        var frozenEnd = DateTime.SpecifyKind(Today.AddHours(6), DateTimeKind.Utc); // 14:00
        var (wo, _) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 360,
            scheduledStart: DateTime.SpecifyKind(Today, DateTimeKind.Utc),
            scheduledEnd: frozenEnd, machineCode: "M1", operationSeq: 10);
        AddWo("WO-A", "B-A", DispatchStatus.Pending, operationSeq: 20);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op20 = _context.Dispatches.Single(d => d.DispatchId == "WO-A-20");
        Assert.Equal(DispatchStatus.Scheduled, op20.Status);
        Assert.True(op20.ScheduledStart >= frozenEnd,
            $"後序排在 {op20.ScheduledStart:HH:mm}，應 ≥ 前序（InProgress）完成 {frozenEnd:HH:mm}");
    }

    // ── 3b. 前序進度超前：後序接力到「重算後」的完成時刻，而非帳面舊結束 ─────────

    [Fact]
    public async Task RunAsync_FrozenPredecessorAheadOfSchedule_RelaysRecalculatedEnd()
    {
        AddMachine("M1");
        AddMachine("M2");
        AddFullDayCalendar();
        // 前序帳面 08:00-14:00（360 分鐘），已完成 300 件 → 剩 60 分鐘 → 重算完成 09:00
        var (_, op10) = AddWo("WO-A", "B-A", DispatchStatus.InProgress, qty: 360,
            scheduledStart: DateTime.SpecifyKind(Today, DateTimeKind.Utc),
            scheduledEnd: DateTime.SpecifyKind(Today.AddHours(6), DateTimeKind.Utc),
            machineCode: "M1", operationSeq: 10);
        op10.QtyDone = 300;
        AddWo("WO-A", "B-A", DispatchStatus.Pending, qty: 60, operationSeq: 20);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a10 = _context.Dispatches.Single(d => d.DispatchId == "WO-A-10");
        var a20 = _context.Dispatches.Single(d => d.DispatchId == "WO-A-20");
        Assert.Equal(Today.AddHours(1), a10.ScheduledEnd);            // 09:00
        Assert.Equal(Today.AddHours(1), a20.ScheduledStart);          // 後序接在重算後完成
    }

    // ── 4. §24.7：排程不再重查料況，一律可排（料況確認已上移至投產硬閘）──────────

    [Fact]
    public async Task RunAsync_DoesNotReEvaluateMaterial_SchedulesRegardless()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        var (_, d) = AddWo("WO-A", "B-A");
        await _context.SaveChangesAsync();

        var result = await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var reloaded = _context.Dispatches.Single(x => x.DispatchId == d.DispatchId);
        Assert.Equal(DispatchStatus.Scheduled, reloaded.Status);
        Assert.DoesNotContain(d.DispatchId, result.SkippedDispatchIds);
    }

    // ── 5. §24.7：MaterialRisk 由投產設定，排程不改寫 ─────────────────────────

    [Fact]
    public async Task RunAsync_PreservesMaterialRiskSetAtRelease()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        var (_, d) = AddWo("WO-A", "B-A");
        d.MaterialRisk = true;   // 投產（Release）時設定：在途已鎖、貨未到
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var reloaded = _context.Dispatches.Single(x => x.DispatchId == d.DispatchId);
        Assert.Equal(DispatchStatus.Scheduled, reloaded.Status);
        Assert.True(reloaded.MaterialRisk, "排程不得清除投產時設定的待料風險");
    }

    // ── 6. §7.2 派工順序：同工單內依 OperationSeq 升冪（前製程先）──────────────
    //     刻意以亂序新增工序（30→10→20），證明先後由引擎排序決定而非插入順序。
    [Fact]
    public async Task RunAsync_WithinWorkOrder_SchedulesByOperationSeqAscending()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 同一工單三道工序，各 60 分（qty 60 × unitMin 1），同機台 → 必須循序
        AddWo("WO-A", "B-A", operationSeq: 30);
        AddWo("WO-A", "B-A", operationSeq: 10);
        AddWo("WO-A", "B-A", operationSeq: 20);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op10 = _context.Dispatches.Single(x => x.DispatchId == "WO-A-10");
        var op20 = _context.Dispatches.Single(x => x.DispatchId == "WO-A-20");
        var op30 = _context.Dispatches.Single(x => x.DispatchId == "WO-A-30");

        Assert.All(new[] { op10, op20, op30 }, d =>
        {
            Assert.Equal(DispatchStatus.Scheduled, d.Status);
            Assert.True(d.ScheduledStart.HasValue && d.ScheduledEnd.HasValue,
                $"{d.DispatchId} 應排入時段");
        });

        // 前製程先：OP10 先於 OP20 先於 OP30，且同機台不重疊（前工序結束 ≤ 後工序開始）
        Assert.True(op10.ScheduledStart < op20.ScheduledStart, "OP10 應早於 OP20 開始");
        Assert.True(op20.ScheduledStart < op30.ScheduledStart, "OP20 應早於 OP30 開始");
        Assert.True(op10.ScheduledEnd <= op20.ScheduledStart, "OP10 結束後 OP20 才能開始");
        Assert.True(op20.ScheduledEnd <= op30.ScheduledStart, "OP20 結束後 OP30 才能開始");

        AssertNoOverlapOnSameMachine(new[] { op10, op20, op30 });
    }

    [Fact]
    public async Task RunAsync_InsertsMoveAndQueueGapBetweenOperations()
    {
        // OP10 與 OP20 分屬不同工作中心/機台（排除同機台序列化干擾），
        // 唯一延後 OP20 的因素是 OP10 的 MoveTime+QueueTime 間隔。
        AddMachine("M1", "WC1");
        AddMachine("M2", "WC2");
        AddFullDayCalendar();

        // OP10：qty 60 × unitMin 1 = 60 分機台佔用；MoveTime 30 + QueueTime 15 = 45 分間隔。
        AddWo("WO-G", "B-G", operationSeq: 10, wc: "WC1", moveMin: 30, queueMin: 15);
        AddWo("WO-G", "B-G", operationSeq: 20, wc: "WC2");
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op10 = _context.Dispatches.Single(x => x.DispatchId == "WO-G-10");
        var op20 = _context.Dispatches.Single(x => x.DispatchId == "WO-G-20");

        // 全天可用日曆下，OP20 最早開工 = OP10 完工 + 45 分（間隔生效）。
        Assert.Equal(op10.ScheduledEnd!.Value.AddMinutes(45), op20.ScheduledStart!.Value);
    }

    // ── 起訖必須落在有產能的時間點 ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_StartedAfterShiftEnd_SnapsToNextDayFirstSegment()
    {
        // 排程執行時刻 17:30 已過當日收班（17:00）→ 起點必須跳到次日 08:00，
        // 不得停在執行時刻本身。
        AddMachine("M1");
        AddShiftCalendar();
        AddWo("WO-CAP1", "B-CAP1", qty: 60, unitMin: 1);
        await _context.SaveChangesAsync();

        var runAt = new DateTime(2026, 6, 15, 17, 30, 0, DateTimeKind.Utc);
        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, runAt);

        var d = _context.Dispatches.Single(x => x.DispatchId == "WO-CAP1-10");
        Assert.Equal(new DateTime(2026, 6, 16, 8, 0, 0), d.ScheduledStart!.Value);
    }

    [Fact]
    public async Task RunAsync_StartedDuringLunchBreak_SnapsToAfternoonSegment()
    {
        // 執行時刻 12:30 落在午休 → 起點必須推到 13:00
        AddMachine("M1");
        AddShiftCalendar();
        AddWo("WO-CAP2", "B-CAP2", qty: 60, unitMin: 1);
        await _context.SaveChangesAsync();

        var runAt = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, runAt);

        var d = _context.Dispatches.Single(x => x.DispatchId == "WO-CAP2-10");
        Assert.Equal(new DateTime(2026, 6, 15, 13, 0, 0), d.ScheduledStart!.Value);
    }

    [Fact]
    public async Task RunAsync_MultiOperation_AllStartsAndEndsFallWithinCapacity()
    {
        // 通則把關：多工序（含工序重疊往前/往後推）排出來的每一個起點與終點，
        // 都必須落在可用時段內——不得停在午休、收班後或非工作日。
        AddMachine("M1", "WC1");
        AddMachine("M2", "WC2");
        AddShiftCalendar();

        AddWo("WO-CAP3", "B-CAP3", operationSeq: 10, wc: "WC1", qty: 100, unitMin: 3,
              batchMode: OperationMode.Batch, batchQty: 10);   // 300 分（前序較慢 → 後序往前推）
        AddWo("WO-CAP3", "B-CAP3", operationSeq: 20, wc: "WC2", qty: 100, unitMin: 1);  // 100 分
        await _context.SaveChangesAsync();

        var runAt = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, runAt);

        var all = _context.Dispatches.Where(x => x.WorkOrderId == "WO-CAP3").ToList();
        Assert.Equal(2, all.Count);
        foreach (var d in all)
        {
            Assert.True(d.ScheduledStart.HasValue && d.ScheduledEnd.HasValue, $"{d.DispatchId} 應排入時段");
            AssertWithinCapacity(d.ScheduledStart!.Value);
            AssertWithinCapacity(d.ScheduledEnd!.Value, isEnd: true);
        }
    }

    // ── 工序重疊（§7.3 b）：RunAsync 層串接前工序脈絡 ─────────────────────────

    [Fact]
    public async Task RunAsync_BatchTransfer_SuccessorSlower_OverlapsFromFirstTransferBatch()
    {
        // 兩序分屬不同工作中心（排除同機台序列化干擾）。
        // OP10：60 件 × 1 分 = 60 分，定量移轉 QT_BOR=10 → 每 10 分鐘出一批。
        // OP20：60 件 × 4 分 = 240 分 > 60 分 → 後工序較慢，第一批一到就能開工且不會斷料
        //       → 起點 = OP10 開始 + 10 分（不是等 OP10 全部完工的 60 分）。
        AddMachine("M1", "WC1");
        AddMachine("M2", "WC2");
        AddFullDayCalendar();

        AddWo("WO-OV1", "B-OV1", operationSeq: 10, wc: "WC1", unitMin: 1,
              batchMode: OperationMode.Batch, batchQty: 10);
        AddWo("WO-OV1", "B-OV1", operationSeq: 20, wc: "WC2", unitMin: 4);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op10 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV1-10");
        var op20 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV1-20");

        Assert.Equal(op10.ScheduledStart!.Value.AddMinutes(10), op20.ScheduledStart!.Value);
        Assert.True(op20.ScheduledStart < op10.ScheduledEnd, "應與前工序重疊，而非等其完工");
    }

    [Fact]
    public async Task RunAsync_BatchTransfer_SuccessorFaster_SchedulesBackwardFromLastBatch()
    {
        // OP10：60 件 × 4 分 = 240 分，定量移轉 QT_BOR=10。
        // OP20：60 件 × 1 分 = 60 分 < 240 分 → 前工序是瓶頸，後工序早開工只會等料
        //       → 往前推：終點 = OP10 完工 + 最後一批(10 件 × 1 分 = 10 分)；起點 = 終點 − 60 分。
        AddMachine("M1", "WC1");
        AddMachine("M2", "WC2");
        AddFullDayCalendar();

        AddWo("WO-OV2", "B-OV2", operationSeq: 10, wc: "WC1", unitMin: 4,
              batchMode: OperationMode.Batch, batchQty: 10);
        AddWo("WO-OV2", "B-OV2", operationSeq: 20, wc: "WC2", unitMin: 1);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op10 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV2-10");
        var op20 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV2-20");

        Assert.Equal(op10.ScheduledEnd!.Value.AddMinutes(10), op20.ScheduledEnd!.Value);
        Assert.Equal(op20.ScheduledEnd!.Value.AddMinutes(-60), op20.ScheduledStart!.Value);
        Assert.True(op20.ScheduledStart < op10.ScheduledEnd, "最後一批移轉前即已開工（重疊）");
    }

    [Fact]
    public async Task RunAsync_WholeOrderTransfer_StillWaitsForPredecessorCompletion()
    {
        // 整批移轉（預設）：不論工時大小都等前工序完工——確認重疊改動未波及既有行為。
        AddMachine("M1", "WC1");
        AddMachine("M2", "WC2");
        AddFullDayCalendar();

        AddWo("WO-OV3", "B-OV3", operationSeq: 10, wc: "WC1", unitMin: 1);
        AddWo("WO-OV3", "B-OV3", operationSeq: 20, wc: "WC2", unitMin: 4);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var op10 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV3-10");
        var op20 = _context.Dispatches.Single(x => x.DispatchId == "WO-OV3-20");

        Assert.Equal(op10.ScheduledEnd, op20.ScheduledStart);
    }

    // ── 6. 生產線多線別（WorkCenterMachine 每筆＝一條線）────────────────────

    private void AddProductionLineWc(string wc, params (string Code, MachineStatus Status)[] lines)
    {
        _context.WorkCenters.Add(new WorkCenter
        {
            WorkCenterCode = wc, WorkCenterName = wc, PlantId = PlantId, IsProductionLine = true
        });
        foreach (var (code, status) in lines)
            _context.WorkCenterMachines.Add(new WorkCenterMachine
            {
                WorkCenterCode = wc, MachineCode = code, MachineName = code, MachineStatus = status
            });
    }

    [Fact]
    public async Task RunAsync_ProductionLineWithLines_SchedulesAcrossLinesInParallel()
    {
        // 生產線登錄兩條線別 → 與一般機台同流程：兩張工單各落一條線、同時開工
        AddProductionLineWc("LINE", ("L1", MachineStatus.Available), ("L2", MachineStatus.Available));
        AddFullDayCalendar();
        AddWo("WO-A", "B-A", wc: "LINE");
        AddWo("WO-B", "B-B", wc: "LINE");
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.WorkOrderId == "WO-A");
        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");
        Assert.Equal(DispatchStatus.Scheduled, a.Status);
        Assert.Equal(DispatchStatus.Scheduled, b.Status);
        Assert.Contains(a.MachineCode, new[] { "L1", "L2" });
        Assert.Contains(b.MachineCode, new[] { "L1", "L2" });
        Assert.NotEqual(a.MachineCode, b.MachineCode);       // 各落一條線
        Assert.Equal(a.ScheduledStart, b.ScheduledStart);    // 兩線平行同時開工
    }

    [Fact]
    public async Task RunAsync_ProductionLineWithoutLines_FallsBackToSingleResource()
    {
        // 未登錄線別：維持既有行為——整條線單一排程資源（key=WorkCenterCode）、串行
        AddProductionLineWc("LINE");
        AddFullDayCalendar();
        AddWo("WO-A", "B-A", wc: "LINE");
        AddWo("WO-B", "B-B", wc: "LINE");
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(d => d.WorkOrderId == "WO-A");
        var b = _context.Dispatches.Single(d => d.WorkOrderId == "WO-B");
        Assert.Equal(DispatchStatus.Scheduled, a.Status);
        Assert.Equal(DispatchStatus.Scheduled, b.Status);
        Assert.Equal("LINE", a.MachineCode);
        Assert.Equal("LINE", b.MachineCode);
        AssertNoOverlapOnSameMachine(new[] { a, b });        // 單一資源必須串行
    }

    [Fact]
    public async Task RunAsync_ProductionLineAllLinesDown_SkipsInsteadOfSingleResourceFallback()
    {
        // 線別全數停線（狀態閘門擋掉）：不得誤 fallback 成單一資源照排，應回報 skipped
        AddProductionLineWc("LINE", ("L1", MachineStatus.BreakDown));
        AddFullDayCalendar();
        AddWo("WO-A", "B-A", wc: "LINE");
        await _context.SaveChangesAsync();

        var result = await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        Assert.Equal(0, result.Scheduled);
        Assert.Equal(1, result.Skipped);
        var a = _context.Dispatches.Single(d => d.WorkOrderId == "WO-A");
        Assert.Equal(DispatchStatus.Pending, a.Status);
        Assert.Null(a.ScheduledStart);
    }

    [Fact]
    public async Task RunAsync_PriorityRule_HigherPriorityBatchGetsMachineFirst()
    {
        // 生產批 Priority 數字小＝優先。原本引擎沒有載入 ProductionBatch，優先序恆為預設 100、
        // 實際順序只是資料庫回傳順序——「插單」從來沒有生效。
        AddMachine("M1");
        AddFullDayCalendar();
        _context.ProductionBatches.Add(new ProductionBatch { BatchId = "B-LOW", MrpRunId = "MRP", MrpRunLineSeq = 1,
            ProductCode = "P-1", Quantity = 1, Priority = 900, PlannedEndDate = Today.AddDays(7), PlantId = PlantId });
        _context.ProductionBatches.Add(new ProductionBatch { BatchId = "B-HIGH", MrpRunId = "MRP", MrpRunLineSeq = 2,
            ProductCode = "P-1", Quantity = 1, Priority = 1, PlannedEndDate = Today.AddDays(7), PlantId = PlantId });
        AddWo("WO-LOW", "B-LOW");     // 先寫入：若不看優先序，它會先排
        AddWo("WO-HIGH", "B-HIGH");
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var high = _context.Dispatches.Single(d => d.WorkOrderId == "WO-HIGH");
        var low  = _context.Dispatches.Single(d => d.WorkOrderId == "WO-LOW");
        Assert.Equal(Today, high.ScheduledStart);
        Assert.True(low.ScheduledStart >= high.ScheduledEnd);
    }

    // ── 派工暫停生命週期（2026-10-06）：機台鎖定、結案工單不佔產能、排程紀錄 ──────────

    [Fact]
    public async Task RunAsync_LockedMachine_StaysOnThatMachine_EvenAcrossWorkCenter()
    {
        AddMachine("M1");                 // 本工序工作中心 WC1、最早可用
        AddMachine("M9", wc: "WC9");      // 人指定的跨工作中心機台
        AddFullDayCalendar();
        var (_, d) = AddWo("WO-A", "B-A", machineCode: "M9");
        d.IsMachineLocked = true;
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(x => x.DispatchId == d.DispatchId);
        Assert.Equal(DispatchStatus.Scheduled, a.Status);
        Assert.Equal("M9", a.MachineCode);   // 舊行為：一律改回同工作中心最早可用的 M1
        Assert.True(a.IsMachineLocked);
    }

    [Fact]
    public async Task RunAsync_LockedMachineDown_FallsBackToWorkCenter_AndClearsLock()
    {
        AddMachine("M1");
        _context.WorkCenterMachines.Add(new WorkCenterMachine
        {
            WorkCenterCode = "WC1", MachineCode = "M2", MachineStatus = MachineStatus.BreakDown
        });
        AddFullDayCalendar();
        var (_, d) = AddWo("WO-A", "B-A", machineCode: "M2");
        d.IsMachineLocked = true;
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var a = _context.Dispatches.Single(x => x.DispatchId == d.DispatchId);
        Assert.Equal(DispatchStatus.Scheduled, a.Status);
        Assert.Equal("M1", a.MachineCode);
        Assert.False(a.IsMachineLocked);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Completed)]
    [InlineData(WorkOrderStatus.Closed)]
    [InlineData(WorkOrderStatus.Void)]
    public async Task RunAsync_FrozenDispatchOfFinishedWorkOrder_DoesNotOccupyMachine(WorkOrderStatus woStatus)
    {
        AddMachine("M1");
        AddFullDayCalendar();
        // 舊資料：工單已完工／結案／作廢，底下還留著一張暫停派工（舊規則暫停算完成）
        var (deadWo, _) = AddWo("WO-DEAD", "B-D", DispatchStatus.OnHold, qty: 600,
            scheduledStart: Today, scheduledEnd: Today.AddHours(10), machineCode: "M1");
        deadWo.Status = woStatus;
        AddWo("WO-B", "B-B", qty: 60);
        await _context.SaveChangesAsync();

        await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today);

        var b = _context.Dispatches.Single(x => x.WorkOrderId == "WO-B");
        Assert.Equal(Today, b.ScheduledStart);   // 不被殘留的暫停派工擋到 10 小時後
    }

    [Fact]
    public async Task RunAsync_RecordsLastRunState()
    {
        AddMachine("M1");
        AddFullDayCalendar();
        AddWo("WO-A", "B-A");
        await _context.SaveChangesAsync();

        var result = await MakeEngine().RunAsync(PlantId, ScheduleRule.Priority, Today, runBy: "planner1");

        Assert.False(result.Busy);
        var state = _context.ScheduleRunStates.Single(x => x.PlantId == PlantId);
        Assert.Equal("planner1", state.LastRunBy);
        Assert.Equal(1, state.LastScheduled);
        Assert.Equal(0, state.LastSkipped);
    }

}

/// <summary>
/// WorkingTimeWalker 反向走訪與 MRP 前置期分鐘估算（scheduling-spec-review 第一階段 2）。
/// </summary>
public class WorkingTimeWalkerBackwardTests
{
    // 標準工作日 08:00-12:00 / 13:00-17:00
    private static IReadOnlyList<(int Start, int End)> StdDay(DateOnly _)
        => new[] { (480, 720), (780, 1020) };

    [Fact]
    public void SubtractWorkingMinutes_CrossesLunchBreak()
    {
        // 週二 15:00 往回 240 分：13:00-15:00 吃 120，午休跳過，10:00-12:00 吃 120 → 10:00
        var end = new DateTime(2026, 6, 16, 15, 0, 0);
        var start = SmartFactory.MES.Modules.MFG.Services.WorkingTimeWalker
            .SubtractWorkingMinutes(end, 240, StdDay);
        Assert.Equal(new DateTime(2026, 6, 16, 10, 0, 0), start);
    }

    [Fact]
    public void SubtractWorkingMinutes_CrossesDays()
    {
        // 週二 10:00 往回 180 分：08:00-10:00 吃 120，剩 60 → 前一日 17:00-16:00 → 週一 16:00
        var end = new DateTime(2026, 6, 16, 10, 0, 0);
        var start = SmartFactory.MES.Modules.MFG.Services.WorkingTimeWalker
            .SubtractWorkingMinutes(end, 180, StdDay);
        Assert.Equal(new DateTime(2026, 6, 15, 16, 0, 0), start);
    }

    [Fact]
    public void SubtractWorkingMinutes_SkipsNonWorkingDays()
    {
        // 週一 09:00 往回 120 分：08:00-09:00 吃 60；週日/週六無時段跳過 → 週五 17:00-16:00 → 16:00
        IReadOnlyList<(int, int)> Segs(DateOnly d)
            => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? Array.Empty<(int, int)>()
                : new[] { (480, 720), (780, 1020) };

        var end = new DateTime(2026, 6, 15, 9, 0, 0);   // 週一
        var start = SmartFactory.MES.Modules.MFG.Services.WorkingTimeWalker
            .SubtractWorkingMinutes(end, 120, Segs);
        Assert.Equal(new DateTime(2026, 6, 12, 16, 0, 0), start);   // 週五 16:00
    }

    [Fact]
    public void EstimateLeadMinutes_IncludesMoveAndQueueTime()
    {
        var routing = new Routing { RoutingId = "R1", ProductCode = "P1" };
        routing.Operations.Add(new RoutingOperation
        {
            RoutingId = "R1", OperationSeq = 10, WorkCenterId = "WC1",
            TimeUnit = OperationTimeUnit.Minute,
            TM_SETUP = 30, TM_UNIT = 2, MoveTime = 15, QueueTime = 45
        });

        // 30 + 2×10 + 15 + 45 = 110 分
        var minutes = MrpEngine.EstimateLeadMinutes(routing, 10m);
        Assert.Equal(110m, minutes);
    }
}

/// <summary>
/// EtaRecalculator InMemory 測試：報工後本派工自身的 ScheduledEnd 必須回寫
/// （scheduling-spec-review 第一階段 3——修正前只平移下游、不更新本派工）。
/// </summary>
public class EtaRecalculatorDbTests : TestBase
{
    private const string PlantId = "P001";

    public EtaRecalculatorDbTests() => _context.CurrentPlantId = PlantId;

    [Fact]
    public async Task RecalculateAsync_UpdatesCurrentDispatchScheduledEnd()
    {
        var start = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        var oldEnd = DateTime.SpecifyKind(start.AddHours(10), DateTimeKind.Utc);
        var now = start.AddHours(2);   // 報工時點 10:00

        var dispatch = new Dispatch
        {
            DispatchId = "D-1", WorkOrderId = "WO-1", OperationSeq = 10,
            Quantity = 10m, QtyDone = 5m, Status = DispatchStatus.InProgress,
            MachineCode = "M1", PlantId = PlantId,
            ScheduledStart = start, ScheduledEnd = oldEnd
        };
        _context.Dispatches.Add(dispatch);
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO-1", OperationSeq = 10, OperationName = "OP10",
            WorkCenterId = "WC1", TimeUnit = OperationTimeUnit.Minute,
            SetupTime = 0, UnitTime = 60   // 標準 600 分；剩一半 → 300 分
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO-1", ProductionBatchId = "B-1", ProductCode = "P-1",
            RoutingId = "R1", Quantity = 10m, Status = WorkOrderStatus.InProgress,
            PlannedStartDate = start, PlannedEndDate = start.AddDays(7), PlantId = PlantId
        });
        await _context.SaveChangesAsync();

        var cal = new Mock<IWorkCalendarService>();
        cal.Setup(c => c.GetSegmentsAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateOnly>()))
           .ReturnsAsync(new[] { (0, 1440) });

        var eta = new EtaRecalculator(_context, cal.Object);
        await eta.RecalculateAsync("D-1", PlantId, now);

        var reloaded = _context.Dispatches.Single(d => d.DispatchId == "D-1");
        Assert.Equal(start, reloaded.ScheduledStart);                 // 起點不動
        Assert.Equal(now.AddMinutes(300), reloaded.ScheduledEnd);      // 終點 = now + 剩餘 300 分
    }
}
