using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 備料完成的成本歸屬：人工逐人計（備料人員與生產人員可能不同人），
/// 製造費用依時間窗只算一次（不隨備料人數疊加）。
/// </summary>
public class SetupCostAccrualTests : TestBase
{
    private readonly ProductionCostAccrualService _svc;
    private const string PlantId = "P001";
    private const string WorkOrderId = "WO-001";
    private const string DispatchId = "D-001";

    public SetupCostAccrualTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new ProductionCostAccrualService(_context);
        Seed();
    }

    private void Seed()
    {
        _context.Set<WorkCenter>().Add(new WorkCenter
        {
            WorkCenterCode = "WC1", WorkCenterName = "WC1", PlantId = PlantId,
            StandardLaborCost = 300m,   // 元/小時
            MachineHourlyRate = 500m,   // 元/小時
        });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WorkOrderId, PlantId = PlantId, ProductCode = "P1",
            ProductionBatchId = "PB-1", Quantity = 100m,
        });
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = WorkOrderId, OperationSeq = 10, WorkCenterId = "WC1",
            OperationName = "OP10", SetupTime = 30m,
        });
        _context.SaveChanges();
    }

    private static Dispatch NewDispatch() => new()
    {
        DispatchId = DispatchId, WorkOrderId = WorkOrderId, OperationSeq = 10, PlantId = PlantId,
        Quantity = 100m,
    };

    private static ProductionSetup Setup(string operatorId, DateTime start, DateTime end) => new()
    {
        SetupId = Guid.NewGuid().ToString("N"), DispatchId = DispatchId, WorkOrderId = WorkOrderId,
        OperatorId = operatorId, SetupStartAt = start, SetupEndAt = end,
        Status = ProductionSetupStatus.Closed, PlantId = PlantId,
    };

    [Fact]
    public async Task SingleOperator_AccruesBothLaborAndOverhead()
    {
        var start = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var setups = new[] { Setup("U1", start, start.AddHours(2)) };

        await _svc.AccrueOnSetupDoneAsync(setups, NewDispatch());
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Single(w => w.WorkOrderId == WorkOrderId);
        Assert.Equal(600m, wo.ActualLaborCost);      // 2h × 300
        Assert.Equal(1000m, wo.ActualOverheadCost);  // 2h × 500
    }

    [Fact]
    public async Task MultipleOperators_LaborPerPerson_OverheadOncePerWindow()
    {
        // 生產線 3 人同時備料 2 小時：人工要 3 份（實際投入 6 人時），
        // 但機台/產線只被占用一次 → overhead 仍是 2 小時份。
        var start = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var setups = new[]
        {
            Setup("U1", start, start.AddHours(2)),
            Setup("U2", start, start.AddHours(2)),
            Setup("U3", start, start.AddHours(2)),
        };

        await _svc.AccrueOnSetupDoneAsync(setups, NewDispatch());
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Single(w => w.WorkOrderId == WorkOrderId);
        Assert.Equal(1800m, wo.ActualLaborCost);     // 6h × 300
        Assert.Equal(1000m, wo.ActualOverheadCost);  // 時間窗 2h × 500
    }

    [Fact]
    public async Task StaggeredOperators_OverheadCoversWholeWindow()
    {
        // 先來後到：人工各自 2h/1h，overhead 從最早開始到最後結束＝3 小時
        var start = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var setups = new[]
        {
            Setup("U1", start, start.AddHours(2)),
            Setup("U2", start.AddHours(2), start.AddHours(3)),
        };

        await _svc.AccrueOnSetupDoneAsync(setups, NewDispatch());
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Single(w => w.WorkOrderId == WorkOrderId);
        Assert.Equal(900m, wo.ActualLaborCost);      // 3h × 300
        Assert.Equal(1500m, wo.ActualOverheadCost);  // 3h × 500
    }

    [Fact]
    public async Task NoSetups_AccruesNothing()
    {
        await _svc.AccrueOnSetupDoneAsync(Array.Empty<ProductionSetup>(), NewDispatch());
        await _context.SaveChangesAsync();

        var wo = _context.WorkOrders.Single(w => w.WorkOrderId == WorkOrderId);
        Assert.Equal(0m, wo.ActualLaborCost);
        Assert.Equal(0m, wo.ActualOverheadCost);
    }
}
