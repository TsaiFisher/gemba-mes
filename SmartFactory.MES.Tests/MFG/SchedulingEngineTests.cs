using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;
using Seg = SmartFactory.MES.Modules.MFG.Services.WorkCalendarSegmentBuilder.Segment;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// Phase C 排程引擎單元測試（mfg-domain.md §11 Phase C 驗收標準）：
///   - WorkingTimeWalker × 3（跨午休、多班別班距、週末跳過）
///   - 排序規則 × 4（EDD / SPT / FIFO / LIFO）
///   - AllocateDispatchSlot × 5（WholeOrder / Continuous / Batch / 委外 / 生產線）
/// </summary>
public class SchedulingEngineTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 輔助方法
    // ─────────────────────────────────────────────────────────────────────────

    private static CalendarContext MakeCtx(
        IDictionary<DateOnly, List<Seg>> daySegments,
        IReadOnlyList<PlannedDowntime>? downtimes = null)
    {
        var map = daySegments.ToDictionary(
            kv => ((string?)null, (string?)null, kv.Key),
            kv => (IReadOnlyList<Seg>)kv.Value);
        return new CalendarContext(map, downtimes ?? Array.Empty<PlannedDowntime>());
    }

    // 一個標準工作日：08:00-12:00（午休）13:00-17:00
    private static List<Seg> StandardDay() =>
        new() { new Seg(480, 720), new Seg(780, 1020) };

    private static WorkOrderOperation MakeOp(
        OperationMode mode = OperationMode.WholeOrder,
        decimal setupMin = 0, decimal unitMin = 60,
        bool outsource = false, decimal batchQty = 1)
        => new WorkOrderOperation
        {
            WorkOrderId  = "WO001",
            OperationSeq = 1,
            WorkCenterId = "WC01",
            TimeUnit     = OperationTimeUnit.Minute,
            SetupTime    = setupMin,
            UnitTime     = unitMin,
            BatchMode    = mode,
            BatchQty     = batchQty,
            IsOutsource  = outsource,
        };

    private static WorkOrder MakeWo(
        string batchId = "B001", int priority = 100,
        DateTime? plannedEnd = null, DateTime? createdAt = null)
        => new WorkOrder
        {
            WorkOrderId       = "WO001",
            ProductionBatchId = batchId,
            ProductCode       = "P001",
            RoutingId         = "R001",
            Quantity          = 10,
            Status            = WorkOrderStatus.Released,
            PlannedEndDate    = plannedEnd ?? new DateTime(2026, 6, 30),
            CreatedAt         = createdAt ?? new DateTime(2026, 6, 1),
            ProductionBatch   = new ProductionBatch
            {
                BatchId  = batchId,
                Priority = priority
            }
        };

    private static Dispatch MakeDispatch(
        string id = "D001", OperationMode mode = OperationMode.WholeOrder,
        bool outsource = false)
        => new Dispatch
        {
            DispatchId   = id,
            WorkOrderId  = "WO001",
            OperationSeq = 1,
            Quantity     = 5,
            Status       = DispatchStatus.Pending,
            IsOutsource  = outsource,
            Operation    = MakeOp(mode, outsource: outsource),
        };

    private static WorkCenterMachine Machine(string code = "M01")
        => new WorkCenterMachine
        {
            WorkCenterCode = "WC01",
            MachineCode    = code,
            MachineName    = code,
            MachineStatus  = MachineStatus.Available,
        };

    // ─────────────────────────────────────────────────────────────────────────
    // WorkingTimeWalker 測試
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Walker_CrossLunchBreak_CorrectEndTime()
    {
        // 早上 10:00 開始，需要 180 分鐘工時（跨 12:00 午休）
        // 08-12（240min）、13-17（240min）；10:00~12:00=120min → 差 60min，13:00+60min=14:00
        var start = new DateTime(2026, 6, 1, 10, 0, 0);
        var segs = StandardDay();

        var end = WorkingTimeWalker.AddWorkingMinutes(
            start, 180,
            _ => segs.Select(s => (s.Start, s.End)).ToList());

        Assert.Equal(new DateTime(2026, 6, 1, 14, 0, 0), end);
    }

    [Fact]
    public void Walker_MultiShiftGap_SkipsGap()
    {
        // 早班 08-12、夜班 22-06（跨午夜）；14:00 開始需要 600 分鐘
        // 當日 08-12（不含，因 cursor=14*60=840 > 720），22-24=120min
        // 隔日 00-06=360min（carry），合計 480min；再隔日 08-12=240min → 480+240=720 ≥ 600 → done
        // 實際：當日夜班 22-24=120min；Day2 carry 00-06=360min → 480min；Day2 08-12 差 120min → 10:00
        var start = new DateTime(2026, 6, 1, 14, 0, 0);

        // Day1: [480,720]早班, [1320,1440]夜班前段
        // Day2: [0,360]夜班後段（carry），[480,720]早班
        var d1 = new List<(int, int)> { (480, 720), (1320, 1440) };
        var d2 = new List<(int, int)> { (0, 360), (480, 720) };
        var date1 = new DateOnly(2026, 6, 1);
        var date2 = new DateOnly(2026, 6, 2);

        var end = WorkingTimeWalker.AddWorkingMinutes(
            start, 600,
            date => date == date1 ? d1 : date == date2 ? d2 : new List<(int, int)>());

        // 120 (夜班 Day1) + 360 (carry Day2) + 120 = 600；Day2 08:00+120min = 10:00
        Assert.Equal(new DateTime(2026, 6, 2, 10, 0, 0), end);
    }

    [Fact]
    public void Walker_SkipsWeekend_LandsOnMonday()
    {
        // 週五 16:30 開始，只剩 30min（16:30-17:00），週末無工時，需再 90min
        // → 週一 08:00+90min = 09:30
        var start = new DateTime(2026, 6, 5, 16, 30, 0);  // 週五
        var fri = StandardDay();

        var end = WorkingTimeWalker.AddWorkingMinutes(
            start, 120,
            date =>
            {
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    return new List<(int, int)>();
                return StandardDay().Select(s => (s.Start, s.End)).ToList();
            });

        // 30min 消耗完 週五，週六日跳過，週一 08:00 + 90min = 09:30
        Assert.Equal(new DateTime(2026, 6, 8, 9, 30, 0), end);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 排序規則測試（5 張派工 / 批次依規則正確排序）
    // ─────────────────────────────────────────────────────────────────────────

    private static (List<WorkOrder>, List<Dispatch>) Make5Batches()
    {
        var now = new DateTime(2026, 6, 1);
        var wos = new List<WorkOrder>
        {
            MakeWo("B1", priority:  50, plannedEnd: new DateTime(2026, 6, 10), createdAt: now.AddHours(5)),
            MakeWo("B2", priority: 200, plannedEnd: new DateTime(2026, 6, 05), createdAt: now.AddHours(1)),
            MakeWo("B3", priority: 100, plannedEnd: new DateTime(2026, 6, 20), createdAt: now.AddHours(3)),
            MakeWo("B4", priority:  10, plannedEnd: new DateTime(2026, 6, 08), createdAt: now.AddHours(4)),
            MakeWo("B5", priority: 300, plannedEnd: new DateTime(2026, 6, 15), createdAt: now.AddHours(2)),
        };
        // 每個批次各一張派工，工時依序 120/60/240/30/180 分鐘（用於 SPT 測試）
        var mins = new[] { 120m, 60m, 240m, 30m, 180m };
        var dispatches = wos.Select((wo, i) =>
        {
            var d = new Dispatch
            {
                DispatchId   = $"D{i + 1}",
                WorkOrderId  = wo.WorkOrderId,
                OperationSeq = 1,
                Quantity     = 1,
                Status       = DispatchStatus.Pending,
                Operation    = MakeOp(unitMin: mins[i]),
            };
            d.Operation.WorkOrder = wo;
            wo.Operations = new List<WorkOrderOperation> { d.Operation };
            return d;
        }).ToList();

        // 讓 wo.ProductionBatch 指向自身批次（Priority 已在 MakeWo 中設定）
        for (int i = 0; i < wos.Count; i++)
            wos[i].WorkOrderId = $"WO{i + 1}";

        return (wos, dispatches);
    }

    [Fact]
    public void SortingRule_EDD_OrdersByEarliestDueDate()
    {
        var (wos, _) = Make5Batches();
        // EDD 預期：B2(06-05) < B4(06-08) < B1(06-10) < B5(06-15) < B3(06-20)
        var sorted = wos.OrderBy(w => w.PlannedEndDate).Select(w => w.ProductionBatchId).ToList();
        Assert.Equal(new[] { "B2", "B4", "B1", "B5", "B3" }, sorted);
    }

    [Fact]
    public void SortingRule_SPT_OrdersByShortestWorkMinutes()
    {
        // SPT：B4(30) < B2(60) < B1(120) < B5(180) < B3(240)
        var (wos, _) = Make5Batches();
        // CalcRequiredMinutes：UnitTime × Qty（setup=0），由各批次工序推算
        var sorted = wos
            .OrderBy(w => w.Operations
                .Sum(op => (double)SchedulingEngine.CalcRequiredMinutes(op, 1)))
            .Select(w => w.ProductionBatchId)
            .ToList();
        Assert.Equal(new[] { "B4", "B2", "B1", "B5", "B3" }, sorted);
    }

    [Fact]
    public void SortingRule_FIFO_OrdersByCreatedAt()
    {
        var (wos, _) = Make5Batches();
        // FIFO：B2(+1h) < B5(+2h) < B3(+3h) < B4(+4h) < B1(+5h)
        var sorted = wos.OrderBy(w => w.CreatedAt).Select(w => w.ProductionBatchId).ToList();
        Assert.Equal(new[] { "B2", "B5", "B3", "B4", "B1" }, sorted);
    }

    [Fact]
    public void SortingRule_LIFO_OrdersByCreatedAtDesc()
    {
        var (wos, _) = Make5Batches();
        // LIFO：B1(+5h) > B4(+4h) > B3(+3h) > B5(+2h) > B2(+1h)
        var sorted = wos.OrderByDescending(w => w.CreatedAt).Select(w => w.ProductionBatchId).ToList();
        Assert.Equal(new[] { "B1", "B4", "B3", "B5", "B2" }, sorted);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AllocateDispatchSlot 測試
    // ─────────────────────────────────────────────────────────────────────────

    private CalendarContext StdCtx()
    {
        var days = Enumerable.Range(0, 30).ToDictionary(
            i => new DateOnly(2026, 6, 1).AddDays(i),
            _ => StandardDay());
        return MakeCtx(days);
    }

    [Fact]
    public void AllocateSlot_WholeOrder_UsesEndOfPrevDispatch()
    {
        // Quantity=1，UnitTime=120min → 總工時 120min；10:00 開始，10:00-12:00 剛好用完
        var today   = new DateTime(2026, 6, 1, 8, 0, 0);
        var prevEnd = new DateTime(2026, 6, 1, 10, 0, 0);
        var op      = MakeOp(OperationMode.WholeOrder, unitMin: 120);
        var d       = new Dispatch
        {
            DispatchId = "D001", WorkOrderId = "WO001", OperationSeq = 1,
            Quantity = 1, Status = DispatchStatus.Pending, Operation = op
        };
        var machines = new List<WorkCenterMachine> { Machine() };
        var avail    = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prevEnd, machines, StdCtx(), avail, today);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0), slot!.End);
        Assert.Equal("M01", slot.MachineCode);
    }

    [Fact]
    public void AllocateSlot_Continuous_CanStartEarlierThanPrevEnd()
    {
        // Quantity=1，UnitTime=60min → 總工時 60min；08:00+60min=09:00
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var op    = MakeOp(OperationMode.Continuous, unitMin: 60);
        var d     = new Dispatch
        {
            DispatchId = "D001", WorkOrderId = "WO001", OperationSeq = 1,
            Quantity = 1, Status = DispatchStatus.Pending,
            IsOutsource = false, Operation = op
        };
        var avail = new Dictionary<string, DateTime>();
        var machines = new List<WorkCenterMachine> { Machine() };

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), today, machines, StdCtx(), avail, today);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), slot!.End);
    }

    [Fact]
    public void AllocateSlot_NoAvailableMachine_ReturnsNull()
    {
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var op    = MakeOp();
        var d     = MakeDispatch();
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), today, new List<WorkCenterMachine>(), StdCtx(), avail, today);

        Assert.Null(slot);
    }

    [Fact]
    public void AllocateSlot_Outsource_ReturnsNullMachine()
    {
        // 委外派工：無機台，以工作日步進
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var op    = MakeOp(outsource: true, batchQty: 3);  // QT_BOR=3 天
        var d     = MakeDispatch(outsource: true);
        var avail = new Dictionary<string, DateTime>();
        var machines = new List<WorkCenterMachine>();  // 委外無機台

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), today, machines, StdCtx(), avail, today);

        Assert.NotNull(slot);
        Assert.Null(slot!.MachineCode);
        // 3 個工作日後
        Assert.True(slot.End > today.AddDays(2));
    }

    [Fact]
    public void AllocateSlot_ProductionLine_SchedulesWithoutMachine()
    {
        // 生產線（IsProductionLine=true）：WorkCenterMachines 一台都沒有（正常設定，
        // 整條線本來就不掛機台），isProductionLine=true 時仍應正確排出時段，
        // MachineCode 回傳工作中心代碼本身（供甘特圖以此分列顯示）。
        // Quantity=1，UnitTime=60min → 08:00+60min=09:00
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var op    = MakeOp(OperationMode.Continuous, unitMin: 60);
        var d     = new Dispatch
        {
            DispatchId = "D001", WorkOrderId = "WO001", OperationSeq = 1,
            Quantity = 1, Status = DispatchStatus.Pending, Operation = op
        };
        var avail = new Dictionary<string, DateTime>();
        var machines = new List<WorkCenterMachine>();   // 生產線無機台，正常設定

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), today, machines, StdCtx(), avail, today, isProductionLine: true);

        Assert.NotNull(slot);
        Assert.Equal("WC01", slot!.MachineCode);   // op.WorkCenterId
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), slot.End);
    }

    [Fact]
    public void Walker_DowntimeAfterWorkFinished_DowntimeNotCounted()
    {
        // 08:00 開始，需 30 分鐘工作（08:00 - 08:30）
        // 停機時間在 08:45 - 09:00 (15 分鐘)，這是在完工(08:30)之後。
        // 原演算法會把這 15 分鐘加算，導致完工日變成 08:45；
        // 正確演算法應在 08:30 完工。
        var start = new DateTime(2026, 6, 1, 8, 0, 0);
        var segs = StandardDay(); // 08:00-12:00, 13:00-17:00
        var downtimes = new List<(DateTime From, DateTime To)>
        {
            (new DateTime(2026, 6, 1, 8, 45, 0), new DateTime(2026, 6, 1, 9, 0, 0))
        };

        var end = WorkingTimeWalker.AddWorkingMinutes(
            start, 30,
            _ => segs.Select(s => (s.Start, s.End)).ToList(),
            downtimes);

        Assert.Equal(new DateTime(2026, 6, 1, 8, 30, 0), end);
    }

    [Fact]
    public void Walker_DowntimeDuringWork_DowntimeShifted()
    {
        // 08:00 開始，需 30 分鐘工作。
        // 停機時間在 08:10 - 08:20 (10 分鐘)。
        // 實際工作應為：08:00-08:10 (10分)，08:10-08:20 (停機 0分)，08:20-08:40 (20分，合計 30分)
        // 預期完工時間為 08:40。
        var start = new DateTime(2026, 6, 1, 8, 0, 0);
        var segs = StandardDay();
        var downtimes = new List<(DateTime From, DateTime To)>
        {
            (new DateTime(2026, 6, 1, 8, 10, 0), new DateTime(2026, 6, 1, 8, 20, 0))
        };

        var end = WorkingTimeWalker.AddWorkingMinutes(
            start, 30,
            _ => segs.Select(s => (s.Start, s.End)).ToList(),
            downtimes);

        Assert.Equal(new DateTime(2026, 6, 1, 8, 40, 0), end);
    }

    [Fact]
    public void CalcRequiredMinutes_SetupAndUnit_UseIndependentTimeUnits()
    {
        // 準備工時 2 分（SetupTimeUnit=Minute），單件工時 30 秒（TimeUnit=Second），qty=4：
        // setup = 2 分；unit = 30×4 = 120 秒 = 2 分；合計 4 分。驗證兩欄各用自己的單位換算。
        var op = new WorkOrderOperation
        {
            WorkOrderId = "WO-1", OperationSeq = 10, WorkCenterId = "WC1",
            SetupTime = 2, SetupTimeUnit = OperationTimeUnit.Minute,
            UnitTime = 30, TimeUnit = OperationTimeUnit.Second,
        };

        Assert.Equal(4m, SchedulingEngine.CalcRequiredMinutes(op, 4));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 工序重疊（§7.3 b：前工序 CD_BOR 決定往前／往後推）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>本工序（後工序）的派工：qty 件 × unitMin 分。</summary>
    private static (Dispatch D, WorkOrderOperation Op) NextOp(decimal unitMin, decimal qty)
    {
        var op = MakeOp(OperationMode.WholeOrder, unitMin: unitMin);
        var d  = new Dispatch
        {
            DispatchId = "D002", WorkOrderId = "WO001", OperationSeq = 2,
            Quantity = qty, Status = DispatchStatus.Pending, Operation = op
        };
        return (d, op);
    }

    [Fact]
    public void Overlap_Batch_SuccessorSlower_SchedulesForwardFromFirstTransfer()
    {
        // 前工序 08:00-10:00（2 件 × 60 分 = 120 分），定量移轉 QT_BOR=1。
        // 後工序 2 件 × 120 分 = 240 分 > 前工序 120 分 → 後工序是瓶頸 → 往後推：
        // 起點 = 前工序開始 08:00 + 產出第一批(60 分) = 09:00（不是整批移轉的 10:00）。
        var today   = new DateTime(2026, 6, 1, 8, 0, 0);
        var prev    = new PrevOpContext(
            Start: new DateTime(2026, 6, 1, 8, 0, 0),
            End:   new DateTime(2026, 6, 1, 10, 0, 0),
            WorkMinutes: 120, Mode: OperationMode.Batch, TransferQty: 1,
            SetupMinutes: 0, UnitMinutes: 60, GapMinutes: 0);

        var (d, op) = NextOp(unitMin: 120, qty: 2);
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prev.End, new List<WorkCenterMachine> { Machine() },
            StdCtx(), avail, today, false, prev);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), slot!.Start);
    }

    [Fact]
    public void Overlap_Batch_SuccessorFaster_SchedulesBackwardFromLastTransfer()
    {
        // 前工序 08:00-16:00（8 小時，含午休 → 當日 08:00-12:00 + 13:00-17:00 取 8 小時工時）。
        // 後工序 2 件 × 60 分 = 120 分 < 前工序 480 分 → 前工序是瓶頸 → 往前推：
        // 終點 = 前工序完工 16:00 + 本工序做最後一批(1 件 60 分) = 17:00；起點倒推 120 分 = 15:00。
        // 若仍照舊「整批移轉」，起點會是 16:00、終點 18:00（跨到隔天），圖上位置完全不同。
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var prev  = new PrevOpContext(
            Start: new DateTime(2026, 6, 1, 8, 0, 0),
            End:   new DateTime(2026, 6, 1, 16, 0, 0),
            WorkMinutes: 480, Mode: OperationMode.Batch, TransferQty: 1,
            SetupMinutes: 0, UnitMinutes: 60, GapMinutes: 0);

        var (d, op) = NextOp(unitMin: 60, qty: 2);
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prev.End, new List<WorkCenterMachine> { Machine() },
            StdCtx(), avail, today, false, prev);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 17, 0, 0), slot!.End);
        Assert.Equal(new DateTime(2026, 6, 1, 15, 0, 0), slot.Start);
        // 往前推同樣要把資源佔用推進到終點，後續派工才不會疊上來
        Assert.Equal(slot.End, avail["M01"]);
    }

    [Fact]
    public void Overlap_Backward_StartBeforeMachineAvailable_FallsBackToForward()
    {
        // 同上，但機台 15:30 前都被占用 → 倒推起點 15:00 排不進去，
        // 依 §7.3 c 落回往後計算：起點 = 前工序完工 16:00。
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var prev  = new PrevOpContext(
            Start: new DateTime(2026, 6, 1, 8, 0, 0),
            End:   new DateTime(2026, 6, 1, 16, 0, 0),
            WorkMinutes: 480, Mode: OperationMode.Batch, TransferQty: 1,
            SetupMinutes: 0, UnitMinutes: 60, GapMinutes: 0);

        var (d, op) = NextOp(unitMin: 60, qty: 2);
        var avail = new Dictionary<string, DateTime> { ["M01"] = new DateTime(2026, 6, 1, 15, 30, 0) };

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prev.End, new List<WorkCenterMachine> { Machine() },
            StdCtx(), avail, today, false, prev);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 16, 0, 0), slot!.Start);
    }

    [Fact]
    public void Overlap_WholeOrder_UnchangedWaitsForPredecessorEnd()
    {
        // 整批移轉：不論工時大小一律等前工序全部完工（原行為，不得被重疊改動波及）
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var prev  = new PrevOpContext(
            Start: new DateTime(2026, 6, 1, 8, 0, 0),
            End:   new DateTime(2026, 6, 1, 10, 0, 0),
            WorkMinutes: 120, Mode: OperationMode.WholeOrder, TransferQty: 1,
            SetupMinutes: 0, UnitMinutes: 60, GapMinutes: 0);

        var (d, op) = NextOp(unitMin: 120, qty: 2);
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prev.End, new List<WorkCenterMachine> { Machine() },
            StdCtx(), avail, today, false, prev);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0), slot!.Start);
    }

    [Fact]
    public void Overlap_Continuous_TransfersOnePiece()
    {
        // 連續移轉：每 1 件即可移轉。前工序 3 件 × 30 分 = 90 分（08:00-09:30），
        // 後工序 3 件 × 60 分 = 180 分 > 90 分 → 往後推，起點 = 08:00 + 1 件(30 分) = 08:30。
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var prev  = new PrevOpContext(
            Start: new DateTime(2026, 6, 1, 8, 0, 0),
            End:   new DateTime(2026, 6, 1, 9, 30, 0),
            WorkMinutes: 90, Mode: OperationMode.Continuous, TransferQty: 99,
            SetupMinutes: 0, UnitMinutes: 30, GapMinutes: 0);

        var (d, op) = NextOp(unitMin: 60, qty: 3);
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), prev.End, new List<WorkCenterMachine> { Machine() },
            StdCtx(), avail, today, false, prev);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 30, 0), slot!.Start);
    }

    [Fact]
    public void Overlap_NoPrevContext_BehavesAsBeforeChange()
    {
        // 第一道工序（無前工序）：起點仍是 max(prevEnd, today)
        var today = new DateTime(2026, 6, 1, 8, 0, 0);
        var (d, op) = NextOp(unitMin: 60, qty: 1);
        var avail = new Dictionary<string, DateTime>();

        var slot = SchedulingEngine.AllocateDispatchSlot(
            d, op, MakeWo(), new DateTime(2026, 6, 1, 9, 0, 0),
            new List<WorkCenterMachine> { Machine() }, StdCtx(), avail, today);

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), slot!.Start);
    }

    [Fact]
    public void SnapBackToAvailable_OutsideShift_ReturnsEndOfPreviousSegment()
    {
        // 22:00 不在班表內 → 退回當日最後一段產能結束點 17:00
        Func<DateOnly, IReadOnlyList<(int, int)>> segs = d =>
            StandardDay().Select(s => (s.Start, s.End)).ToList();

        var snapped = WorkingTimeWalker.SnapBackToAvailable(new DateTime(2026, 6, 1, 22, 0, 0), segs);

        Assert.Equal(new DateTime(2026, 6, 1, 17, 0, 0), snapped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 凍結派工（生產中家族）計畫時段重估
    //
    // 取材自實際案例 PB-2607-00014（母件 _01 / 子件 _02，兩張皆 PendingFPI、QtyDone=0）：
    // 甘特上 _01 的起點停在過去（8/13 12:20），且 _01 竟比自己的子件 _02 早完工。
    // 產線日曆為單一時段 08:00–17:00（540 分/日），週末無產能。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>整日單一時段 08:00–17:00（與案例的全廠層日曆一致，無午休切分）。</summary>
    private static List<Seg> FullDay() => new() { new Seg(480, 1020) };

    /// <summary>8/14(五)、8/17(一)–8/21(五) 有產能；8/15、8/16 週末無。</summary>
    private static CalendarContext WeekdayCtx()
    {
        var days = new Dictionary<DateOnly, List<Seg>>();
        foreach (var d in new[] { 14, 17, 18, 19, 20, 21 })
            days[new DateOnly(2026, 8, d)] = FullDay();
        return MakeCtx(days);
    }

    private static WorkOrderOperation FrozenOp(decimal setupMin, decimal unitMin)
        => new WorkOrderOperation
        {
            WorkOrderId  = "PB-2607-00014_01",
            OperationSeq = 10,
            WorkCenterId = "WCA11",
            TimeUnit     = OperationTimeUnit.Minute,
            SetupTimeUnit= OperationTimeUnit.Minute,
            SetupTime    = setupMin,
            UnitTime     = unitMin,
            BatchMode    = OperationMode.WholeOrder,
            BatchQty     = 1,
        };

    private static readonly DateTime FrozenToday = new(2026, 8, 14, 9, 6, 0);

    [Fact]
    public void FrozenEstimate_NotStarted_StartMovesToTodayInsteadOfKeepingStaleValue()
    {
        // PendingFPI 且 QtyDone=0：一件都還沒做，整份工時自 today 起算，
        // 起點必須跟著移到 today，不得留下前一次排程的化石（8/13 12:20）。
        var occ = new FrozenDispatchInput(
            DispatchId:     "PB-2607-00014_01-10",
            WorkOrderId:    "PB-2607-00014_01",
            OperationSeq:   10,
            BatchId:        "PB-2607-00014",
            MachineCode:    "WCA11-1",
            ScheduledStart: new DateTime(2026, 8, 13, 12, 20, 0),   // 化石：在 today 之前
            ScheduledEnd:   new DateTime(2026, 8, 17, 10, 46, 0),
            Quantity:       300,
            QtyDone:        0,
            Status:         DispatchStatus.PendingFPI,
            IsOutsource:    false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(setupMin: 40, unitMin: 2.0m), WeekdayCtx(), FrozenToday);

        Assert.True(est.Recalculated);
        Assert.Equal(FrozenToday, est.Start);
        Assert.True(est.Start >= FrozenToday, "未開工的凍結派工，起點不得早於 today");

        // 工時 40 + 2.0×300 = 640 分：8/14 09:06→17:00 用掉 474，餘 166 → 8/17 08:00+166 = 10:46
        Assert.Equal(new DateTime(2026, 8, 17, 10, 46, 0), est.End);
    }

    [Fact]
    public void FrozenEstimate_NotStartedOutsideShift_StartSnapsIntoWorkingTime()
    {
        // today 落在非上班時段（週六）→ 起點須 snap 到下一段可用時段（8/17 08:00），
        // 否則起點會停在沒有產能的時刻，與經過日曆校正的完工時刻對不起來。
        var occ = new FrozenDispatchInput(
            "D-SNAP", "WO-SNAP", 10, "B-TEST", "WCA11-1",
            new DateTime(2026, 8, 13, 12, 20, 0), new DateTime(2026, 8, 17, 10, 46, 0),
            Quantity: 60, QtyDone: 0, Status: DispatchStatus.ReadyToStart, IsOutsource: false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(0, 1.0m), WeekdayCtx(), new DateTime(2026, 8, 15, 10, 0, 0));

        Assert.Equal(new DateTime(2026, 8, 17, 8, 0, 0), est.Start);
        Assert.Equal(new DateTime(2026, 8, 17, 9, 0, 0), est.End);
    }

    [Fact]
    public void FrozenEstimate_InProgress_KeepsHistoricalStart()
    {
        // 已實際投入產能：帳面起點是真實開工時刻，起點在過去是正確的，必須保留。
        var staleStart = new DateTime(2026, 8, 13, 12, 20, 0);
        var occ = new FrozenDispatchInput(
            "D-RUNNING", "WO-RUNNING", 10, "B-TEST", "WCA11-1", staleStart, new DateTime(2026, 8, 17, 10, 46, 0),
            Quantity: 300, QtyDone: 100, Status: DispatchStatus.InProgress, IsOutsource: false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(40, 2.0m), WeekdayCtx(), FrozenToday);

        Assert.True(est.Recalculated);
        Assert.Equal(staleStart, est.Start);
    }

    [Fact]
    public void FrozenEstimate_SetupInProgressWithoutOutput_KeepsHistoricalStart()
    {
        // 尚無產出但機台已被準備作業佔用 → 仍視為已實際投入，保留起點。
        var staleStart = new DateTime(2026, 8, 13, 16, 0, 0);
        var occ = new FrozenDispatchInput(
            "D-SETUP", "WO-SETUP", 10, "B-TEST", "WCA11-1", staleStart, new DateTime(2026, 8, 17, 10, 46, 0),
            Quantity: 300, QtyDone: 0, Status: DispatchStatus.SetupInProgress, IsOutsource: false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(40, 2.0m), WeekdayCtx(), FrozenToday);

        Assert.Equal(staleStart, est.Start);
    }

    [Fact]
    public void FrozenEstimate_OnHold_EndOnlyPushedLaterNeverPulledIn()
    {
        // OnHold 重估是「立即恢復」的樂觀下界，只准往後延，不准把原計畫結束往前拉。
        var laterEnd = new DateTime(2026, 8, 25, 9, 0, 0);
        var occ = new FrozenDispatchInput(
            "D-HOLD", "WO-HOLD", 10, "B-TEST", "WCA11-1", new DateTime(2026, 8, 13, 12, 20, 0), laterEnd,
            Quantity: 300, QtyDone: 10, Status: DispatchStatus.OnHold, IsOutsource: false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(40, 2.0m), WeekdayCtx(), FrozenToday);

        Assert.Equal(laterEnd, est.End);
    }

    [Fact]
    public void FrozenEstimate_Outsource_NotRecalculated()
    {
        // 委外無工時概念（以工作天步進），沿用帳面時段、不寫回。
        var start = new DateTime(2026, 8, 13, 12, 20, 0);
        var end   = new DateTime(2026, 8, 17, 10, 46, 0);
        var occ = new FrozenDispatchInput(
            "D-OUT", "WO-OUT", 10, "B-TEST", null, start, end,
            Quantity: 300, QtyDone: 0, Status: DispatchStatus.PendingFPI, IsOutsource: true);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(40, 2.0m), WeekdayCtx(), FrozenToday);

        Assert.False(est.Recalculated);
        Assert.Equal(start, est.Start);
        Assert.Equal(end,   est.End);
    }

    [Fact]
    public void FrozenEstimate_NoRemainingQty_NotRecalculated()
    {
        var start = new DateTime(2026, 8, 13, 12, 20, 0);
        var end   = new DateTime(2026, 8, 17, 10, 46, 0);
        var occ = new FrozenDispatchInput(
            "D-DONE", "WO-DONE", 10, "B-TEST", "WCA11-1", start, end,
            Quantity: 300, QtyDone: 300, Status: DispatchStatus.InProgress, IsOutsource: false);

        var est = SchedulingEngine.EstimateFrozenDispatch(
            occ, FrozenOp(40, 2.0m), WeekdayCtx(), FrozenToday);

        Assert.False(est.Recalculated);
        Assert.Equal(end, est.End);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 凍結派工「彼此之間」的先後關係（EstimateFrozenDispatches）
    //
    // 凍結派工不進 pendingWos，所以主迴圈的 BOM 深度排序與 prevEnd 接力對它們一行都不生效。
    // 舊實作每張各自自 today 起算自己的剩餘工時，等於假設所有機台平行、彼此無關。
    // ─────────────────────────────────────────────────────────────────────────

    private const string Batch14 = "PB-2607-00014";
    private const string Wo01    = "PB-2607-00014_01";   // 母件 PA1102
    private const string Wo02    = "PB-2607-00014_02";   // 元件 SA1102（_01 的子件）

    /// <summary>_02 的父是 _01；_01 無父。</summary>
    private static Dictionary<string, string?> Batch14Parents() => new()
    {
        [Wo01] = null,
        [Wo02] = Wo01,
    };

    private static WorkOrderOperation OpFor(
        string woId, string wcId, decimal setupMin, decimal unitMin,
        OperationMode mode = OperationMode.WholeOrder, decimal batchQty = 1)
        => new WorkOrderOperation
        {
            WorkOrderId  = woId,
            OperationSeq = 10,
            WorkCenterId = wcId,
            TimeUnit     = OperationTimeUnit.Minute,
            SetupTimeUnit= OperationTimeUnit.Minute,
            SetupTime    = setupMin,
            UnitTime     = unitMin,
            BatchMode    = mode,
            BatchQty     = batchQty,
        };

    private static FrozenDispatchInput FrozenPendingFpi(
        string dispatchId, string woId, string machine)
        => new(dispatchId, woId, 10, Batch14, machine,
               ScheduledStart: new DateTime(2026, 8, 13, 12, 20, 0),   // 化石，兩張都有
               ScheduledEnd:   new DateTime(2026, 8, 17, 10, 46, 0),
               Quantity: 300, QtyDone: 0,
               Status: DispatchStatus.PendingFPI, IsOutsource: false);

    [Fact]
    public void FrozenBatch_ParentMustNotFinishBeforeItsComponent()
    {
        // 實際案例：母件 _01 工時 640 分、元件 _02 工時 2460 分，兩張皆 PendingFPI。
        // 各自自 today 起算的話 _01 算到 8/17、_02 算到 8/20——母件比自己的元件早三天完工。
        var occs = new List<FrozenDispatchInput>
        {
            FrozenPendingFpi("D-01", Wo01, "WCA11-1"),
            FrozenPendingFpi("D-02", Wo02, "WCA21-1"),
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [(Wo01, 10)] = OpFor(Wo01, "WCA11", 40, 2.0m),   // 40 + 2.0×300 = 640
            [(Wo02, 10)] = OpFor(Wo02, "WCA21", 60, 8.0m),   // 60 + 8.0×300 = 2460
        };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, Batch14Parents(), opMap, WeekdayCtx(), FrozenToday);

        var parent    = res["D-01"];
        var component = res["D-02"];

        Assert.True(parent.End > component.End,
            $"母件 {Wo01} 完工 {parent.End:yyyy-MM-dd HH:mm} 不得早於元件 {Wo02} 完工 " +
            $"{component.End:yyyy-MM-dd HH:mm}——元件沒做完，母件不可能先完工");

        Assert.True(parent.Start >= component.End,
            $"母件起點 {parent.Start:yyyy-MM-dd HH:mm} 不得早於元件完工 " +
            $"{component.End:yyyy-MM-dd HH:mm}");

        // 元件自己不受影響：深階先排，它仍自 today 起算
        Assert.Equal(FrozenToday, component.Start);
        Assert.Equal(new DateTime(2026, 8, 20, 14, 6, 0), component.End);

        // 母件接在元件之後：8/20 14:06 起，640 分 → 當日剩 174 分，餘 466 分落在 8/21
        Assert.Equal(new DateTime(2026, 8, 21, 15, 46, 0), parent.End);
    }

    [Fact]
    public void FrozenBatch_BatchTransfer_ParentOverlapsComponentByBackwardAnchor()
    {
        // 實際案例 PB-2607-00014 的真實參數：元件 _02 是「定量移轉、每 30 件一批」。
        // 母件不必等元件整批做完——最後一個移轉批在元件完工時抵達，母件再做那一批即結束。
        // 故正確畫法是：**終點 = 元件完工 + 母件處理最後一批的時間，再往前倒推起點**
        // （母件因此與元件的生產時段重疊）。單純接力（起點 = 元件完工）是錯的語意。
        //
        // 這正是同批的 PB-2607-00015 在畫面上的樣子——它未凍結、走主迴圈，
        // 拿到了正確的倒推重疊；0014 凍結、走另一條路徑，才被畫成單純接力。
        var occs = new List<FrozenDispatchInput>
        {
            FrozenPendingFpi("D-01", Wo01, "WCA11-1"),
            FrozenPendingFpi("D-02", Wo02, "WCA21-1"),
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [(Wo01, 10)] = OpFor(Wo01, "WCA11", 40, 2.0m),                            // 640 分
            [(Wo02, 10)] = OpFor(Wo02, "WCA21", 60, 8.0m,                             // 2460 分
                                 OperationMode.Batch, batchQty: 30),                  // 每 30 件移轉
        };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, Batch14Parents(), opMap, WeekdayCtx(), FrozenToday);

        var parent    = res["D-01"];
        var component = res["D-02"];

        // 元件不受影響
        Assert.Equal(new DateTime(2026, 8, 20, 14, 6, 0), component.End);

        // 母件終點 = 元件完工 8/20 14:06 + 單件 2.0 × 移轉批量 30 = 60 分 → 15:06
        Assert.Equal(new DateTime(2026, 8, 20, 15, 6, 0), parent.End);

        // 自 15:06 倒推 640 分：8/20 回到 08:00 用掉 426 分，餘 214 分落在 8/19（17:00−214）
        Assert.Equal(new DateTime(2026, 8, 19, 13, 26, 0), parent.Start);

        // 核心語意：母件與元件的生產時段**重疊**，但完工仍晚於元件
        Assert.True(parent.Start < component.End,
            $"定量移轉時母件應與元件重疊，實得母件起點 {parent.Start:yyyy-MM-dd HH:mm} " +
            $"晚於元件完工 {component.End:yyyy-MM-dd HH:mm}——這是單純接力、不是重疊");
        Assert.True(parent.End > component.End, "母件完工仍不得早於元件完工");
    }

    [Fact]
    public void FrozenBatch_WholeOrderTransfer_ParentWaitsForComponentEnd()
    {
        // 對照組：移轉方式為「整批」時沒有重疊，母件起點就是元件完工（原行為）。
        var occs = new List<FrozenDispatchInput>
        {
            FrozenPendingFpi("D-01", Wo01, "WCA11-1"),
            FrozenPendingFpi("D-02", Wo02, "WCA21-1"),
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [(Wo01, 10)] = OpFor(Wo01, "WCA11", 40, 2.0m),
            [(Wo02, 10)] = OpFor(Wo02, "WCA21", 60, 8.0m),   // 預設 WholeOrder
        };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, Batch14Parents(), opMap, WeekdayCtx(), FrozenToday);

        Assert.True(res["D-01"].Start >= res["D-02"].End,
            "整批移轉時母件不得早於元件完工開工");
    }

    [Fact]
    public void FrozenBatch_SameMachine_DoesNotDoubleBook()
    {
        // 同一台機器上的兩張凍結派工不得同時開工——舊實作兩張都自 today 起算，時段整段重疊。
        var occs = new List<FrozenDispatchInput>
        {
            FrozenPendingFpi("D-A", Wo01, "WCA11-1"),
            FrozenPendingFpi("D-B", Wo02, "WCA11-1"),   // 同機台
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [(Wo01, 10)] = OpFor(Wo01, "WCA11", 0, 1.0m),   // 300 分
            [(Wo02, 10)] = OpFor(Wo02, "WCA11", 0, 1.0m),   // 300 分
        };
        var machineAvail = new Dictionary<string, DateTime> { ["WCA11-1"] = FrozenToday };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, Batch14Parents(), opMap, WeekdayCtx(), FrozenToday, machineAvail);

        var first  = res["D-B"];   // 深階（元件）先
        var second = res["D-A"];

        Assert.True(second.Start >= first.End,
            $"同機台第二張起點 {second.Start:yyyy-MM-dd HH:mm} 與第一張完工 " +
            $"{first.End:yyyy-MM-dd HH:mm} 重疊——同一台機器不能同時做兩張");

        // machineAvail 應被推進到最晚完工，供後續主迴圈接力
        Assert.Equal(second.End, machineAvail["WCA11-1"]);
    }

    [Fact]
    public void FrozenBatch_AlreadyRunningDispatch_IsNotPushedByPredecessor()
    {
        // 已實際投入的派工不可能因為前置晚了就倒退重來——它的起點是既成事實。
        var staleStart = new DateTime(2026, 8, 13, 12, 20, 0);
        var occs = new List<FrozenDispatchInput>
        {
            new("D-RUN", Wo01, 10, Batch14, "WCA11-1", staleStart,
                new DateTime(2026, 8, 17, 10, 46, 0),
                Quantity: 300, QtyDone: 100,
                Status: DispatchStatus.InProgress, IsOutsource: false),
            FrozenPendingFpi("D-COMP", Wo02, "WCA21-1"),
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [(Wo01, 10)] = OpFor(Wo01, "WCA11", 40, 2.0m),
            [(Wo02, 10)] = OpFor(Wo02, "WCA21", 60, 8.0m),
        };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, Batch14Parents(), opMap, WeekdayCtx(), FrozenToday);

        Assert.Equal(staleStart, res["D-RUN"].Start);
    }

    [Fact]
    public void FrozenBatch_DifferentBatches_DoNotChainToEachOther()
    {
        // 跨批之間沒有物料相依，不共用機台就不該互相推擠。
        var occs = new List<FrozenDispatchInput>
        {
            new("D-X", "WO-X", 10, "BATCH-A", "M-X",
                new DateTime(2026, 8, 13, 12, 20, 0), new DateTime(2026, 8, 17, 0, 0, 0),
                Quantity: 300, QtyDone: 0, Status: DispatchStatus.PendingFPI, IsOutsource: false),
            new("D-Y", "WO-Y", 10, "BATCH-B", "M-Y",
                new DateTime(2026, 8, 13, 12, 20, 0), new DateTime(2026, 8, 17, 0, 0, 0),
                Quantity: 300, QtyDone: 0, Status: DispatchStatus.PendingFPI, IsOutsource: false),
        };
        var opMap = new Dictionary<(string, int), WorkOrderOperation>
        {
            [("WO-X", 10)] = OpFor("WO-X", "WC-X", 0, 1.0m),
            [("WO-Y", 10)] = OpFor("WO-Y", "WC-Y", 0, 1.0m),
        };
        var parents = new Dictionary<string, string?> { ["WO-X"] = null, ["WO-Y"] = null };

        var res = SchedulingEngine.EstimateFrozenDispatches(
            occs, parents, opMap, WeekdayCtx(), FrozenToday);

        // 兩批各自自 today 起算，起訖相同
        Assert.Equal(res["D-X"].Start, res["D-Y"].Start);
        Assert.Equal(res["D-X"].End,   res["D-Y"].End);
    }

    [Fact]
    public void BomDepth_MissingParent_StopsWithoutThrowing()
    {
        // 父工單不在集合內（跨批、或已結案被濾掉）→ 就地停止，不可拋例外
        var parents = new Dictionary<string, string?> { ["C"] = "B" };
        Assert.Equal(0, SchedulingEngine.BomDepth("C", parents));
    }

    [Fact]
    public void BomDepth_CyclicParentChain_Terminates()
    {
        // 資料異常造成環狀父子鏈不得無限迴圈
        var parents = new Dictionary<string, string?> { ["A"] = "B", ["B"] = "A" };
        var depth = SchedulingEngine.BomDepth("A", parents);
        Assert.True(depth <= 2, $"環狀父子鏈應終止，實得深度 {depth}");
    }
}
