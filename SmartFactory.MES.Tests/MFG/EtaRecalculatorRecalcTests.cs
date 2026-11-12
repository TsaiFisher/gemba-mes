using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// EtaRecalculator.RecalculateAsync 整合測試（InMemory DB）。
/// 驗證 ETA 重算主流程：無日曆降級路徑、1 分鐘差異守門、同機台後續派工平移。
/// mfg-domain.md §7.5 / §10.12。
/// </summary>
public class EtaRecalculatorRecalcTests : TestBase
{
    private const string PlantId = "P001";
    private const string WcId    = "WC01";
    private const string Machine = "MC01";

    public EtaRecalculatorRecalcTests()
    {
        _context.CurrentPlantId = PlantId;
    }

    // 空日曆（無工作時段）→ 觸發降級路徑（直接加分鐘）
    private static Mock<IWorkCalendarService> EmptyCalendar()
    {
        var mock = new Mock<IWorkCalendarService>();
        mock.Setup(c => c.GetSegmentsAsync(
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<DateOnly>()))
            .ReturnsAsync((IReadOnlyList<(int Start, int End)>)Array.Empty<(int Start, int End)>());
        return mock;
    }

    private static WorkOrderOperation MakeOp(string woId, int seq, decimal unitMin)
        => new WorkOrderOperation
        {
            WorkOrderId  = woId,
            OperationSeq = seq,
            WorkCenterId = WcId,
            TimeUnit     = OperationTimeUnit.Minute,
            SetupTime    = 0m,
            UnitTime     = unitMin,
            BatchMode    = OperationMode.WholeOrder,
            BatchQty     = 1,
        };

    private static Dispatch MakeDispatch(
        string id, string woId, int seq,
        DateTime? start, DateTime? end,
        decimal qty = 5m, decimal done = 0m,
        DispatchStatus status = DispatchStatus.Scheduled)
        => new Dispatch
        {
            DispatchId     = id,
            WorkOrderId    = woId,
            OperationSeq   = seq,
            PlantId        = PlantId,
            MachineCode    = Machine,
            Quantity       = qty,
            QtyDone        = done,
            Status         = status,
            ScheduledStart = start,
            ScheduledEnd   = end,
        };

    // ── Guard: dispatch 不存在 ──────────────────────────────────────────────

    [Fact]
    public async Task RecalculateAsync_DispatchNotFound_CompletesWithoutException()
    {
        var svc = new EtaRecalculator(_context, EmptyCalendar().Object);

        // 不應拋例外
        await svc.RecalculateAsync("NO_SUCH_ID", PlantId, DateTime.UtcNow);
    }

    // ── Guard: dispatch 無排程時段 ──────────────────────────────────────────

    [Fact]
    public async Task RecalculateAsync_NoScheduledStart_SkipsWithoutModification()
    {
        _context.WorkOrderOperations.Add(MakeOp("WO1", 1, 2m));
        _context.Dispatches.Add(MakeDispatch("D001", "WO1", 1, start: null, end: null));
        await _context.SaveChangesAsync();

        var svc = new EtaRecalculator(_context, EmptyCalendar().Object);
        await svc.RecalculateAsync("D001", PlantId, DateTime.UtcNow);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Null(d!.ScheduledEnd);
    }

    // ── 降級路徑（無日曆）且 delta > 1min → 更新 ScheduledEnd ───────────────

    [Fact]
    public async Task RecalculateAsync_FallbackNoCalendar_UpdatesScheduledEnd()
    {
        // op: UnitTime=2min/piece, qty=5 → stdMin=10
        // QtyDone=0 → remainingMin=10
        // now=08:30, oldEnd=09:00 → newEnd=08:40, delta=-20min → |delta|>1 → 更新
        var now = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc);

