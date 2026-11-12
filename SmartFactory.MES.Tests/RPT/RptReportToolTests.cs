using SmartFactory.MES.Tests.Shared;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Reporting;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Tests.RPS;
using Xunit;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// RPT 批次 2 的六支報表工具（2026-09-15）。
///
/// <para>這一批與前十一支不同：**查詢邏輯早就在 service 層**，工具只做三件事——
/// 期間解算、把 service 的回應接上排程契約、宣告匯出欄位。所以這裡不重測查詢，
/// 測的是那三件事各自會怎麼壞：</para>
///
/// <list type="number">
/// <item><b>包裝型別攤不平</b>：六支的回應全是包裝型別（Rows／Lines ＋ 合計），
/// 取錯層就是一份**格式正確但零筆**的報表——沒有例外、沒有日誌。
/// 2026-09-14 Wave R 實測撞過這個形狀。</item>
/// <item><b>期別對應算錯</b>：損益表與資產負債表收的是會計期別字串，
/// 用 <c>yyyyMM</c> 硬格式化會在會計行事曆不等於日曆月時安靜查到隔壁期。</item>
/// <item><b>互動式參數被偷偷換掉</b>：維度、變動成本分類這些畫面上的選擇，
/// 排程沒有人在那一刻做——傳什麼進去必須是明寫的決定，而不是預設值碰巧。</item>
/// </list>
/// </summary>
public class RptReportToolTests : TestBase
{
    private const string PlantId = "P001";

    private readonly Mock<IRelativePeriodResolver> _period = new();
    private static readonly DateRange Window =
        new(new DateOnly(2026, 6, 10), new DateOnly(2026, 7, 20));

