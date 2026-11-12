using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 每晚 01:00 全量 ETA 校準與報工後 ETA 重算（2026-10-06 修正）。
///
/// 兩條路徑都在**沒有登入使用者的背景 scope** 執行。原本的查詢受全域過濾影響
/// （PlantId == null、CreatedByRoleId == null）一律撈到 0 筆，從來沒有生效；既有測試都在建構式設了
/// CurrentPlantId，剛好把這件事遮住。本檔的 DB 測試刻意讓 context 維持「背景狀態」（無廠別、無角色）。
///
/// 全量校準另外重寫了邏輯（舊版即使撈得到資料也會：再套一次 48 小時視窗、跳過已開工的工單、
/// 同機台後續派工按工單數重複平移、從不重判預計延遲），以純函式測試釘住新語意。
/// </summary>
public class EtaCalibrationTests : TestBase
{
    private const string PlantId = "P001";
    // 時刻數字即廠別當地時間（mfg-domain.md UTC 地雷）
    private static readonly DateTime Now = new(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);

    // ── 校準＝排程引擎的「維持機台與先後」模式 ────────────────────────────────

    private static WorkOrderOperation Op(string wo, int seq, string wc = "WC1") => new()
    {
        WorkOrderId = wo, OperationSeq = seq, WorkCenterId = wc, OperationName = "OP",
        TimeUnit = OperationTimeUnit.Minute, SetupTime = 0, UnitTime = 1, BatchMode = OperationMode.WholeOrder, BatchQty = 1
    };

    private void Machine(string code, string wc = "WC1")
    {
        if (!_context.WorkCenters.Local.Any(w => w.WorkCenterCode == wc))
            _context.WorkCenters.Add(new WorkCenter { WorkCenterCode = wc, WorkCenterName = wc, PlantId = PlantId });
        _context.WorkCenterMachines.Add(new WorkCenterMachine { WorkCenterCode = wc, MachineCode = code, MachineStatus = MachineStatus.Available });
    }

