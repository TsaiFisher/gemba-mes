using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// WorkOrderCostFinalizationService（§16.3）測試。
/// 結算語意 = 將該期 Preliminary 工單成本鎖定為 Final（不重算費率）；
/// 涵蓋期間範圍篩選、廠別隔離、已關帳阻擋、回傳筆數，以及月結第 4 步整合。
/// </summary>
public class WorkOrderCostFinalizationServiceTests : TestBase
{
    private const string PlantId  = "P001";
    private const string PeriodId = "202606";
    private const string UserId   = "finance01";

    private readonly WorkOrderCostFinalizationService _svc;
    private readonly AccountingPeriodService _periods;

    public WorkOrderCostFinalizationServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _periods = new AccountingPeriodService(_context);
        _svc     = new WorkOrderCostFinalizationService(_context, _periods);
        SeedPeriod(PeriodStatus.Open);
    }

    [Fact]
    public async Task Finalize_PreliminaryCostsInPeriod_BecomeFinal()
    {
        SeedCost("WO-1", new DateOnly(2026, 6, 10), CostStatus.Preliminary, PlantId);
        SeedCost("WO-2", new DateOnly(2026, 6, 30), CostStatus.Preliminary, PlantId);

        var count = await _svc.FinalizeAsync(PeriodId, PlantId);

        Assert.Equal(2, count);
        Assert.All(_context.WorkOrderActualCosts.ToList(),
            c => Assert.Equal(CostStatus.Final, c.Status));
    }

    [Fact]
    public async Task Finalize_CostsOutsidePeriod_Unchanged()
    {
        SeedCost("WO-IN",  new DateOnly(2026, 6, 15), CostStatus.Preliminary, PlantId);
        SeedCost("WO-OUT", new DateOnly(2026, 7, 1),  CostStatus.Preliminary, PlantId);

        var count = await _svc.FinalizeAsync(PeriodId, PlantId);

        Assert.Equal(1, count);
        Assert.Equal(CostStatus.Final,       _context.WorkOrderActualCosts.Single(c => c.WorkOrderId == "WO-IN").Status);
        Assert.Equal(CostStatus.Preliminary, _context.WorkOrderActualCosts.Single(c => c.WorkOrderId == "WO-OUT").Status);
    }

    [Fact]
    public async Task Finalize_AlreadyFinal_NotRecounted()
    {
        SeedCost("WO-P", new DateOnly(2026, 6, 10), CostStatus.Preliminary, PlantId);
        SeedCost("WO-F", new DateOnly(2026, 6, 10), CostStatus.Final,       PlantId);

        var count = await _svc.FinalizeAsync(PeriodId, PlantId);

        Assert.Equal(1, count);   // 只算 Preliminary
    }

    [Fact]
    public async Task Finalize_OtherPlant_NotAffected()
    {
        SeedCost("WO-A", new DateOnly(2026, 6, 10), CostStatus.Preliminary, PlantId);
        SeedCost("WO-B", new DateOnly(2026, 6, 10), CostStatus.Preliminary, "P002");

        var count = await _svc.FinalizeAsync(PeriodId, PlantId);

        Assert.Equal(1, count);
        Assert.Equal(CostStatus.Preliminary,
            _context.WorkOrderActualCosts.IgnoreQueryFilters().Single(c => c.WorkOrderId == "WO-B").Status);
    }

    [Fact]
    public async Task Finalize_ClosedPeriod_Throws()
    {
        _context.AccountingPeriods.Find(PeriodId, PlantId)!.Status = PeriodStatus.Closed;
        _context.SaveChanges();
        SeedCost("WO-1", new DateOnly(2026, 6, 10), CostStatus.Preliminary, PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.FinalizeAsync(PeriodId, PlantId));
    }

    [Fact]
    public async Task Finalize_UnknownPeriod_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.FinalizeAsync("209912", PlantId));
    }

    [Fact]
    public async Task MonthEndStep4_FinalizesWorkOrderCosts()
    {
        SeedCost("WO-1", new DateOnly(2026, 6, 10), CostStatus.Preliminary, PlantId);
        var monthEnd = new MonthEndService(_context, _periods, _svc);
        var cl = await monthEnd.InitChecklistAsync(PeriodId, PlantId);
        // 月結步驟必須依序執行，先把 1–3 走完才輪得到 4
        for (var n = 1; n <= 3; n++)
            await monthEnd.CompleteStepAsync(cl.ChecklistId, n, UserId);

        await monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 4, UserId);

        Assert.Equal(CostStatus.Final,
            _context.WorkOrderActualCosts.Single(c => c.WorkOrderId == "WO-1").Status);
        var item = _context.ChecklistItems.First(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 4);
        Assert.Contains("[[Note_WoFinalized|", item.Notes ?? "");
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private void SeedPeriod(PeriodStatus status)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId  = PeriodId,
            PlantId   = PlantId,
            Year      = 2026,
            Month     = 6,
            StartDate = new DateOnly(2026, 6, 1),
            EndDate   = new DateOnly(2026, 6, 30),
            Status    = status,
        });
        _context.SaveChanges();
    }

    private void SeedCost(string woId, DateOnly costDate, CostStatus status, string plantId)
    {
        _context.WorkOrderActualCosts.Add(new WorkOrderActualCost
        {
            WorkOrderId = woId,
            CostDate    = costDate,
            Status      = status,
            PlantId     = plantId,
            ActualLabor = 100m,
            ActualOverhead = 50m,
            ActualMaterial = 200m,
        });
        _context.SaveChanges();
    }
}