    public RptReportToolTests()
    {
        // AccountingPeriod 是 IPlantEntity，受全域廠別過濾器管轄。不設這個值，
        // 種下去的期間查不回來——而 FiscalPeriodMapper 刻意**不**繞過過濾器
        // （要的是本廠的會計行事曆），所以測試也不該繞過。
        _context.CurrentPlantId = PlantId;

        // 六支工具都先解算期間再呼叫 service；這裡固定回一個跨兩期的視窗。
        _period.Setup(p => p.ResolveAsync(
                It.IsAny<ReportRequestBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Window);
    }

    private void SeedPeriods(params (int Year, int Month)[] periods)
    {
        foreach (var (y, m) in periods)
        {
            var start = new DateOnly(y, m, 1);
            _context.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId  = $"{y}{m:00}",
                PlantId   = PlantId,
                Year      = y,
                Month     = m,
                StartDate = start,
                EndDate   = start.AddMonths(1).AddDays(-1),
            });
        }
        _context.SaveChanges();
    }

    private static IStringLocalizer<ProjectZReportHeaderResource> Headers()
        => ReportHeaderLocalizer.Create();

    // ════════════════════════════════════════════════════════════════
    // 一、期別對應
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task 期別對應取區間涵蓋到的頭尾兩期()
    {
        SeedPeriods((2026, 5), (2026, 6), (2026, 7), (2026, 8));

        var (from, to) = await new FiscalPeriodMapper(_context).RangeAsync(Window);

        // 視窗是 6/10~7/20：兩端都只涵蓋半個月，但半個月也算涵蓋到那一期。
        Assert.Equal("202606", from);
        Assert.Equal("202607", to);
    }

    /// <summary>
    /// 會計行事曆**不等於**日曆月時仍取到正確期別。
    ///
    /// <para>這一條才是 <see cref="FiscalPeriodMapper"/> 存在的理由。上一條用的是
    /// 標準月曆，那種情況下拿 <c>range.From.ToString("yyyyMM")</c> 硬湊也會**剛好對**——
    /// 所以那條擋不住「用字串格式化代替查表」這個實作。這裡用 26 日起算的行事曆：
    /// 視窗 6/10~7/20 落在 202605（5/26–6/25）與 202606（6/26–7/25）兩期，
    /// 而字串格式化會答出 202606／202607，**整整差一期**。</para>
    /// </summary>
    [Fact]
    public async Task 會計行事曆不等於日曆月時仍取到正確期別()
    {
        // 26 日起算：202605 = 5/26~6/25、202606 = 6/26~7/25、202607 = 7/26~8/25
        foreach (var (id, y, m, sy, sm, sd, ey, em, ed) in new[]
        {
            ("202605", 2026, 5, 2026, 5, 26, 2026, 6, 25),
            ("202606", 2026, 6, 2026, 6, 26, 2026, 7, 25),
            ("202607", 2026, 7, 2026, 7, 26, 2026, 8, 25),
        })
        {
            _context.AccountingPeriods.Add(new AccountingPeriod
            {
                PeriodId  = id, PlantId = PlantId, Year = y, Month = m,
                StartDate = new DateOnly(sy, sm, sd),
                EndDate   = new DateOnly(ey, em, ed),
            });
        }
        _context.SaveChanges();

        var (from, to) = await new FiscalPeriodMapper(_context).RangeAsync(Window);

        Assert.Equal("202605", from);
        Assert.Equal("202606", to);
        Assert.Equal("202606", await new FiscalPeriodMapper(_context).AsOfAsync(Window));
    }

    [Fact]
    public async Task 截至某期取的是迄日那一期不是起日那一期()
    {
        SeedPeriods((2026, 6), (2026, 7));

        Assert.Equal("202607", await new FiscalPeriodMapper(_context).AsOfAsync(Window));
    }

    /// <summary>
    /// 查無涵蓋期間時**拋例外**，不退回日曆月。
    /// 退回日曆月會寄出一份看起來正常、口徑卻與帳務不符的報表——那比收到錯誤訊息糟。
    /// </summary>
    [Fact]
    public async Task 查無會計期間時拋例外而不是退回日曆月()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FiscalPeriodMapper(_context).RangeAsync(Window));

        PzAssert.Message("Rpt_NoPeriodOverlapFmt", ex.Message);
        Assert.Contains("2026-06-10~2026-07-20", ex.Message);
    }

    // ════════════════════════════════════════════════════════════════
    // 二、包裝型別要攤成列（六支各一條）
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task 毛利分析攤出的是列不是包裝型別()
    {
        var svc = new Mock<IGrossMarginService>();
        svc.Setup(s => s.GetReportAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GrossMarginReport
            {
                Rows = { new GrossMarginRow { ProductCode = "P-1", Revenue = 100m, Cost = 60m, ShipCount = 2 } }
            });

        IReportToolDescriptor tool = new GrossMarginTool(svc.Object, _period.Object);
        var rows = await tool.GetExportRowsAsync(new GrossMarginRequest { PlantId = PlantId }, null);

        var row = Assert.IsType<GrossMarginRow>(Assert.Single(rows));
        Assert.Equal("P-1", row.ProductCode);
    }

    [Fact]
    public async Task 損益表攤出的是科目列()
    {
        SeedPeriods((2026, 6), (2026, 7));
        var svc = new Mock<IProfitLossService>();
        svc.Setup(s => s.GetReportAsync("202606", "202607", PlantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfitLossReport
            {
                Lines = { new ProfitLossLine { AccountCode = "4100", AccountName = "銷貨收入", Amount = 500m } }
            });

        IReportToolDescriptor tool = new ProfitLossTool(
            svc.Object, _period.Object, new FiscalPeriodMapper(_context));
        var rows = await tool.GetExportRowsAsync(new ProfitLossRequest { PlantId = PlantId }, null);

        Assert.Equal("4100", Assert.IsType<ProfitLossLine>(Assert.Single(rows)).AccountCode);
        // 期別是對應出來的，不是字串格式化——service 收到的必須是這兩個 PeriodId。
        svc.Verify(s => s.GetReportAsync("202606", "202607", PlantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 委外績效攤出的是廠商列()
    {
        var svc = new Mock<IOutsourcePerformanceService>();
        svc.Setup(s => s.GetReportAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutsourcePerformanceReport
            {
                Rows = { new OutsourcePerformanceRow { SupplierCode = "S-1", SupplierName = "供應商一" } }
            });

        IReportToolDescriptor tool = new OutsourcePerformanceTool(svc.Object, _period.Object);
        var rows = await tool.GetExportRowsAsync(new OutsourcePerformanceRequest { PlantId = PlantId }, null);

        Assert.Equal("S-1", Assert.IsType<OutsourcePerformanceRow>(Assert.Single(rows)).SupplierCode);
    }

    // ════════════════════════════════════════════════════════════════
    // 三、互動式參數：排程傳什麼必須是明寫的決定
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null,       "Product")]   // 未指定 → 照抄畫面 Export 的預設值
    [InlineData("",         "Product")]   // 空白同上，不把空字串傳給 service
    [InlineData("Customer", "Customer")]
    [InlineData("Month",    "Month")]
    public async Task 毛利維度走GroupBy未指定時用畫面的預設值(string? groupBy, string expected)
    {
        var svc = new Mock<IMarginAnalysisService>();
        svc.Setup(s => s.GetReportAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MarginAnalysisReport());

        var tool = new MarginAnalysisTool(svc.Object, _period.Object);
        await tool.ExecuteAsync(new MarginAnalysisRequest { PlantId = PlantId, GroupBy = groupBy }, null);

        svc.Verify(s => s.GetReportAsync(
            It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId, expected,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 邊際貢獻的變動成本分類**一律用系統預設**（<c>policyOverride: null</c>）。
    /// 畫面那三個核取方塊是「這一次想怎麼看」，排程沒有人在那一刻做這個選擇；
    /// 若哪天改成存進排程設定，這一條會紅，而那正是應該被看見的改變。
    /// </summary>
    [Fact]
    public async Task 邊際貢獻的變動成本分類一律用系統預設()
    {
        var svc = new Mock<IContributionMarginService>();
        svc.Setup(s => s.BuildAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId,
                It.IsAny<ContributionMarginCalculator.VariablePolicy?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContributionMarginReport());

        var tool = new ContributionMarginTool(svc.Object, _period.Object);
        await tool.ExecuteAsync(new ContributionMarginRequest { PlantId = PlantId }, null);

        svc.Verify(s => s.BuildAsync(
            It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), PlantId,
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ════════════════════════════════════════════════════════════════
    // 四、資產負債表：兩層攤平、類別在地化
    // ════════════════════════════════════════════════════════════════

    private BalanceSheetTool BalanceSheet(Mock<IBalanceSheetService> svc)
        => new(svc.Object, _period.Object, new FiscalPeriodMapper(_context), Headers());

    private static Mock<IBalanceSheetService> BalanceSheetService(params (string Category, string Code)[] items)
    {
        var svc = new Mock<IBalanceSheetService>();
        var report = new BalanceSheetReport();
        foreach (var group in items.GroupBy(i => i.Category))
        {
            var section = new BalanceSheetSection { Category = group.Key };
            foreach (var i in group)
                section.Items.Add(new BalanceSheetItem { AccountCode = i.Code, AccountName = i.Code, Balance = 1m });
            report.Sections.Add(section);
        }
        svc.Setup(s => s.GetReportAsync(It.IsAny<string>(), PlantId, It.IsAny<CancellationToken>()))
           .ReturnsAsync(report);
        return svc;
    }

    [Fact]
    public async Task 資產負債表把分區攤平且類別跟著每一列()
    {
        SeedPeriods((2026, 6), (2026, 7));
        var svc = BalanceSheetService(("Assets", "1101"), ("Assets", "1102"), ("Liabilities", "2101"));

        IReportToolDescriptor tool = BalanceSheet(svc);
        var rows = (await tool.GetExportRowsAsync(new BalanceSheetRequest { PlantId = PlantId }, null))
            .Cast<BalanceSheetExportRow>().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Assets", "Assets", "Liabilities"], rows.Select(r => r.Category));
        Assert.Equal(["1101", "1102", "2101"], rows.Select(r => r.Item.AccountCode));

        // 「截至」取的是迄日那一期
        svc.Verify(s => s.GetReportAsync("202607", PlantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 資產負債表的類別欄在兩個語系各自在地化()
    {
        SeedPeriods((2026, 7));
        var tool = BalanceSheet(BalanceSheetService(("Assets", "1101")));
        var column = tool.GetExportColumns(new BalanceSheetRequest()).First();
        var row = new BalanceSheetExportRow("Assets", new BalanceSheetItem());

        Assert.Equal("資產",  ValueUnder("zh-TW", () => column.ValueOf(row)));
        Assert.Equal("Assets", ValueUnder("en-US", () => column.ValueOf(row)));
    }

    /// <summary>
    /// 未知的類別碼**原樣輸出**，不輸出資源鍵。
    /// <c>IStringLocalizer</c> 查不到鍵時會靜默回傳鍵本身，匯出檔就會冒出
    /// `BalanceSheet.CategoryXxx` 這種字串——原始碼至少還看得懂。
    /// </summary>
    [Fact]
    public async Task 未知類別碼原樣輸出而不是資源鍵()
    {
        SeedPeriods((2026, 7));
        var tool = BalanceSheet(BalanceSheetService(("Assets", "1101")));
        var column = tool.GetExportColumns(new BalanceSheetRequest()).First();

        var value = ValueUnder("zh-TW",
            () => column.ValueOf(new BalanceSheetExportRow("Contingent", new BalanceSheetItem())));

        Assert.Equal("Contingent", value);
    }

    private static object? ValueUnder(string culture, Func<object?> f)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture =
            System.Globalization.CultureInfo.GetCultureInfo(culture);
        try { return f(); }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }
}
