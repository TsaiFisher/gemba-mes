using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// GrossMarginService 報表計算邏輯測試。
/// 覆蓋收入彙總（**已過帳**出貨）、銷貨成本彙總（GS StockMovement）、
/// 毛利計算公式、日期區間過濾與邊界條件。
/// </summary>
public class GrossMarginServiceTests : TestBase
{
    private const string PlantId = "P001";

    private readonly GrossMarginService _svc;

    public GrossMarginServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new GrossMarginService(_context);
    }

    // ── Revenue 計算 ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_PostedShipment_RevenueCorrect()
    {
        SeedShipment("GS001", new DateOnly(2026, 6, 10), "PRD-A", shipQty: 100m, unitPrice: 50m);

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        Assert.Single(report.Rows);
        Assert.Equal(5000m, report.Rows[0].Revenue);     // 100 × 50
        Assert.Equal(5000m, report.TotalRevenue);
    }

    /// <summary>
    /// 尚未過帳的出貨不計收入——它的成本腿（GS 出庫異動）根本還不存在，
    /// 只算收入會得到 100% 毛利率。兩條腿必須是同一批單據。
    /// </summary>
    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Confirmed)]
    public async Task GetReport_NotPostedShipment_ExcludedFromRevenue(DocStatus status)
    {
        SeedShipment("GS002", new DateOnly(2026, 6, 10), "PRD-A",
            shipQty: 100m, unitPrice: 50m, status: status);

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        Assert.Empty(report.Rows);
        Assert.Equal(0m, report.TotalRevenue);
    }

    // ── COGS 計算 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_GsStockMovement_CogsMatched()
    {
        SeedShipment("GS003", new DateOnly(2026, 6, 15), "PRD-B", shipQty: 50m, unitPrice: 200m);
        SeedStockMovement("PRD-B", new DateOnly(2026, 6, 15), totalCost: 6000m);

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        var row = report.Rows.Single(r => r.ProductCode == "PRD-B");
        Assert.Equal(10000m, row.Revenue);    // 50 × 200
        Assert.Equal(6000m,  row.Cost);
        Assert.Equal(4000m,  row.GrossProfit);
    }

    /// <summary>
    /// 已作廢出貨：營收腿本來就不算，成本腿的原出庫列也要排除（作廢沖銷列方向 +1，不在成本腿裡）。
    /// 原本只看方向 −1，作廢一張出貨毛利就憑空少一筆成本（2026-10-22）。
    /// </summary>
    [Fact]
    public async Task GetReport_VoidedShipment_CostExcludedLikeRevenue()
    {
        SeedShipment("GS-OK", new DateOnly(2026, 6, 10), "PRD-V", shipQty: 10m, unitPrice: 100m);
        SeedStockMovement("PRD-V", new DateOnly(2026, 6, 10), totalCost: 600m, docNo: "GS-OK");
        SeedShipment("GS-VD", new DateOnly(2026, 6, 11), "PRD-V", shipQty: 5m, unitPrice: 100m, status: DocStatus.Void);
        SeedStockMovement("PRD-V", new DateOnly(2026, 6, 11), totalCost: 300m, docNo: "GS-VD");

        var report = await _svc.GetReportAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        var row = report.Rows.Single(r => r.ProductCode == "PRD-V");
        Assert.Equal(1000m, row.Revenue);
        Assert.Equal(600m, row.Cost);
    }

    [Fact]
    public async Task GetReport_NoStockMovement_CostZero()
    {
        // BVA：有出貨收入但無對應庫存異動 → Cost=0，毛利=Revenue
        SeedShipment("GS004", new DateOnly(2026, 6, 5), "PRD-C", shipQty: 10m, unitPrice: 100m);

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        var row = report.Rows.Single(r => r.ProductCode == "PRD-C");
        Assert.Equal(0m,    row.Cost);
        Assert.Equal(1000m, row.GrossProfit);
    }

    // ── 毛利率計算 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_GrossMarginRate_CalculatedCorrectly()
    {
        // Revenue=10000, Cost=7500 → GrossProfit=2500, Rate=25.00%
        SeedShipment("GS005", new DateOnly(2026, 6, 1), "PRD-D", shipQty: 100m, unitPrice: 100m);
        SeedStockMovement("PRD-D", new DateOnly(2026, 6, 1), totalCost: 7500m);

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        var row = report.Rows.Single(r => r.ProductCode == "PRD-D");
        Assert.Equal(25.00m, row.GrossMarginRate);
        Assert.Equal(25.00m, report.TotalMarginRate);
    }

    [Fact]
    public async Task GetReport_ZeroRevenue_MarginRateZero()
    {
        // BVA：revenue=0 時不得除以零，毛利率應為 0
        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        Assert.Equal(0m, report.TotalMarginRate);
    }

    // ── 日期區間過濾 ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetReport_DateRangeFilter_OnlyIncludesInRange()
    {
        SeedShipment("GS006", new DateOnly(2026, 5, 31), "PRD-E", 10m, 100m);  // 範圍外
        SeedShipment("GS007", new DateOnly(2026, 6, 1),  "PRD-F", 20m, 100m);  // 範圍內

        var report = await _svc.GetReportAsync(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), PlantId);

        Assert.Single(report.Rows);
        Assert.Equal("PRD-F", report.Rows[0].ProductCode);
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────

    private void SeedShipment(string shipId, DateOnly date, string productCode,
        decimal shipQty, decimal unitPrice, DocStatus status = DocStatus.Posted)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = shipId,
            ShipDate        = date,
            CompanyCode     = "CUST001",
            TypeCode        = "GS",
            PlantId         = PlantId,
            CreatedBy       = "test",
            Status          = status,
        };
        var lineAmt = shipQty * unitPrice;
        gs.Lines.Add(new GoodsShipmentLine
        {
            LineNo      = 1,
            ProductCode = productCode,
            ShipQty     = shipQty,
            UnitPrice   = unitPrice,
            LineAmount  = lineAmt,
            TotalAmount = lineAmt,
        });
        gs.TotalAmount = lineAmt;
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
    }

    private void SeedStockMovement(string productCode, DateOnly date, decimal totalCost, string docNo = "")
    {
        _context.StockMovements.Add(new StockMovement
        {
            MovementId   = Guid.NewGuid(),
            DocNo        = docNo,
            TypeCode     = "GS",
            Direction    = -1,
            ProductCode  = productCode,
            MovementDate = date,
            Qty          = 1m,
            TotalCost    = totalCost,
            PlantId      = PlantId,
        });
        _context.SaveChanges();
    }
}
