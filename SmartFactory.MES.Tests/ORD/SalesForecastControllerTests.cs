using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ORD;

/// <summary>
/// 銷售預測（2026-10-27 驗收回報：只賣過兩個月的品號，預測值等於「12月均值」，而均值本身高估 6 倍）。
/// 「今天」固定在 2026-10-27：窗口＝2025-10 ～ 2026-09 共 12 個完整月份，預測從 2026-10 起。
/// </summary>
public class SalesForecastControllerTests : TestBase
{
    private const string PlantId = "P001";
    private int _seq;

    public SalesForecastControllerTests() => _context.CurrentPlantId = PlantId;

    private SalesForecastController Build()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(PlantId);
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.Now).Returns(new DateTime(2026, 10, 27, 9, 0, 0));
        return new SalesForecastController(_context, user.Object, perm.Object,
            new Mock<IExportService>().Object, dt.Object, GetMockLocalizer<ProjectZSharedResource>().Object);
    }

    private void Sale(string product, int year, int month, decimal lineAmount, decimal rate = 1m,
                      DocumentApprovalStatus status = DocumentApprovalStatus.Approved)
    {
        var id = $"SO-{++_seq}";
        _context.SalesOrders.Add(new SalesOrder
        {
            SalesOrderId = id, OrderDate = new DateOnly(year, month, 15), CompanyCode = "C1",
            PlantId = PlantId, ApprovalStatus = status, CreatedBy = "u"
        });
        _context.SalesOrderLines.Add(new SalesOrderLine
        {
            SalesOrderId = id, LineNo = 1, ProductCode = product, OrderQty = 1, UomCode = "PCS",
            LineAmount = lineAmount, ExchangeRate = rate, DeliveryDate = new DateOnly(year, month, 28)
        });
        _context.SaveChanges();
    }

    private async Task<List<SalesForecastRowVm>> RowsAsync(int months = 3)
    {
        var view = Assert.IsType<ViewResult>(await Build().Index(null, months, searched: true));
        return Assert.IsAssignableFrom<List<SalesForecastRowVm>>(view.Model);
    }

    [Fact]
    public async Task 只賣過兩個月_均值除以12_無季節係數_預測從本月起()
    {
        Sale("P", 2026, 5, 975m);
        Sale("P", 2026, 9, 225m);

        var rows = await RowsAsync();

        var actual = rows.Where(r => !r.IsForecast).ToList();
        Assert.Equal(12, actual.Count);                                  // 沒賣的月份補 0，不是被略過
        Assert.Equal("2025-10", actual.First().YearMonth);
        Assert.Equal("2026-09", actual.Last().YearMonth);
        Assert.All(actual, r => Assert.Equal(100m, r.MovingAvg));       // (975+225)/12
        Assert.All(rows, r => Assert.Null(r.SeasonalIndex));            // 銷售史未滿一年

        var fc = rows.Where(r => r.IsForecast).ToList();
        Assert.Equal(new[] { "2026-10", "2026-11", "2026-12" }, fc.Select(r => r.YearMonth));
        Assert.All(fc, r => Assert.Equal(100m, r.ForecastAmount));
    }

    [Fact]
    public async Task 銷售史滿一年_套用去年同月季節係數()
    {
        Sale("P", 2025, 10, 600m);       // 窗口第一個月就有銷售 → 有季節係數
        Sale("P", 2026, 3, 600m);

        var rows = await RowsAsync(2);

        var oct = rows.Single(r => r.IsForecast && r.YearMonth == "2026-10");
        Assert.Equal(6m, oct.SeasonalIndex);             // 600 / (1200/12)
        Assert.Equal(600m, oct.ForecastAmount);
        var nov = rows.Single(r => r.IsForecast && r.YearMonth == "2026-11");
        Assert.Equal(0m, nov.SeasonalIndex);             // 去年 11 月沒賣
        Assert.Equal(0m, nov.ForecastAmount);
    }

    [Fact]
    public async Task 金額取本幣_本月未完的訂單與駁回草稿不計()
    {
        Sale("P", 2026, 9, 10m, rate: 30m);                                       // 外幣 10 × 30 = 300
        Sale("P", 2026, 10, 9999m);                                               // 本月（未完整）
        Sale("P", 2026, 8, 9999m, status: DocumentApprovalStatus.Rejected);
        Sale("P", 2026, 8, 9999m, status: DocumentApprovalStatus.Draft);

        var rows = await RowsAsync(1);

        Assert.Equal(300m, rows.Single(r => r.YearMonth == "2026-09").ActualAmount);
        Assert.Equal(0m, rows.Single(r => !r.IsForecast && r.YearMonth == "2026-08").ActualAmount);
        Assert.Equal(25m, rows.First().MovingAvg);                                // 300 / 12
    }
}
