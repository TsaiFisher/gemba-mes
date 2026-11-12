using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 完工（CompleteSession）：報工與完工分成兩個動作——
/// 報工可多次且人留在線上繼續生產，完工才關閉 session、結算工時；
/// 生產線多人各自完工、各報各的量，最後一人完工且數量達標才結案。
/// </summary>
public class CompleteSessionControllerTests : TestBase
{
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IReportEtaNotifier> _mockNotifier = new();
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();
    private const string PlantId = "P001";

    public CompleteSessionControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockLoc.Setup(x => x[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k));
        _mockLoc.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
                .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        _context.Set<WorkCenter>().Add(new WorkCenter
        {
            WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId,
            StandardLaborCost = 300m, MachineHourlyRate = 500m,
        });
        _context.SaveChanges();
    }

    private DispatchController NewController() =>
        new(
            _mockLoc.Object,
            new Mock<IInventoryReservationService>().Object,
            new Mock<IPlantParamService>().Object,
            new Mock<IWorkOrderMaterialReadinessService>().Object,
            new ProductionCostAccrualService(_context),
            new Mock<IStagingSourceService>().Object,
            PassThroughUom.Create(),
            new FpiGateService(_context),
            MfgSimplified(),
            new OperationSequenceGuard(_context, MfgSimplified()))
        {
            ControllerContext = GetControllerContextWithServices(
                permService: _mockPerm.Object,
                configureServices: s =>
                {
                    s.AddSingleton(_mockNotifier.Object);
                    s.AddSingleton(new Mock<IToolingShotService>().Object);
                })
        };

    private async Task SeedAsync(string dispatchId, string sessionId, decimal qty = 10m, decimal qtyDone = 0m)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = "WO001", ProductionBatchId = "B001", ProductCode = "P-001",
            RoutingId = "R001", Quantity = qty, Status = WorkOrderStatus.InProgress, PlantId = PlantId,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO001", OperationSeq = 1, WorkCenterId = "WC1", OperationName = "OP10",
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = dispatchId, WorkOrderId = "WO001", OperationSeq = 1, PlantId = PlantId,
            Quantity = qty, QtyDone = qtyDone, Status = DispatchStatus.InProgress, MachineCode = "MC01",
        });
        _context.ProductionSessions.Add(new ProductionSession
        {
            SessionId = sessionId, DispatchId = dispatchId, WorkOrderId = "WO001",
            Status = ProductionSessionStatus.Open, MachineCode = "MC01", PlantId = PlantId,
            StartAt = DateTime.UtcNow.AddHours(-2), OperatorId = "U1",
        });
        await _context.SaveChangesAsync();
    }

    private static bool IsSuccess(JsonResult json)
    {
        dynamic data = json.Value!;
        return (bool)data.GetType().GetProperty("success")!.GetValue(data)!;
    }

    [Fact]
    public async Task Complete_WithQty_ReportsAndClosesSession()
    {
        await SeedAsync("D1", "S1");

        var result = (JsonResult)await NewController().CompleteSession("D1", "S1", qtyOk: 4m, qtyNg: 1m,
            containerCode: null, ngReasonCode: "NG01");

        Assert.True(IsSuccess(result));

        var report = _context.ProductionReports.Single();
        Assert.Equal(4m, report.QtyOk);
        Assert.Equal(1m, report.QtyNg);
        Assert.Equal("NG01", report.NgReasonCode);

        var session = _context.ProductionSessions.Single(s => s.SessionId == "S1");
        Assert.Equal(ProductionSessionStatus.Closed, session.Status);
        Assert.NotNull(session.EndAt);

        var dispatch = _context.Dispatches.Single(d => d.DispatchId == "D1");
        Assert.Equal(4m, dispatch.QtyDone);
        Assert.Equal(DispatchStatus.InProgress, dispatch.Status);   // 未達量不結案
    }

    [Fact]
    public async Task Complete_WithoutQty_ClosesSessionOnly()
    {
        // 本次產出已於中途報工回報完畢 → 純下工，不強迫再報一次 0
        await SeedAsync("D1", "S1", qtyDone: 6m);

        var result = (JsonResult)await NewController().CompleteSession("D1", "S1");

        Assert.True(IsSuccess(result));
        Assert.Empty(_context.ProductionReports);
        Assert.Equal(ProductionSessionStatus.Closed,
            _context.ProductionSessions.Single(s => s.SessionId == "S1").Status);
        Assert.Equal(6m, _context.Dispatches.Single(d => d.DispatchId == "D1").QtyDone);
    }

    [Fact]
    public async Task Complete_ReachingTargetQty_ClosesDispatch()
    {
        await SeedAsync("D1", "S1", qty: 10m, qtyDone: 7m);

        await NewController().CompleteSession("D1", "S1", qtyOk: 3m);

        Assert.Equal(DispatchStatus.Closed,
            _context.Dispatches.Single(d => d.DispatchId == "D1").Status);
    }

    [Fact]
    public async Task Complete_OverReport_Rejected()
    {
        await SeedAsync("D1", "S1", qty: 10m, qtyDone: 8m);

        var result = (JsonResult)await NewController().CompleteSession("D1", "S1", qtyOk: 5m);

        Assert.False(IsSuccess(result));
        Assert.Empty(_context.ProductionReports);
        // 被擋下時 session 不得被關掉，否則作業員要重新開工才能改報
        Assert.Equal(ProductionSessionStatus.Open,
            _context.ProductionSessions.Single(s => s.SessionId == "S1").Status);
    }

    [Fact]
    public async Task Complete_AlreadyClosedSession_Rejected()
    {
        await SeedAsync("D1", "S1");
        var session = _context.ProductionSessions.Single(s => s.SessionId == "S1");
        session.Status = ProductionSessionStatus.Closed;
        session.EndAt  = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var result = (JsonResult)await NewController().CompleteSession("D1", "S1", qtyOk: 1m);

        Assert.False(IsSuccess(result));
        Assert.Empty(_context.ProductionReports);
    }

    [Fact]
    public async Task Report_KeepsSessionOpen_ForMultipleReports()
    {
        // 批次轉移：每轉一批報一次工，人還在線上，session 不能被關掉
        await SeedAsync("D1", "S1", qty: 10m);

        await NewController().CreateReport("D1", "S1", qtyOk: 3m, qtyNg: 0m, containerCode: null);
        await NewController().CreateReport("D1", "S1", qtyOk: 2m, qtyNg: 0m, containerCode: null);

        Assert.Equal(2, _context.ProductionReports.Count());
        Assert.Equal(5m, _context.Dispatches.Single(d => d.DispatchId == "D1").QtyDone);
        Assert.Equal(ProductionSessionStatus.Open,
            _context.ProductionSessions.Single(s => s.SessionId == "S1").Status);
    }

    [Fact]
    public async Task Complete_AccruesLaborCost()
    {
        await SeedAsync("D1", "S1");

        await NewController().CompleteSession("D1", "S1", qtyOk: 1m);

        var wo = _context.WorkOrders.Single(w => w.WorkOrderId == "WO001");
        Assert.True(wo.ActualLaborCost > 0m);
    }
}