    private void Calendar()
    {
        for (var i = -1; i < 70; i++)
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, Date = DateOnly.FromDateTime(Now).AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440
            });
    }

    private void Wo(string wo, string batch, int priority = 100, string? parent = null, DateTime? due = null)
    {
        if (!_context.ProductionBatches.Local.Any(b => b.BatchId == batch))
            _context.ProductionBatches.Add(new ProductionBatch
            {
                BatchId = batch, MrpRunId = "MRP", MrpRunLineSeq = 1, ProductCode = "P", Quantity = 1, Priority = priority,
                PlannedEndDate = due ?? Now.AddDays(30), PlantId = PlantId, CreatedByRoleId = "R1"
            });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = wo, ProductionBatchId = batch, ParentWorkOrderId = parent, ProductCode = "P", RoutingId = "R",
            Quantity = 1, Status = WorkOrderStatus.InProgress, PlantId = PlantId, CreatedByRoleId = "R1",
            PlannedStartDate = Now, PlannedEndDate = due ?? Now.AddDays(30)
        });
    }

    private void Disp(string id, string wo, int seq, string machine, DispatchStatus status, int qty,
        DateTime start, DateTime end, decimal done = 0, string wc = "WC1")
    {
        if (!_context.WorkOrderOperations.Local.Any(o => o.WorkOrderId == wo && o.OperationSeq == seq))
            _context.WorkOrderOperations.Add(Op(wo, seq, wc));
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = id, WorkOrderId = wo, OperationSeq = seq, PlantId = PlantId, CreatedByRoleId = "R1",
            MachineCode = machine, Quantity = qty, QtyDone = done, Status = status,
            ScheduledStart = start, ScheduledEnd = end
        });
    }

    /// <summary>寫完種子後把 context 變成背景的樣子（無廠別、無角色），再跑校準。</summary>
    private async Task<EtaCalibrationResult> CalibrateInBackgroundAsync()
    {
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        _context.CurrentPlantId = null;
        _context.CurrentRoleId = null;
        _context.CurrentRolePath = null;
        return await CalibrationService().CalibratePlantAsync(PlantId, Now);
    }

    private Dispatch Get(string id) => _context.Dispatches.IgnoreQueryFilters().AsNoTracking().Single(d => d.DispatchId == id);

    [Fact]
    public async Task Calibrate_RightAfterFullSchedule_ChangesNothing_SameRulesAsScheduling()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Calendar();
        Wo("WA", "BA", 1); Wo("WB", "BB", 2);
        Disp("A", "WA", 10, "M1", DispatchStatus.Pending, 60, Now, Now);
        Disp("B", "WB", 10, "M1", DispatchStatus.Pending, 90, Now, Now);
        _context.SaveChanges();
        await new SchedulingEngine(_context, new Mock<IPlantParamService>().Object).RunAsync(PlantId, ScheduleRule.Priority, Now);
        var a = Get("A"); var b = Get("B");

        var result = await CalibrateInBackgroundAsync();

        Assert.Equal(0, result.Moved);
        Assert.Equal((a.ScheduledStart, a.ScheduledEnd), (Get("A").ScheduledStart, Get("A").ScheduledEnd));
        Assert.Equal((b.ScheduledStart, b.ScheduledEnd), (Get("B").ScheduledStart, Get("B").ScheduledEnd));
    }

    [Fact]
    public async Task Calibrate_RunningLate_PushesSameMachineFollowers_EvenBeyond48Hours_AndOnlyOnce()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Calendar();
        // 生產中：600 件做了 0 件、帳面 08:00 就該結束 → 重估到 18:00
        Wo("WR", "BR"); Disp("R", "WR", 10, "M1", DispatchStatus.InProgress, 600, Now.AddHours(-10), Now);
        // 同機台後面三張屬於三張不同工單，最後一張在 3 天後（超出報工即時重算的 48 小時視窗）
        Wo("WA", "BA"); Disp("A", "WA", 10, "M1", DispatchStatus.Scheduled, 60, Now, Now.AddHours(1));
        Wo("WB", "BB"); Disp("B", "WB", 10, "M1", DispatchStatus.Scheduled, 60, Now.AddHours(1), Now.AddHours(2));
        Wo("WF", "BF"); Disp("F", "WF", 10, "M1", DispatchStatus.Scheduled, 60, Now.AddDays(3), Now.AddDays(3).AddHours(1));

        await CalibrateInBackgroundAsync();

        Assert.Equal(Now.AddHours(10), Get("R").ScheduledEnd);
        Assert.Equal(Now.AddHours(10), Get("A").ScheduledStart);   // 機台被佔到 18:00
        Assert.Equal(Now.AddHours(11), Get("B").ScheduledStart);   // 接在 A 後面——不是被每張工單各推一次
        Assert.Equal(Now.AddHours(12), Get("F").ScheduledStart);   // 同排程規則：遠期那張也接上來（48 小時外照樣處理）
        Assert.Equal("M1", Get("F").MachineCode);
    }

    [Fact]
    public async Task Calibrate_AheadOfSchedule_PullsFollowersEarlier_LikeScheduling()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Calendar();
        // 生產中且提早：100 件做了 90 件 → 剩 10 分鐘（帳面 12:00 才結束）
        Wo("WR", "BR"); Disp("R", "WR", 10, "M1", DispatchStatus.InProgress, 100, Now.AddHours(-2), Now.AddHours(4), done: 90);
        Wo("WA", "BA"); Disp("A", "WA", 10, "M1", DispatchStatus.Scheduled, 60, Now.AddHours(4), Now.AddHours(5));

        await CalibrateInBackgroundAsync();

        Assert.Equal(Now.AddMinutes(10), Get("R").ScheduledEnd);
        Assert.Equal(Now.AddMinutes(10), Get("A").ScheduledStart);   // 跟排程一樣往前補
    }

    [Fact]
    public async Task Calibrate_PredecessorDelay_PropagatesToNextOperationOnAnotherMachine()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Machine("M2", "WC2"); Calendar();
        Wo("W1", "B1");
        Disp("R10", "W1", 10, "M1", DispatchStatus.InProgress, 600, Now.AddHours(-10), Now);
        Disp("N20", "W1", 20, "M2", DispatchStatus.Scheduled, 60, Now, Now.AddHours(1), wc: "WC2");

        await CalibrateInBackgroundAsync();

        Assert.Equal(Now.AddHours(10), Get("N20").ScheduledStart);   // 前工序 18:00 完工才接得上
    }

    [Fact]
    public async Task Calibrate_KeepsMachineAndOrder_EvenWhenAnotherMachineIsIdle()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Machine("M2"); Calendar();
        // 優先序：WB 比 WA 優先，但目前排在 WA 後面；M2 整天空著
        Wo("WA", "BA", priority: 9); Disp("A", "WA", 10, "M1", DispatchStatus.Scheduled, 60, Now.AddHours(1), Now.AddHours(2));
        Wo("WB", "BB", priority: 1); Disp("B", "WB", 10, "M1", DispatchStatus.Scheduled, 60, Now.AddHours(2), Now.AddHours(3));

        await CalibrateInBackgroundAsync();

        Assert.Equal("M1", Get("A").MachineCode);
        Assert.Equal("M1", Get("B").MachineCode);   // 不換到空著的 M2
        Assert.True(Get("A").ScheduledStart < Get("B").ScheduledStart);   // 不依優先序改先後
    }

    [Fact]
    public async Task Calibrate_DoesNotSchedulePending_NorMarkScheduleFresh()
    {
        _context.CurrentPlantId = PlantId;
        Machine("M1"); Calendar();
        Wo("WP", "BP"); Disp("P", "WP", 10, "M1", DispatchStatus.Pending, 60, Now, Now);

        await CalibrateInBackgroundAsync();

        Assert.Equal(DispatchStatus.Pending, Get("P").Status);   // 排新工作是重排排程的事
        Assert.False(_context.ScheduleRunStates.Any());          // 不能讓排程看起來是最新的
    }

    // ── DB：背景狀態（無廠別、無角色）下仍要撈得到資料 ────────────────────────

    private void SeedBackgroundData(DateTime runningEnd, DateTime batchDue)
    {
        _context.CurrentPlantId = PlantId;   // 只為了寫入；下面清掉
        Machine("M1"); Machine("M2");
        _context.ProductionBatches.Add(new ProductionBatch
        {
            BatchId = "B1", MrpRunId = "MRP", MrpRunLineSeq = 1, ProductCode = "P", Quantity = 1,
            PlannedEndDate = batchDue, PlantId = PlantId, CreatedByRoleId = "R1"
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "W1", ProductionBatchId = "B1", ProductCode = "P", RoutingId = "R", Quantity = 600,
            Status = WorkOrderStatus.InProgress, PlantId = PlantId, CreatedByRoleId = "R1",
            PlannedStartDate = Now, PlannedEndDate = batchDue
        });
        _context.WorkOrderOperations.Add(Op("W1", 10));
        _context.WorkOrderOperations.Add(Op("W1", 20));
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D10", WorkOrderId = "W1", OperationSeq = 10, PlantId = PlantId, CreatedByRoleId = "R1",
            MachineCode = "M1", Quantity = 600, Status = DispatchStatus.InProgress,
            ScheduledStart = Now.AddHours(-10), ScheduledEnd = runningEnd
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = "D20", WorkOrderId = "W1", OperationSeq = 20, PlantId = PlantId, CreatedByRoleId = "R1",
            MachineCode = "M2", Quantity = 60, Status = DispatchStatus.Scheduled,
            ScheduledStart = runningEnd, ScheduledEnd = runningEnd.AddHours(1)
        });
        for (var i = -1; i < 70; i++)
            _context.WorkCalendarSegments.Add(new WorkCalendarSegment
            {
                PlantId = PlantId, Date = DateOnly.FromDateTime(Now).AddDays(i), Seq = 1, StartMinute = 0, EndMinute = 1440
            });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        // 背景 scope 的樣子：沒有登入使用者
        _context.CurrentPlantId = null;
        _context.CurrentRoleId = null;
        _context.CurrentRolePath = null;
    }

    private EtaCalibrationService CalibrationService() =>
        new(_context, new SchedulingEngine(_context, new Mock<IPlantParamService>().Object));

    [Fact]
    public async Task CalibratePlant_InBackgroundContext_FindsAndShiftsDispatches()
    {
        SeedBackgroundData(runningEnd: Now, batchDue: Now.AddDays(5));

        var result = await CalibrationService().CalibratePlantAsync(PlantId, Now);

        Assert.True(result.Moved >= 2, $"背景狀態下應撈得到並調整派工，實得 {result.Moved}");
        var d20 = _context.Dispatches.IgnoreQueryFilters().AsNoTracking().Single(d => d.DispatchId == "D20");
        Assert.Equal(Now.AddHours(10), d20.ScheduledStart);
    }

    [Fact]
    public async Task CalibratePlant_RefreshesPredictedLate_AndReportsNewlyLate()
    {
        // 交期今天 12:00，但生產中那張要到 18:00、下一道到 19:00 → 預計延遲
        SeedBackgroundData(runningEnd: Now, batchDue: Now.AddHours(4));

        var result = await CalibrationService().CalibratePlantAsync(PlantId, Now);

        var late = Assert.Single(result.NewlyLate);
        Assert.Equal("B1", late.BatchId);
        Assert.Equal(Now.AddHours(11), late.Eta);
        Assert.True(_context.ProductionBatches.IgnoreQueryFilters().AsNoTracking().Single().IsPredictedLate);

        // 再跑一次：已經是延遲，不重複回報
        Assert.Empty((await CalibrationService().CalibratePlantAsync(PlantId, Now)).NewlyLate);
    }

    [Fact]
    public async Task ReportEtaRecalculate_InBackgroundContext_StillShiftsDispatch()
    {
        // 報工後的增量重算同樣在背景 scope：原本第一個查詢就撈不到派工而直接 return
        SeedBackgroundData(runningEnd: Now, batchDue: Now.AddDays(5));
        var calendar = new WorkCalendarService(_context);

        await new EtaRecalculator(_context, calendar).RecalculateAsync("D10", PlantId, Now);

        var d10 = _context.Dispatches.IgnoreQueryFilters().AsNoTracking().Single(d => d.DispatchId == "D10");
        Assert.Equal(Now.AddHours(10), d10.ScheduledEnd);   // 600 件剩 600 件 → now + 600 分（沿日曆）
    }
}