        _context.WorkOrderOperations.Add(MakeOp("WO1", 1, unitMin: 2m));
        _context.Dispatches.Add(MakeDispatch("D001", "WO1", 1,
            start: now.AddMinutes(-10),
            end  : now.AddMinutes(30),   // oldEnd = 09:00
            qty  : 5m, done: 0m));
        await _context.SaveChangesAsync();

        var svc = new EtaRecalculator(_context, EmptyCalendar().Object);
        await svc.RecalculateAsync("D001", PlantId, now);

        var updated = await _context.Dispatches.FindAsync("D001");
        var expectedEnd = now.AddMinutes(10);   // 08:40
        Assert.Equal(expectedEnd, updated!.ScheduledEnd!.Value, TimeSpan.FromSeconds(2));
    }

    // ── delta < 1min → 不寫入 DB ──────────────────────────────────────────

    [Fact]
    public async Task RecalculateAsync_DeltaUnderOneMinute_DoesNotSave()
    {
        // op: UnitTime=2min, qty=5 → stdMin=10
        // now=08:30, oldEnd=08:40 = now+10 → newEnd=08:40 → delta=0 < 1min → 跳過
        var now = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc);

        _context.WorkOrderOperations.Add(MakeOp("WO1", 1, unitMin: 2m));
        _context.Dispatches.Add(MakeDispatch("D001", "WO1", 1,
            start: now,
            end  : now.AddMinutes(10),  // oldEnd 恰好等於 now+remainingMin → delta=0
            qty  : 5m, done: 0m));
        await _context.SaveChangesAsync();

        var snapshotEnd = now.AddMinutes(10);

        var svc = new EtaRecalculator(_context, EmptyCalendar().Object);
        await svc.RecalculateAsync("D001", PlantId, now);

        var d = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(snapshotEnd, d!.ScheduledEnd!.Value, TimeSpan.FromSeconds(1));
    }

    // ── 同機台後續派工跟著平移 ───────────────────────────────────────────────

    [Fact]
    public async Task RecalculateAsync_MachineSucessor_ShiftsWithDelta()
    {
        // D001: oldEnd=09:00, newEnd=08:40 → delta=-20min
        // D002: 同機台, Status=Scheduled, ScheduledStart=09:30 > D001.oldEnd → 在影響集合內
        // 預期: D002.ScheduledStart → 09:10, D002.ScheduledEnd → 10:10
        var now    = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        var d1End  = now.AddMinutes(30);     // 09:00

        _context.WorkOrderOperations.Add(MakeOp("WO1", 1, unitMin: 2m));
        _context.Dispatches.Add(MakeDispatch("D001", "WO1", 1,
            start: now.AddMinutes(-10),
            end  : d1End,
            qty: 5m, done: 0m));

        // D002：同廠同機台，在 D001 結束後才開始
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId     = "D002",
            WorkOrderId    = "WO2",
            OperationSeq   = 1,
            PlantId        = PlantId,
            MachineCode    = Machine,
            Status         = DispatchStatus.Scheduled,
            Quantity       = 5m,
            QtyDone        = 0m,
            ScheduledStart = d1End.AddMinutes(30),       // 09:30 > 09:00
            ScheduledEnd   = d1End.AddMinutes(30 + 60),  // 10:30
        });
        await _context.SaveChangesAsync();

        var svc = new EtaRecalculator(_context, EmptyCalendar().Object);
        await svc.RecalculateAsync("D001", PlantId, now);

        // D001 終點更新為 now+10=08:40
        var d1 = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(now.AddMinutes(10), d1!.ScheduledEnd!.Value, TimeSpan.FromSeconds(2));

        // D002 平移 -20min：09:30→09:10, 10:30→10:10
        var d2 = await _context.Dispatches.FindAsync("D002");
        Assert.Equal(d1End.AddMinutes(10),  d2!.ScheduledStart!.Value, TimeSpan.FromSeconds(2));
        Assert.Equal(d1End.AddMinutes(70),  d2.ScheduledEnd!.Value,    TimeSpan.FromSeconds(2));
    }
}
