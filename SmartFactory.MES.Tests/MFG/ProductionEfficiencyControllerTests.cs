using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 生產效率分析：分母改用實際報工時段（2026-10-27）。
/// 原本用排程起訖，提早開工的派工完工時結束早於開始，排程工時出現負數。
/// </summary>
public class ProductionEfficiencyControllerTests : TestBase
{
    private const string PlantId = "P001";
    private readonly Mock<IDateTimeService> _dt = new();

    public ProductionEfficiencyControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        // 廠別在 UTC+8：當地日 00:00 = 前一天 16:00 UTC
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 10, 27));
        _dt.Setup(d => d.ToUtcTime(It.IsAny<DateTime>())).Returns<DateTime>(x => x.AddHours(-8));
    }

    private ProductionEfficiencyController Build()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(PlantId);
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        return new ProductionEfficiencyController(_context, user.Object, perm.Object,
            new Mock<IExportService>().Object, _dt.Object, GetMockLocalizer<ProjectZSharedResource>().Object);
    }

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>標準工時 = 1 分/件 × 數量。排程時段刻意給在實際之後（提早開工）。</summary>
    private void SeedDispatch(string id, decimal qty, params (DateTime Start, DateTime End)[] sessions)
    {
        _context.WorkOrderOperations.Add(new WorkOrderOperation
        {
            WorkOrderId = "WO-" + id, OperationSeq = 10, OperationName = "OP10", WorkCenterId = "WC1",
            UnitTime = 1, TimeUnit = OperationTimeUnit.Minute
        });
        _context.Dispatches.Add(new Dispatch
        {
            DispatchId = id, WorkOrderId = "WO-" + id, OperationSeq = 10, PlantId = PlantId, Quantity = qty,
            Status = DispatchStatus.Closed,
            ScheduledStart = Utc(14, 9), ScheduledEnd = sessions.Max(s => s.End),
        });
        var n = 0;
        foreach (var (s, e) in sessions)
            _context.ProductionSessions.Add(new ProductionSession
            {
                SessionId = $"{id}-{++n}", DispatchId = id, WorkOrderId = "WO-" + id, PlantId = PlantId,
                StartAt = s, EndAt = e, Status = ProductionSessionStatus.Closed
            });
        _context.SaveChanges();
    }

    private async Task<ProductionEfficiencyRowVm> RowAsync(string from, string to)
    {
        var view = Assert.IsType<ViewResult>(await Build().Index(from, to, workCenterId: null, searched: true));
        return Assert.Single(Assert.IsAssignableFrom<List<ProductionEfficiencyRowVm>>(view.Model));
    }

    [Fact]
    public async Task 提早開工的派工_用實際上工時段_不會是負數()
    {
        // 排程在 10/14，10/09 就做完：60 分鐘、標準 60 分鐘
        SeedDispatch("D1", 60, (Utc(9, 1), Utc(9, 2)));

        var row = await RowAsync("2026-10-09", "2026-10-09");

        Assert.Equal(60m, row.ActualMinutes);
        Assert.Equal(100m, row.EfficiencyRate);
    }

    [Fact]
    public async Task 多人重疊只算一次_中斷不算()
    {
        // 兩人 01:00–02:00、01:30–02:30 重疊 → 90 分；休息後 03:00–03:30 再 30 分
        SeedDispatch("D1", 60, (Utc(9, 1), Utc(9, 2)), (Utc(9, 1, 30), Utc(9, 2, 30)), (Utc(9, 3), Utc(9, 3, 30)));

        var row = await RowAsync("2026-10-09", "2026-10-09");

        Assert.Equal(120m, row.ActualMinutes);
        Assert.Equal(50m, row.EfficiencyRate);
    }

    [Fact]
    public async Task 期間以完工時刻的廠別當地日為準()
    {
        // 10/09 17:00 UTC = 當地 10/10 01:00 完工 → 屬於 10/10，不屬於 10/09
        SeedDispatch("D1", 60, (Utc(9, 16), Utc(9, 17)));

        var view = Assert.IsType<ViewResult>(await Build().Index("2026-10-09", "2026-10-09", null, searched: true));
        Assert.Empty(Assert.IsAssignableFrom<List<ProductionEfficiencyRowVm>>(view.Model));

        var row = await RowAsync("2026-10-10", "2026-10-10");
        Assert.Equal(60m, row.ActualMinutes);
    }
}
