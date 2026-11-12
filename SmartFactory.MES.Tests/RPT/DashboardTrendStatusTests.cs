using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.RPT.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// 儀表板年度趨勢的**單據狀態範圍**。
///
/// 兩條趨勢原本都只認 <c>DocStatus.Confirmed</c>，但單據過帳完成後停在 <c>Posted</c>
/// （出貨與進貨皆然），於是「真正完成的交易」反而不進趨勢圖，圖上只剩下確認了但還沒
/// 過帳的那些。同一個錯誤在 GrossMargin／ContributionMargin／MarginAnalysis 也有一份，
/// 由 `Flows/ReportReconciliationFlowTests` 從報表對單據明細那一側守住；
/// 儀表板的年度篩選綁在 <c>DateTime.UtcNow.Year</c>、不吃參數，故在這裡單獨釘。
///
/// Draft 與 Void 仍必須排除——這是本檔的另一半，否則「把狀態放寬」很容易一路放到全收。
/// </summary>
public class DashboardTrendStatusTests : TestBase
{
    private const string PlantId = "P001";

    private readonly DashboardService _svc;
    private static readonly int ThisYear = DateTime.UtcNow.Year;

    public DashboardTrendStatusTests()
    {
        _context.CurrentPlantId = PlantId;
        var clock = new Moq.Mock<SmartFactory.MES.Services.IDateTimeService>();
        clock.Setup(x => x.GetLocalDate(null)).Returns(new DateOnly(ThisYear, 6, 15));
        clock.Setup(x => x.ToUtcTime(Moq.It.IsAny<DateTime>())).Returns<DateTime>(x => DateTime.SpecifyKind(x, DateTimeKind.Utc));
        _svc = new DashboardService(_context, clock.Object);
    }

