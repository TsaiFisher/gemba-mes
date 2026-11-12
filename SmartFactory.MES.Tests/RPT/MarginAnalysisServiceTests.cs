using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.RPT.Services;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// MarginAnalysisService 多維度（品號／客戶／月份）毛利計算測試。
/// 營收＝已確認出貨明細；成本＝GS StockMovement（客戶維度經 SourceDocNo 回溯出貨單客戶）。
/// </summary>
public class MarginAnalysisServiceTests : TestBase
{
    private const string PlantId = "P001";
    private readonly MarginAnalysisService _svc;

    public MarginAnalysisServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new MarginAnalysisService(_context);
    }

    [Fact]
    public async Task Product_dimension_groups_by_product()
    {
        SeedShipment("GS1", new DateOnly(2026, 6, 5), "CUST-A", "PRD-A", 100m, 50m); // 營收 5000
        SeedStockMovement("GS1", "PRD-A", new DateOnly(2026, 6, 5), 3000m);
        SeedShipment("GS2", new DateOnly(2026, 6, 6), "CUST-B", "PRD-B", 10m, 100m); // 營收 1000
        SeedStockMovement("GS2", "PRD-B", new DateOnly(2026, 6, 6), 700m);

        var r = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId, "Product");

        Assert.Equal(2, r.Rows.Count);
        var a = r.Rows.Single(x => x.DimensionKey == "PRD-A");
        Assert.Equal(5000m, a.Revenue);
        Assert.Equal(3000m, a.Cost);
        Assert.Equal(2000m, a.GrossProfit);
        Assert.Equal(6000m, r.TotalRevenue);
        Assert.Equal(3700m, r.TotalCost);
    }

    [Fact]
    public async Task Customer_dimension_aggregates_across_products_via_sourcedoc()
    {
        // 同客戶兩張出貨（不同品號）→ 客戶維度合併為一列，成本經 SourceDocNo 回溯
        SeedShipment("GS3", new DateOnly(2026, 6, 5), "CUST-X", "PRD-A", 100m, 50m); // 5000
        SeedStockMovement("GS3", "PRD-A", new DateOnly(2026, 6, 5), 3000m);
        SeedShipment("GS4", new DateOnly(2026, 6, 8), "CUST-X", "PRD-B", 20m, 100m); // 2000
        SeedStockMovement("GS4", "PRD-B", new DateOnly(2026, 6, 8), 1200m);

        var r = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId, "Customer");

        var row = Assert.Single(r.Rows);
        Assert.Equal("CUST-X", row.DimensionKey);
        Assert.Equal(7000m, row.Revenue);   // 5000 + 2000
        Assert.Equal(4200m, row.Cost);       // 3000 + 1200
        Assert.Equal(2, row.ShipCount);
    }

    [Fact]
    public async Task Month_dimension_groups_by_year_month()
    {
        SeedShipment("GS5", new DateOnly(2026, 5, 20), "CUST-A", "PRD-A", 10m, 100m); // 2026-05
        SeedShipment("GS6", new DateOnly(2026, 6, 10), "CUST-A", "PRD-A", 30m, 100m); // 2026-06

        var r = await _svc.GetReportAsync(
            new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 30), PlantId, "Month");

        Assert.Equal(2, r.Rows.Count);
        Assert.Contains(r.Rows, x => x.DimensionKey == "2026-05" && x.Revenue == 1000m);
        Assert.Contains(r.Rows, x => x.DimensionKey == "2026-06" && x.Revenue == 3000m);
    }

    [Fact]
    public async Task Draft_shipment_excluded()
    {
        SeedShipment("GS7", new DateOnly(2026, 6, 5), "CUST-A", "PRD-A", 100m, 50m, DocStatus.Draft);

        var r = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId, "Product");

        Assert.Empty(r.Rows);
    }

    [Fact]
    public async Task Voided_shipment_cost_is_excluded_like_its_revenue()
    {
        SeedShipment("GS5", new DateOnly(2026, 6, 5), "CUST-A", "PRD-A", 10m, 100m);
        SeedStockMovement("GS5", "PRD-A", new DateOnly(2026, 6, 5), 600m);
        SeedShipment("GS6", new DateOnly(2026, 6, 6), "CUST-A", "PRD-A", 5m, 100m, DocStatus.Void);
        SeedStockMovement("GS6", "PRD-A", new DateOnly(2026, 6, 6), 300m);

        var r = await _svc.GetReportAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId, "Product");

        var row = Assert.Single(r.Rows);
        Assert.Equal(1000m, row.Revenue);
        Assert.Equal(600m, row.Cost);
    }

    private void SeedShipment(string shipId, DateOnly date, string customer, string productCode,
        decimal shipQty, decimal unitPrice, DocStatus status = DocStatus.Confirmed)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = shipId, ShipDate = date, CompanyCode = customer,
            TypeCode = "GS", PlantId = PlantId, CreatedBy = "test", Status = status,
        };
        var lineAmt = shipQty * unitPrice;
        gs.Lines.Add(new GoodsShipmentLine
        {
            LineNo = 1, ProductCode = productCode, ShipQty = shipQty,
            UnitPrice = unitPrice, LineAmount = lineAmt, TotalAmount = lineAmt,
        });
        gs.TotalAmount = lineAmt;
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
    }

    private void SeedStockMovement(string sourceDocNo, string productCode, DateOnly date, decimal totalCost)
    {
        _context.StockMovements.Add(new StockMovement
        {
            MovementId = Guid.NewGuid(), TypeCode = "GS", Direction = -1, DocNo = sourceDocNo,
            ProductCode = productCode, MovementDate = date, Qty = 1m,
            TotalCost = totalCost, SourceDocNo = sourceDocNo, PlantId = PlantId,
        });
        _context.SaveChanges();
    }
}
