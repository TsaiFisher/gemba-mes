using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.RPT.Controllers;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>
/// 合併財報畫面。
///
/// <para>這支畫面的危險形狀不是壞掉，是**看起來沒壞**：廠別漏掉一個、或全域廠別過濾
/// 把別廠的分錄濾掉，畫面照樣渲染出一張排版完整的報表，只是數字少了一廠。
/// 所以這裡守的是「誰被算進去」與「使用者看不看得出自己在看什麼狀態的數字」。</para>
/// </summary>
public class ConsolidatedStatementControllerTests : TestBase
{
    private const string PlantA = "P001";
    private const string PlantB = "P002";

    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();
    private readonly Mock<ICurrencyService>   _cur  = new();
    private readonly Mock<IExportService>     _export;
    private ExportRequest? _exported;
    private int _seq;

    public ConsolidatedStatementControllerTests()
    {
        // 操作者站在 A 廠：合併畫面必須看得到 B 廠，否則「合併」等於「本廠」。
        _context.CurrentPlantId = PlantA;
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 9, 22, 9, 0, 0));
        _cur.Setup(c => c.GetBaseCurrencyAsync()).ReturnsAsync("TWD");
        _cur.Setup(c => c.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["TWD"] = 0 });

        _export = GetMockExportService();
        _export.Setup(e => e.ToExcel(It.IsAny<ExportRequest>()))
               .Callback<ExportRequest>(r => _exported = r)
               .Returns(Array.Empty<byte>());

        _context.Plants.Add(new Plant { Id = PlantA, Name = "台北廠" });
        _context.Plants.Add(new Plant { Id = PlantB, Name = "台中廠" });
        _context.SaveChanges();
    }

    private ConsolidatedStatementController New()
    {
        var classifier = new Modules.BAS.Services.AccountClassifier(_context);
        return new ConsolidatedStatementController(
            _context,
            new BalanceSheetService(_context, classifier),
            new ProfitLossService(_context, classifier),
            _perm.Object,
            _export.Object,
            _dt.Object,
            _cur.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object)
        { ControllerContext = GetControllerContextWithServices() };
    }

    private void Seed(string plantId, string periodId,
        params (string Code, string Name, decimal Debit, decimal Credit)[] lines)
    {
        var entry = new JournalEntry
        {
            JournalId     = $"JE{++_seq:D5}",
            PeriodId      = periodId,
            EntryDate     = new DateOnly(int.Parse(periodId[..4]), int.Parse(periodId[4..]), 1),
            SourceDocType = "Test",
            PlantId       = plantId,
        };
        var no = 1;
        foreach (var l in lines)
            entry.Lines.Add(new JournalLine
            {
                LineNo = no++, AccountCode = l.Code, AccountName = l.Name,
                Debit = l.Debit, Credit = l.Credit, AmountLocal = l.Debit + l.Credit,
            });
        _context.JournalEntries.Add(entry);
        _context.SaveChanges();
    }

    private void SeedPeriod(string plantId, string periodId, PeriodStatus status)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = periodId, PlantId = plantId,
            Year = int.Parse(periodId[..4]), Month = int.Parse(periodId[4..]),
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 1, 31),
            Status = status,
        });
        _context.SaveChanges();
    }

    private static ConsolidatedStatementVm? ModelOf(IActionResult r)
        => Assert.IsType<ViewResult>(r).Model as ConsolidatedStatementVm;

    // ── 涵蓋範圍 ────────────────────────────────────────────────

    [Fact]
    public async Task 預設納入全部廠別而不是操作者本廠()
    {
        Seed(PlantA, "202601", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));
        Seed(PlantB, "202601", ("1100", "現金", 250m, 0m), ("3100", "股本", 0m, 250m));

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.Equal(350m, vm.BalanceSheet!.TotalAssets);
        Assert.Equal(new[] { PlantA, PlantB }, vm.Plants.Select(p => p.PlantId));
    }

    [Fact]
    public async Task 只勾一個廠時其他廠不得混進來()
    {
        Seed(PlantA, "202601", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));
        Seed(PlantB, "202601", ("1100", "現金", 250m, 0m), ("3100", "股本", 0m, 250m));

        var vm = ModelOf(await New().Index("202601", null, new[] { PlantB }, searched: true))!;

        Assert.Equal(250m, vm.BalanceSheet!.TotalAssets);
        Assert.Equal(PlantB, Assert.Single(vm.Plants).PlantId);
    }

    [Fact]
    public async Task 不存在的廠別代碼被忽略而不是查出空報表()
    {
        // 網址列可以手打 plantIds；未知代碼若原樣帶進查詢，得到的是一張全 0 的報表，
        // 而使用者看不出那是「這一廠沒有資料」還是「這一廠不存在」。
        Seed(PlantA, "202601", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));

        var vm = ModelOf(await New().Index("202601", null, new[] { "P999" }, searched: true));

        Assert.Null(vm);   // 有效廠別為空集合 → 回到「請重新選擇」
    }

    // ── 在途存貨 ────────────────────────────────────────────────

    [Fact]
    public async Task 已撥出未收貨時在途存貨等於廠際往來殘值()
    {
        const string Clearing = Modules.ACT.StandardAccounts.InterPlantClearingCode;
        Seed(PlantA, "202601", (Clearing, "廠際調撥往來", 800m, 0m), ("1401", "原料存貨", 0m, 800m));

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.Equal(800m, vm.InterPlantInTransit);
    }

    [Fact]
    public async Task 兩腿都做完時在途存貨為零()
    {
        const string Clearing = Modules.ACT.StandardAccounts.InterPlantClearingCode;
        Seed(PlantA, "202601", (Clearing, "廠際調撥往來", 800m, 0m), ("1401", "原料存貨", 0m, 800m));
        Seed(PlantB, "202601", ("1401", "原料存貨", 800m, 0m), (Clearing, "廠際調撥往來", 0m, 800m));

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.Equal(0m, vm.InterPlantInTransit);
    }

    // ── 各廠期間狀態 ────────────────────────────────────────────

    [Fact]
    public async Task 各廠關帳進度不同時要標記出來()
    {
        Seed(PlantA, "202601", ("1100", "現金", 100m, 0m), ("3100", "股本", 0m, 100m));
        SeedPeriod(PlantA, "202601", PeriodStatus.Closed);
        SeedPeriod(PlantB, "202601", PeriodStatus.Open);

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.True(vm.HasMixedCloseStatus);
        Assert.Equal(PeriodStatus.Closed, vm.Plants.Single(p => p.PlantId == PlantA).Status);
        Assert.Equal(PeriodStatus.Open,   vm.Plants.Single(p => p.PlantId == PlantB).Status);
    }

    [Fact]
    public async Task 沒建期間的廠別狀態留空不得補成開帳中()
    {
        // 「沒建期間」與「已開帳」在治理上不同：前者連過帳閘門都不生效。
        SeedPeriod(PlantA, "202601", PeriodStatus.Closed);

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.Equal(PeriodStatus.Closed, vm.Plants.Single(p => p.PlantId == PlantA).Status);
        Assert.Null(vm.Plants.Single(p => p.PlantId == PlantB).Status);
    }

    [Fact]
    public async Task 各廠狀態一致時不出現提醒()
    {
        SeedPeriod(PlantA, "202601", PeriodStatus.Closed);
        SeedPeriod(PlantB, "202601", PeriodStatus.Closed);

        var vm = ModelOf(await New().Index("202601", null, null, searched: true))!;

        Assert.False(vm.HasMixedCloseStatus);
    }

    // ── 期間清單與損益口徑 ──────────────────────────────────────

    [Fact]
    public async Task 期間清單要列全公司的期別不是只列本廠的()
    {
        // 只列本廠的話，操作者站在 A 廠時選不到 B 廠才建的期間——
        // 而那正是合併報表最需要看的那一期。
        SeedPeriod(PlantB, "202602", PeriodStatus.Open);

        var r = await New().Index(null, null, null, searched: false);
        var periods = Assert.IsType<ViewResult>(r).ViewData["PeriodOptions"] as List<string>;

        Assert.Contains("202602", periods!);
    }

    [Fact]
    public async Task 指定損益起期時本期損益仍以有史以來為準()
    {
        // 資產負債表不做期末結轉，差額是「有史以來的損益」；
        // 若這裡跟著區間走，等式會對不起來而且沒有任何提示。
        Seed(PlantA, "202601", ("4100", "銷貨收入", 0m, 100m), ("1200", "應收帳款", 100m, 0m));
        Seed(PlantA, "202602", ("4100", "銷貨收入", 0m,  40m), ("1200", "應收帳款",  40m, 0m));

        var vm = ModelOf(await New().Index("202602", "202602", null, searched: true))!;

        Assert.Equal(40m,  vm.ProfitLoss!.Revenue);   // 區間損益
        Assert.Equal(140m, vm.NetProfit);             // 累計損益
    }

    // ── 權限 ────────────────────────────────────────────────────

    [Fact]
    public async Task 沒有讀取權限時擋掉()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(await New().Index("202601", null, null, searched: true));
    }

    [Fact]
    public async Task 沒有列印權限時匯不出來()
    {
        // 匯出等於把全公司金額帶出系統，閘門比 Index 更該守。
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);

        Assert.IsType<ForbidResult>(await New().Export("202601", null, null));
    }

    // ── 匯出 ────────────────────────────────────────────────────

    [Fact]
    public async Task 匯出同時帶出兩張報表且涵蓋全部廠別()
    {
        // 匯出走的是另一條路徑，不吃畫面的 ViewModel——漏掉一張表或漏掉一個廠
        // 在檔案裡看不出來，因為少的那幾列本來就不會出現。
        Seed(PlantA, "202601", ("1100", "現金", 100m, 0m), ("4100", "銷貨收入", 0m, 100m));
        Seed(PlantB, "202601", ("1100", "現金", 250m, 0m), ("4100", "銷貨收入", 0m, 250m));

        await New().Export("202601", null, null);

        Assert.NotNull(_exported);
        var statements = _exported!.Rows.Select(r => (string)r[0]!).Distinct().ToList();
        Assert.Equal(2, statements.Count);                       // 資產負債表＋損益表

        var cash = _exported.Rows.Single(r => (string)r[2]! == "1100");
        Assert.Equal(350m, (decimal)cash[4]!);                   // 兩廠都算進去
    }
}
