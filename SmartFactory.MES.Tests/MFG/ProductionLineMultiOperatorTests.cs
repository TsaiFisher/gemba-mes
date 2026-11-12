using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 生產線（WorkCenter.IsProductionLine）多人上/下工單元測試（2026-07-15）。
///
/// 一般機台恆同時只能 1 人在線上（避免同一台機台被兩人各自宣稱操作），生產線允許多人各自
/// StartSession/StopSession；人工成本仍按每人各自工時累加，機台/產線 overhead 則改成
/// 「最後一人下工」時依運轉時間窗結算一次，避免隨人數重複疊加（DispatchController.StartSession/
/// StopSession + ProductionCostAccrualService）。
/// </summary>
public class ProductionLineMultiOperatorTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IPermissionService> _mockPerm;
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc;

    public ProductionLineMultiOperatorTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockPerm = new Mock<IPermissionService>();
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);

        _mockLoc = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        _mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        _mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
                .Returns<string, object[]>((k, _) => new LocalizedString(k, k));
    }

    private DispatchController NewController(string userId)
    {
        var ctrl = new DispatchController(
            _mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            new Mock<IPlantParamService>().Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            new FpiGateService(_context),
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()));
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: userId, permService: _mockPerm.Object,
            // CreateReport 報工成功後以 GetRequiredService<IReportEtaNotifier> 觸發 ETA 重算、
            // IToolingShotService 回寫模具打次（本測試派工無模具，mock 即可）。
            configureServices: s =>
            {
                s.AddSingleton(new Mock<IReportEtaNotifier>().Object);
                s.AddSingleton(new Mock<IToolingShotService>().Object);
            });
        return ctrl;
    }

    private async Task SeedAsync(bool isProductionLine, decimal laborRate = 100m, decimal overheadRate = 50m,
        DispatchStatus dispatchStatus = DispatchStatus.InProgress)
    {
        _context.Set<WorkCenter>().Add(new WorkCenter
        {
            WorkCenterCode    = "WC1",
            WorkCenterName    = "WC1",
            PlantId           = PlantId,
            IsProductionLine  = isProductionLine,
            StandardLaborCost = laborRate,
            MachineHourlyRate = overheadRate,
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO001",
            PlantId     = PlantId,
            Status      = WorkOrderStatus.InProgress,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId  = "WO001",
            OperationSeq = 1,
            WorkCenterId = "WC1",
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId   = "D001",
            WorkOrderId  = "WO001",
            OperationSeq = 1,
            PlantId      = PlantId,
            Quantity     = 10m,
            Status       = dispatchStatus,
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(JsonResult json)
    {
        dynamic data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    [Fact]
    public async Task StartSession_ProductionLine_TwoOperators_BothSucceed()
    {
        await SeedAsync(isProductionLine: true);

        var r1 = await NewController("U1").StartSession("D001", null, null);
        var r2 = await NewController("U2").StartSession("D001", null, null);

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r1)));
        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r2)));
        Assert.Equal(2, _context.ProductionSessions.Count(s =>
            s.DispatchId == "D001" && s.Status == ProductionSessionStatus.Open));
    }

    [Fact]
    public async Task StartSession_ProductionLine_SameOperatorTwice_SecondBlocked()
    {
        await SeedAsync(isProductionLine: true);

        var r1 = await NewController("U1").StartSession("D001", null, null);
        var r2 = await NewController("U1").StartSession("D001", null, null);

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r1)));
        Assert.False(IsSuccess(Assert.IsType<JsonResult>(r2)));
    }

    [Fact]
    public async Task StartSession_RegularMachine_SecondOperatorBlocked()
    {
        await SeedAsync(isProductionLine: false);

        var r1 = await NewController("U1").StartSession("D001", null, null);
        var r2 = await NewController("U2").StartSession("D001", null, null);

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r1)));
        Assert.False(IsSuccess(Assert.IsType<JsonResult>(r2)));
    }

    [Fact]
    public async Task StopSession_ProductionLine_LaborAddsPerPerson_OverheadChargedOnceForWindow()
    {
        await SeedAsync(isProductionLine: true, laborRate: 100m, overheadRate: 50m);

        // U1: 08:00–10:00（2hr）；U2: 09:00–11:00（2hr，與 U1 重疊，U2 是最後下工的人）
        var s1 = new ProductionSession
        {
            SessionId = "S1", DispatchId = "D001", WorkOrderId = "WO001", PlantId = PlantId,
            Status = ProductionSessionStatus.Open,
            StartAt = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc), OperatorId = "U1",
        };
        var s2 = new ProductionSession
        {
            SessionId = "S2", DispatchId = "D001", WorkOrderId = "WO001", PlantId = PlantId,
            Status = ProductionSessionStatus.Open,
            StartAt = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc), OperatorId = "U2",
        };
        _context.ProductionSessions.AddRange(s1, s2);
        await _context.SaveChangesAsync();

        var ctrl = NewController("U1");

        // U1 先下工（10:00）——此時 U2 仍在線上，overhead 不結算。
        s1.EndAt = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
        s1.Status = ProductionSessionStatus.Closed;
        await new ProductionCostAccrualService(_context).AccrueOnSessionStopAsync(s1);
        await _context.SaveChangesAsync();

        var woAfterFirst = await _context.WorkOrders.FindAsync("WO001");
        Assert.Equal(200m, woAfterFirst!.ActualLaborCost);      // 2hr × 100
        Assert.Equal(0m, woAfterFirst.ActualOverheadCost);      // 還有人在線上，不結算

        // U2 下工（11:00）——最後一人下工，overhead 依「08:00→11:00」整個運轉時間窗結算一次
        // （而不是 U2 自己的 2 小時，也不是 U1+U2 相加的 4 小時）。
        s2.EndAt = new DateTime(2026, 7, 15, 11, 0, 0, DateTimeKind.Utc);
        s2.Status = ProductionSessionStatus.Closed;
        await new ProductionCostAccrualService(_context).AccrueOnSessionStopAsync(s2);
        await _context.SaveChangesAsync();

        var woFinal = await _context.WorkOrders.FindAsync("WO001");
        Assert.Equal(400m, woFinal!.ActualLaborCost);           // 200 + 2hr × 100
        Assert.Equal(150m, woFinal.ActualOverheadCost);         // 3hr（08:00–11:00）× 50，只算一次

        var dispatch = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(s2.EndAt, dispatch!.OverheadChargedThroughUtc);
    }

    [Fact]
    public async Task StopSession_RegularMachine_OverheadChargedImmediatelyPerSession()
    {
        await SeedAsync(isProductionLine: false, laborRate: 100m, overheadRate: 50m);

        var s1 = new ProductionSession
        {
            SessionId = "S1", DispatchId = "D001", WorkOrderId = "WO001", PlantId = PlantId,
            Status = ProductionSessionStatus.Closed,
            StartAt = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc),
            EndAt   = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc),
            OperatorId = "U1",
        };
        _context.ProductionSessions.Add(s1);
        await _context.SaveChangesAsync();

        await new ProductionCostAccrualService(_context).AccrueOnSessionStopAsync(s1);
        await _context.SaveChangesAsync();

        var wo = await _context.WorkOrders.FindAsync("WO001");
        Assert.Equal(200m, wo!.ActualLaborCost);
        Assert.Equal(100m, wo.ActualOverheadCost);   // 一般機台維持原行為：session 工時直接累加
    }

    // ── 換班：一般機台先後由不同人上工 ──────────────────────────────────────

    [Fact]
    public async Task StartSession_RegularMachine_ShiftChange_DifferentOperatorContinuesAfterPreviousStops()
    {
        await SeedAsync(isProductionLine: false);

        // U1 上工、下工（換班交接）
        var startU1 = await NewController("U1").StartSession("D001", null, null);
        Assert.True(IsSuccess(Assert.IsType<JsonResult>(startU1)));
        var s1 = await _context.ProductionSessions.SingleAsync(s => s.DispatchId == "D001" && s.OperatorId == "U1");
        var stopU1 = await NewController("U1").StopSession(s1.SessionId);
        Assert.True(IsSuccess(Assert.IsType<JsonResult>(stopU1)));

        // U1 下工後，一般機台已無人在線 → U2（不同人）可以接手繼續上工
        var startU2 = await NewController("U2").StartSession("D001", null, null);
        Assert.True(IsSuccess(Assert.IsType<JsonResult>(startU2)));

        var openSessions = await _context.ProductionSessions
            .Where(s => s.DispatchId == "D001" && s.Status == ProductionSessionStatus.Open)
            .ToListAsync();
        Assert.Single(openSessions);
        Assert.Equal("U2", openSessions[0].OperatorId);
    }

    [Fact]
    public async Task CreateReport_RegularMachine_ShiftChange_QtyAccumulatesAcrossSessions()
    {
        await SeedAsync(isProductionLine: false);

        var ctrlU1 = NewController("U1");
        await ctrlU1.StartSession("D001", null, null);
        var s1 = await _context.ProductionSessions.SingleAsync(s => s.DispatchId == "D001" && s.OperatorId == "U1");

        // U1 上工期間先報一次工（同一 session 可多次回報）
        await ctrlU1.CreateReport("D001", s1.SessionId, qtyOk: 3m, qtyNg: 0m, containerCode: null);
        await ctrlU1.CreateReport("D001", s1.SessionId, qtyOk: 2m, qtyNg: 0m, containerCode: null);
        await ctrlU1.StopSession(s1.SessionId);

        // 換班：U2 接手同一張派工單，繼續報工
        var ctrlU2 = NewController("U2");
        await ctrlU2.StartSession("D001", null, null);
        var s2 = await _context.ProductionSessions.SingleAsync(s => s.DispatchId == "D001" && s.OperatorId == "U2");
        await ctrlU2.CreateReport("D001", s2.SessionId, qtyOk: 4m, qtyNg: 0m, containerCode: null);

        var dispatch = await _context.Dispatches.FindAsync("D001");
        Assert.Equal(9m, dispatch!.QtyDone);   // 3 + 2 + 4，橫跨兩個 session 正確累加
        Assert.Equal(3, _context.ProductionReports.Count(r => r.DispatchId == "D001"));
    }

    // ── 備料（ProductionSetup）多人 ──────────────────────────────────────────

    [Fact]
    public async Task StartSetup_ProductionLine_TwoOperators_BothSucceed()
    {
        await SeedAsync(isProductionLine: true, dispatchStatus: DispatchStatus.Scheduled);

        var r1 = await NewController("U1").StartSetup("D001");
        var r2 = await NewController("U2").StartSetup("D001");

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r1)));
        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r2)));
        Assert.Equal(2, _context.ProductionSetups.Count(s =>
            s.DispatchId == "D001" && s.Status == ProductionSetupStatus.Open));
        Assert.Equal(DispatchStatus.SetupInProgress, (await _context.Dispatches.FindAsync("D001"))!.Status);
    }

    [Fact]
    public async Task StartSetup_RegularMachine_SecondOperatorBlocked()
    {
        await SeedAsync(isProductionLine: false, dispatchStatus: DispatchStatus.Scheduled);

        var r1 = await NewController("U1").StartSetup("D001");
        var r2 = await NewController("U2").StartSetup("D001");

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(r1)));
        Assert.False(IsSuccess(Assert.IsType<JsonResult>(r2)));
    }

    [Fact]
    public async Task DoneSetup_ProductionLine_ClosesAllOpenSetups_OverheadChargedOnceForWindow()
    {
        await SeedAsync(isProductionLine: true, laborRate: 100m, overheadRate: 50m,
            dispatchStatus: DispatchStatus.Scheduled);

        // 相對「現在」回推的時間，避免測試執行當下的真實時鐘影響斷言的精確度。
        var start1 = DateTime.UtcNow.AddHours(-2);      // U1 兩小時前開始備料（最早）
        var start2 = DateTime.UtcNow.AddHours(-1.5);    // U2 一個半小時前加入
        var s1 = new ProductionSetup
        {
            SetupId = "SU1", DispatchId = "D001", WorkOrderId = "WO001", PlantId = PlantId,
            Status = ProductionSetupStatus.Open, SetupStartAt = start1, OperatorId = "U1",
        };
        var s2 = new ProductionSetup
        {
            SetupId = "SU2", DispatchId = "D001", WorkOrderId = "WO001", PlantId = PlantId,
            Status = ProductionSetupStatus.Open, SetupStartAt = start2, OperatorId = "U2",
        };
        _context.ProductionSetups.AddRange(s1, s2);
        var dispatch = await _context.Dispatches.FindAsync("D001");
        dispatch!.Status = DispatchStatus.SetupInProgress;
        await _context.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var result = await NewController("U1").DoneSetup("D001");
        var after  = DateTime.UtcNow;

        Assert.True(IsSuccess(Assert.IsType<JsonResult>(result)));
        Assert.Equal(0, _context.ProductionSetups.Count(s => s.DispatchId == "D001" && s.Status == ProductionSetupStatus.Open));
        Assert.Equal(DispatchStatus.ReadyToStart, (await _context.Dispatches.FindAsync("D001"))!.Status);

        var wo = await _context.WorkOrders.FindAsync("WO001");
        // 應依「最早開始備料」（start1）到現在的單一時間窗結算（≈2 小時 × 50），
        // 不是兩人各自時間相加（那樣會 ≈ 3.5 小時 × 50 = 175，明顯偏高）。
        var expectedMin = (decimal)(before - start1).TotalHours * 50m;
        var expectedMax = (decimal)(after  - start1).TotalHours * 50m;
        Assert.InRange(wo!.ActualOverheadCost, expectedMin, expectedMax);
        Assert.True(wo.ActualOverheadCost < 150m);
    }
}