    private void SeedShipment(string id, DocStatus status, decimal total, int month)
    {
        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = id, ShipDate = new DateOnly(ThisYear, month, 15),
            CompanyCode = "C001", CreatedBy = "u1", PlantId = PlantId,
            Status = status, TotalAmount = total,
        });
        _context.SaveChanges();
    }

    private void SeedReceipt(string id, DocStatus status, decimal total, int month)
    {
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = id, ReceiptDate = new DateOnly(ThisYear, month, 15),
            SupplierCode = "F001", CreatedBy = "u1", PlantId = PlantId,
            Status = status, TotalAmount = total,
        });
        _context.SaveChanges();
    }

    /// <summary>趨勢點的 Month 是兩位數字串（DashboardService.MonthLabel）。</summary>
    private static decimal MonthAmount(IEnumerable<SmartFactory.MES.Modules.RPT.Models.MonthlyTrendPoint> trend, int month)
        => trend.Where(p => p.Month == month.ToString("00")).Sum(p => p.Amount);

    [Fact]
    public async Task RevenueTrend_IncludesPostedShipments_NotOnlyConfirmed()
    {
        SeedShipment("GS-P", DocStatus.Posted,    1000m, 3);
        SeedShipment("GS-C", DocStatus.Confirmed,  500m, 3);

        var kpi = await _svc.GetKpiAsync(PlantId);

        Assert.Equal(1500m, MonthAmount(kpi.RevenueTrend, 3));
    }

    [Fact]
    public async Task RevenueTrend_StillExcludesDraftAndVoid()
    {
        SeedShipment("GS-P2", DocStatus.Posted, 1000m, 4);
        SeedShipment("GS-D",  DocStatus.Draft,   700m, 4);
        SeedShipment("GS-V",  DocStatus.Void,    900m, 4);

        var kpi = await _svc.GetKpiAsync(PlantId);

        Assert.Equal(1000m, MonthAmount(kpi.RevenueTrend, 4));
    }

    [Fact]
    public async Task PurchaseTrend_IncludesPostedReceipts_NotOnlyConfirmed()
    {
        SeedReceipt("GR-P", DocStatus.Posted,    2000m, 5);
        SeedReceipt("GR-C", DocStatus.Confirmed,  300m, 5);

        var kpi = await _svc.GetKpiAsync(PlantId);

        Assert.Equal(2300m, MonthAmount(kpi.PurchaseTrend, 5));
    }

    [Fact]
    public async Task PurchaseTrend_StillExcludesDraftAndVoid()
    {
        SeedReceipt("GR-P2", DocStatus.Posted, 2000m, 6);
        SeedReceipt("GR-D",  DocStatus.Draft,   400m, 6);
        SeedReceipt("GR-V",  DocStatus.Void,    600m, 6);

        var kpi = await _svc.GetKpiAsync(PlantId);

        Assert.Equal(2000m, MonthAmount(kpi.PurchaseTrend, 6));
    }

    [Fact]
    public async Task Periods_UseInjectedLocalDate_AtYearBoundary()
    {
        // January in the plant may still be December in UTC. The period must follow the clock service.
        var clock = new Moq.Mock<SmartFactory.MES.Services.IDateTimeService>();
        clock.Setup(x => x.GetLocalDate(null)).Returns(new DateOnly(2031, 1, 1));
        clock.Setup(x => x.ToUtcTime(Moq.It.IsAny<DateTime>()))
            .Returns<DateTime>(x => DateTime.SpecifyKind(x.AddHours(-8), DateTimeKind.Utc));
        _context.GoodsShipments.AddRange(
            new GoodsShipment { GoodsShipmentId = "GS-NEW-YEAR", ShipDate = new DateOnly(2031, 1, 1),
                CompanyCode = "C001", CreatedBy = "u1", PlantId = PlantId, Status = DocStatus.Posted, TotalAmount = 100m },
            new GoodsShipment { GoodsShipmentId = "GS-OLD-YEAR", ShipDate = new DateOnly(2030, 12, 31),
                CompanyCode = "C001", CreatedBy = "u1", PlantId = PlantId, Status = DocStatus.Posted, TotalAmount = 900m });
        await _context.SaveChangesAsync();
        var result = await new DashboardService(_context, clock.Object).GetKpiAsync(PlantId);
        Assert.Equal(100m, result.MonthlyRevenue);
        Assert.Equal(100m, result.RevenueTrend.Sum(x => x.Amount));
        clock.Verify(x => x.ToUtcTime(new DateTime(2031, 1, 1)), Moq.Times.Once);
        clock.Verify(x => x.ToUtcTime(new DateTime(2031, 2, 1)), Moq.Times.Once);
    }
    [Fact]
    public async Task OnTimeRate_UsesPlantLocalMonth_NotUtcMonth()
    {
        // 台北（UTC+8）2031 年 1 月＝UTC 2030-12-31 16:00 ～ 2031-01-31 16:00。
        // WO-JAN：計畫完工 UTC 12/31 17:00（當地 1/1 01:00），準時 → 屬於本月
        // WO-FEB：計畫完工 UTC 1/31 17:00（當地 2/1 01:00），逾期 → 不屬於本月
        // 若誤用 UTC 月界，會算進逾期的 WO-FEB、漏掉準時的 WO-JAN，準時率變 0。
        var clock = new Moq.Mock<SmartFactory.MES.Services.IDateTimeService>();
        clock.Setup(x => x.GetLocalDate(null)).Returns(new DateOnly(2031, 1, 15));
        clock.Setup(x => x.ToUtcTime(Moq.It.IsAny<DateTime>()))
            .Returns<DateTime>(x => DateTime.SpecifyKind(x.AddHours(-8), DateTimeKind.Utc));
        _context.WorkOrders.AddRange(
            new WorkOrder { WorkOrderId = "WO-JAN", ProductCode = "P", PlantId = PlantId, Status = WorkOrderStatus.Closed,
                PlannedEndDate = new DateTime(2030, 12, 31, 17, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2030, 12, 31, 10, 0, 0, DateTimeKind.Utc) },
            new WorkOrder { WorkOrderId = "WO-FEB", ProductCode = "P", PlantId = PlantId, Status = WorkOrderStatus.Closed,
                PlannedEndDate = new DateTime(2031, 1, 31, 17, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2031, 2, 5, 0, 0, 0, DateTimeKind.Utc) });
        await _context.SaveChangesAsync();   // UpdatedAt 只在 Modified 時被覆寫，新增時保留設定值

        var result = await new DashboardService(_context, clock.Object).GetKpiAsync(PlantId);

        Assert.Equal(100m, result.WorkOrderOnTimeRate);
    }
}
