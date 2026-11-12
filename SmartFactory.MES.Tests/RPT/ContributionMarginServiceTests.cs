using Moq;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using VP = SmartFactory.MES.Modules.RPT.Services.ContributionMarginCalculator.VariablePolicy;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// ContributionMarginService 端到端計算：營收(出貨)＋COGS(GS 異動)＋變動占比(WorkOrderActualCost)。
/// </summary>
public class ContributionMarginServiceTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<ISystemParamService> _param = new();
    private readonly ContributionMarginService _svc;

    public ContributionMarginServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _param.Setup(p => p.GetAllAsync()).ReturnsAsync(new Dictionary<string, string?>());
        _svc = new ContributionMarginService(_context, _param.Object);
    }

    [Fact]
    public async Task Build_uses_workorder_elements_for_variable_ratio()
    {
        // P1：營收1000、COGS600；工單要素 材料400+人工200+製費600(合計1200)
        SeedShipment("GS1", new DateOnly(2026, 6, 5), "P1", 1000m);
        SeedGsMovement("P1", new DateOnly(2026, 6, 5), 600m);
        SeedActualCost("WO1", "P1", material: 400m, labor: 200m, overhead: 600m,
            createdAt: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        // 材料+人工＝變動 → 占比 (400+200)/1200 = 0.5
        var r = await _svc.BuildAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            PlantId, new VP(true, true, false));

        var row = Assert.Single(r.Rows);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal(0.5m, row.VariableRatio);
        Assert.Equal(300m, row.VariableCost);        // 600 × 0.5
        Assert.Equal(700m, row.ContributionMargin);  // 1000 − 300
        Assert.True(row.HasCostBasis);
    }

    [Fact]
    public async Task Build_falls_back_to_overall_ratio_when_product_has_no_elements()
    {
        // P1 有工單要素(占比0.5)；P2 有營收但無工單要素 → 用整體平均占比0.5
        SeedShipment("GS1", new DateOnly(2026, 6, 5), "P1", 1000m);
        SeedGsMovement("P1", new DateOnly(2026, 6, 5), 600m);
        SeedActualCost("WO1", "P1", 400m, 200m, 600m, new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        SeedShipment("GS2", new DateOnly(2026, 6, 7), "P2", 500m);
        SeedGsMovement("P2", new DateOnly(2026, 6, 7), 300m);

        var r = await _svc.BuildAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            PlantId, new VP(true, true, false));

        var p2 = r.Rows.Single(x => x.ProductCode == "P2");
        Assert.Equal(0.5m, p2.VariableRatio);   // 整體平均
        Assert.Equal(150m, p2.VariableCost);     // 300 × 0.5
        Assert.Equal(350m, p2.ContributionMargin);
        Assert.False(p2.HasCostBasis);           // 標記為近似
    }

    [Fact]
    public async Task GetDefaultPolicy_defaults_material_and_labor_variable()
    {
        var policy = await _svc.GetDefaultPolicyAsync();
        Assert.True(policy.Material);
        Assert.True(policy.Labor);
        Assert.False(policy.Overhead);   // 製費預設固定
    }

    [Fact]
    public async Task SaveDefaultPolicy_writes_three_params()
    {
        await _svc.SaveDefaultPolicyAsync(new VP(true, false, true));
        _param.Verify(p => p.SetBatchAsync(It.Is<Dictionary<string, string?>>(d =>
            d[ContributionMarginService.KeyMaterial] == "true" &&
            d[ContributionMarginService.KeyLabor]    == "false" &&
            d[ContributionMarginService.KeyOverhead] == "true")), Times.Once);
    }

    [Fact]
    public async Task Build_excludes_cost_of_voided_shipment_like_its_revenue()
    {
        SeedShipment("GS-OK", new DateOnly(2026, 6, 5), "P9", 1000m);
        SeedGsMovement("P9", new DateOnly(2026, 6, 5), 600m, "GS-OK");
        SeedShipment("GS-VD", new DateOnly(2026, 6, 6), "P9", 500m, DocStatus.Void);
        SeedGsMovement("P9", new DateOnly(2026, 6, 6), 300m, "GS-VD");

        var r = await _svc.BuildAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId, new VP(true, true, false));

        var row = Assert.Single(r.Rows);
        Assert.Equal(1000m, row.Revenue);
        Assert.Equal(600m, row.Cost);
    }

    private void SeedShipment(string shipId, DateOnly date, string productCode, decimal lineAmount,
        DocStatus status = DocStatus.Confirmed)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = shipId, ShipDate = date, CompanyCode = "CUST",
            TypeCode = "GS", PlantId = PlantId, CreatedBy = "test", Status = status,
        };
        gs.Lines.Add(new GoodsShipmentLine
        {
            LineNo = 1, ProductCode = productCode, ShipQty = 1m,
            UnitPrice = lineAmount, LineAmount = lineAmount, TotalAmount = lineAmount,
        });
        gs.TotalAmount = lineAmount;
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
    }

    private void SeedGsMovement(string productCode, DateOnly date, decimal totalCost, string docNo = "")
    {
        _context.StockMovements.Add(new StockMovement
        {
            MovementId = Guid.NewGuid(), TypeCode = "GS", Direction = -1, DocNo = docNo,
            ProductCode = productCode, MovementDate = date, Qty = 1m,
            TotalCost = totalCost, PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    private void SeedActualCost(string woId, string productCode,
        decimal material, decimal labor, decimal overhead, DateTime createdAt)
    {
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = woId, ProductionBatchId = woId + "-B", ProductCode = productCode,
            RoutingId = woId + "-RT", Quantity = 1m, Status = WorkOrderStatus.Completed, PlantId = PlantId,
        });
        _context.WorkOrderActualCosts.Add(new WorkOrderActualCost
        {
            WorkOrderId = woId, ActualMaterial = material, ActualLabor = labor,
            ActualOverhead = overhead, PlantId = PlantId, CreatedAt = createdAt,
        });
        _context.SaveChanges();
    }
}
