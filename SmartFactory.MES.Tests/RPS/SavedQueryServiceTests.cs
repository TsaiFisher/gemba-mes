using Moq;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 常用查詢（§7.9 L2）。
///
/// 這裡的重點不是 CRUD，而是三條「錯了不會有任何跡象」的邊界：
/// 只能看到自己的、權限失效要顯示而不是消失、以及**互動觸發一律用當下使用者**
/// 解析權限（絕不沿用 OwnerRoleId，否則就是權限提升管道）。
/// </summary>
public class SavedQueryServiceTests : TestBase
{
    private const string Me    = "user-me";
    private const string Other = "user-other";

    private readonly Mock<IUserContext>            _user    = new();
    private readonly Mock<IReportToolCatalog>      _catalog = new();
    private readonly Mock<IRelativePeriodResolver> _periods = new();

    public SavedQueryServiceTests()
    {
        _user.Setup(u => u.UserId).Returns(Me);
        _user.Setup(u => u.ActiveRoleId).Returns("role-1");
        _user.Setup(u => u.PlantId).Returns("P001");
        _user.Setup(u => u.TimeZoneId).Returns("Asia/Taipei");

        _catalog.Setup(c => c.IsAuthorizedAsync(It.IsAny<string>())).ReturnsAsync(true);

        _periods.Setup(p => p.ResolveAsync(It.IsAny<RelativePeriod>(), It.IsAny<string?>(),
                                           It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
    }

    private SavedQueryService Svc()
        => new(_context, _user.Object, _catalog.Object, _periods.Object);

    private static IReadOnlyDictionary<string, string?> Filters(
        params (string Key, string? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private ReportDefinition Seed(
        string owner, string name, string toolKey, string paramsJson, bool isActive = true)
    {
        var row = new ReportDefinition
        {
            Name        = name,
            OwnerUserId = owner,
            OwnerRoleId = "role-owner",
            ToolKey     = toolKey,
            ParamsJson  = paramsJson,
            IsActive    = isActive,
            CreatedAt   = DateTime.UtcNow,
        };
        _context.ReportDefinitions.Add(row);
        _context.SaveChanges();
        return row;
    }

    // ────────────────────────────────────────────────────────────────
    // 存檔
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 存檔_有日期區間的報表未給相對期間_擋下()
    {
        var r = await Svc().SaveAsync(new SavedQueryInput(
            "SalesRanking", "上月銷售", Period: null, Filters()));

        // §7.9-1：存絕對日期的「上月銷售排行」下個月點開還是上個月的資料，
        // 正好是常用查詢的反面。
        Assert.False(r.Success);
        Assert.Equal("SavedQuery_PeriodRequired", r.ErrorMessage);
        Assert.Empty(_context.ReportDefinitions);
    }

    [Fact]
    public async Task 存檔_無日期區間的報表不需要期間()
    {
        var r = await Svc().SaveAsync(new SavedQueryInput(
            "StockShortage", "缺料清單", Period: null, Filters(("searchProduct", "A-100"))));

        Assert.True(r.Success);
        Assert.Single(_context.ReportDefinitions);
    }

    [Fact]
    public async Task 存檔_白名單外的參數被丟棄()
    {
        await Svc().SaveAsync(new SavedQueryInput(
            "StockShortage", "缺料清單", null,
            Filters(("searchProduct", "A-100"), ("plantId", "P999"), ("sortOrder", "x"))));

        var json = _context.ReportDefinitions.Single().ParamsJson;

        // ParamsJson 會被展開成目標畫面的查詢字串；不過濾等於開放對該畫面注入任意參數。
        Assert.Contains("A-100", json);
        Assert.DoesNotContain("P999", json);
        Assert.DoesNotContain("sortOrder", json);
    }

    [Fact]
    public async Task 存檔_沒有目標報表權限_擋下()
    {
        _catalog.Setup(c => c.IsAuthorizedAsync("StockShortage")).ReturnsAsync(false);

        var r = await Svc().SaveAsync(new SavedQueryInput("StockShortage", "X", null, Filters()));

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_Forbidden", r.ErrorMessage);
    }

    [Fact]
    public async Task 存檔_未知工具_擋下()
    {
        var r = await Svc().SaveAsync(new SavedQueryInput("NotAReport", "X", null, Filters()));

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_UnknownTool", r.ErrorMessage);
    }

    [Fact]
    public async Task 存檔_同名覆寫而非新增第二筆()
    {
        var svc = Svc();
        await svc.SaveAsync(new SavedQueryInput("StockShortage", "常用", null,
            Filters(("searchProduct", "A"))));
        await svc.SaveAsync(new SavedQueryInput("StockShortage", "常用", null,
            Filters(("searchProduct", "B"))));

        // 唯一鍵是 (OwnerUserId, Name)（§7.9-5）：同一人不該存出三個都叫「常用」的查詢。
        var row = Assert.Single(_context.ReportDefinitions);
        Assert.Contains("\"B\"", row.ParamsJson);
    }

    [Fact]
    public async Task 存檔_名稱空白_擋下()
    {
        var r = await Svc().SaveAsync(new SavedQueryInput("StockShortage", "   ", null, Filters()));

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_NameRequired", r.ErrorMessage);
    }

    [Fact]
    public async Task 存檔_ScheduleKind維持null即為純常用查詢()
    {
        await Svc().SaveAsync(new SavedQueryInput("StockShortage", "常用", null, Filters()));

        Assert.Null(_context.ReportDefinitions.Single().ScheduleKind);
    }

    // ────────────────────────────────────────────────────────────────
    // 清單
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 清單_只看得到自己的()
    {
        Seed(Me,    "我的",   "StockShortage", "{}");
        Seed(Other, "別人的", "StockShortage", "{}");

        var items = await Svc().ListAsync();

        Assert.Equal("我的", Assert.Single(items).Name);
    }

    [Fact]
    public async Task 清單_失去權限的項目仍列出但標記為無權限()
    {
        Seed(Me, "可用",   "StockShortage", "{}");
        Seed(Me, "沒權限", "InventoryValuation", "{}");
        _catalog.Setup(c => c.IsAuthorizedAsync("InventoryValuation")).ReturnsAsync(false);

        var items = await Svc().ListAsync();

        // §7.9-3：靜默隱藏會被當成資料遺失來報修；要顯示成「無權限」。
        Assert.Equal(2, items.Count);
        Assert.False(items.Single(i => i.Name == "沒權限").IsAuthorized);
        Assert.True(items.Single(i => i.Name == "可用").IsAuthorized);
    }

    [Fact]
    public async Task 清單_ParamsJson損毀_標記為失效而非靜默當成預設值()
    {
        Seed(Me, "壞掉的", "StockShortage", "{ this is not json ");

        var item = Assert.Single(await Svc().ListAsync());

        // §7.0.1-c：靜默跳過會給出一個看起來正常、內容卻是預設值的查詢。
        Assert.False(item.IsValid);
        Assert.Equal("SavedQuery_Invalid", item.InvalidReason);
    }

    [Fact]
    public async Task 清單_可依工具篩選()
    {
        Seed(Me, "A", "StockShortage", "{}");
        Seed(Me, "B", "InventoryValuation", "{}");

        var items = await Svc().ListAsync("StockShortage");

        Assert.Equal("A", Assert.Single(items).Name);
    }

    // ────────────────────────────────────────────────────────────────
    // 刪除
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 刪除_他人的查詢_不受理且不刪除()
    {
        var other = Seed(Other, "別人的", "StockShortage", "{}");

        Assert.False(await Svc().DeleteAsync(other.Id));
        Assert.Single(_context.ReportDefinitions);
    }

    [Fact]
    public async Task 刪除_自己的查詢()
    {
        var mine = Seed(Me, "我的", "StockShortage", "{}");

        Assert.True(await Svc().DeleteAsync(mine.Id));
        Assert.Empty(_context.ReportDefinitions);
    }

    // ────────────────────────────────────────────────────────────────
    // 執行
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 執行_相對期間每次重新解算為當下的絕對日期()
    {
        var row = Seed(Me, "上月銷售", "SalesRanking", """{"period":"LastMonth","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success);
        Assert.Contains("/SalesRanking/Index?searched=true", r.Url);
        Assert.Contains("fromDate=2026-07-01", r.Url);
        Assert.Contains("toDate=2026-07-31", r.Url);
        _periods.Verify(p => p.ResolveAsync(RelativePeriod.LastMonth, "Asia/Taipei",
                                            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 執行_以當下使用者解析權限_不沿用OwnerRoleId()
    {
        var row = Seed(Me, "銷售", "SalesRanking", """{"period":"LastMonth","filters":{}}""");
        _catalog.Setup(c => c.IsAuthorizedAsync("SalesRanking")).ReturnsAsync(false);

        var r = await Svc().BuildRunUrlAsync(row.Id);

        // 存檔時有權限不代表現在還有（§7.9-3）；而且日後開放分享時，
        // 沿用 OwnerRoleId 就成了權限提升管道（§7.9 L3 定案）。
        Assert.False(r.Success);
        Assert.Equal("SavedQuery_Forbidden", r.ErrorMessage);
        Assert.Null(r.Url);
    }

    [Fact]
    public async Task 執行_他人的查詢_查無()
    {
        var other = Seed(Other, "別人的", "SalesRanking", """{"period":"LastMonth","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(other.Id);

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_NotFound", r.ErrorMessage);
    }

    [Fact]
    public async Task 執行_參數會被轉義後併入網址()
    {
        var row = Seed(Me, "缺料", "StockShortage",
                       """{"filters":{"searchProduct":"A 100&x=1"}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success);
        Assert.Contains("searchProduct=A%20100%26x%3D1", r.Url);
        // 轉義失敗會讓存下來的值直接變成額外的查詢參數。
        Assert.DoesNotContain("&x=1", r.Url);
    }

    [Fact]
    public async Task 執行_ParamsJson損毀_回失效而非組出預設查詢()
    {
        var row = Seed(Me, "壞掉的", "SalesRanking", "{ not json ");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_Invalid", r.ErrorMessage);
    }

    [Fact]
    public async Task 執行_有日期區間的報表卻沒存期間_回失效()
    {
        var row = Seed(Me, "怪的", "SalesRanking", """{"filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_Invalid", r.ErrorMessage);
    }

    // ────────────────────────────────────────────────────────────────
    // 會計期間類畫面（財報）
    // ────────────────────────────────────────────────────────────────

    private void SeedPeriod(string periodId, DateOnly from, DateOnly to)
    {
        _context.CurrentPlantId = "P001";
        _context.AccountingPeriods.Add(new SmartFactory.MES.Modules.ACT.Models.AccountingPeriod
        {
            PeriodId  = periodId,
            PlantId   = "P001",
            Year      = from.Year,
            Month     = from.Month,
            StartDate = from,
            EndDate   = to,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task 資產負債表_串的是asOfPeriod而不是日期()
    {
        SeedPeriod("202607", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var row = Seed(Me, "本期資產負債表", "BalanceSheet",
            """{"period":"ThisFiscalPeriod","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("asOfPeriod=202607", r.Url);
        // 這正是 2026-09-21 之前擋著不做繫結的原因：串上去畫面也不讀
        Assert.DoesNotContain("fromDate=", r.Url);
        Assert.DoesNotContain("toDate=", r.Url);
    }

    [Fact]
    public async Task 損益表_串起訖兩個期間代碼()
    {
        SeedPeriod("202606", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));
        SeedPeriod("202607", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        _periods.Setup(p => p.ResolveAsync(It.IsAny<RelativePeriod>(), It.IsAny<string?>(),
                                           It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 7, 31)));

        var row = Seed(Me, "近兩期損益", "ProfitLoss",
            """{"period":"ThisFiscalPeriod","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("fromPeriod=202606", r.Url);
        Assert.Contains("toPeriod=202607", r.Url);
    }

    // ────────────────────────────────────────────────────────────────
    // 年度型畫面（採購金額分析）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 年度型畫面串的是 <c>year=</c>，不是日期也不是期間代碼。
    ///
    /// <para><c>ReportPeriodStyle.Year</c> 是 2026-09-23 為採購金額分析新增的第五種期間形狀
    /// ——那張畫面選的是一個年度、看 12 個月，既有四種都表達不了它。少了這個 case，
    /// <c>AppendPeriodAsync</c> 會落進 <c>default</c> 分支**什麼都不串**，於是常用查詢
    /// 點開後拿到的是畫面預設年度（今年），而使用者存的是「去年」——數字全錯，
    /// 而畫面上沒有任何訊號說期間被忽略了。</para>
    /// </summary>
    [Fact]
    public async Task 年度型畫面_串的是year而不是日期或期間代碼()
    {
        // 「去年」解算出來的區間落在 2025 年內。
        _periods.Setup(p => p.ResolveAsync(It.IsAny<RelativePeriod>(), It.IsAny<string?>(),
                                           It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DateRange(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)));

        var row = Seed(Me, "去年採購金額", "PurchaseAmount",
            """{"period":"LastYear","filters":{"groupBy":"Product"}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("year=2025", r.Url);
        Assert.Contains("groupBy=Product", r.Url);
        // 年度畫面讀不到這些；串上去只是讓網址變長，而且會讓人以為期間有生效。
        Assert.DoesNotContain("fromDate=", r.Url);
        Assert.DoesNotContain("toDate=", r.Url);
        Assert.DoesNotContain("fromPeriod=", r.Url);
    }

    [Fact]
    public async Task 期間代碼是查出來的_不是把日期格式化成yyyyMM()
    {
        // 自訂起訖的會計期間：7/26 起算的「202608」期。若實作是 range.To.ToString("yyyyMM")
        // 會得到 202607——一個這個廠根本沒有的期別，而畫面對查無期間是顯示空報表。
        SeedPeriod("202608", new DateOnly(2026, 7, 26), new DateOnly(2026, 8, 25));
        _periods.Setup(p => p.ResolveAsync(It.IsAny<RelativePeriod>(), It.IsAny<string?>(),
                                           It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DateRange(new DateOnly(2026, 7, 26), new DateOnly(2026, 7, 31)));

        var row = Seed(Me, "自訂期", "BalanceSheet",
            """{"period":"ThisFiscalPeriod","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("asOfPeriod=202608", r.Url);
    }

    [Fact]
    public async Task 查無會計期間_回報錯誤而不是產出一個跑不出數字的網址()
    {
        var row = Seed(Me, "沒建期間", "BalanceSheet",
            """{"period":"ThisFiscalPeriod","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_NoFiscalPeriod", r.ErrorMessage);
    }

    [Fact]
    public async Task 解算端擲例外時轉成錯誤訊息_不讓點連結變成500()
    {
        _periods.Setup(p => p.ResolveAsync(It.IsAny<RelativePeriod>(), It.IsAny<string?>(),
                                           It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("查無涵蓋的會計期間"));

        var row = Seed(Me, "沒建期間", "ProfitLoss",
            """{"period":"LastFiscalPeriod","filters":{}}""");

        var r = await Svc().BuildRunUrlAsync(row.Id);

        Assert.False(r.Success);
        Assert.Equal("SavedQuery_NoFiscalPeriod", r.ErrorMessage);
    }

    // ────────────────────────────────────────────────────────────────
    // 繫結完整性
    // ────────────────────────────────────────────────────────────────

    // 這組斷言原本兩邊各抄一份「當時的六支」。手抄清單守不住新增：
    // 2026-09-15 之後陸續抽出的 11 支工具一支都沒有畫面繫結，而這兩支測試全綠——
    // 因為新工具根本沒被加進母體。母體改為工具那一側的單一來源。
    private static readonly string[] AllToolKeys = ReportToolKeys.All;

    /// <summary>
    /// 已知暫無畫面繫結者，逐支附理由。清單只准縮短。
    ///
    /// <para>2026-09-21 清空：BalanceSheet／ProfitLoss 原本掛在這裡，理由是「畫面吃會計期間
    /// 而不是 fromDate／toDate」。繫結加上 <c>ReportPeriodStyle</c> 之後那個理由消失了——
    /// 期間形狀變成繫結說得出口的東西，解算出來的區間會被換回 <c>AccountingPeriod.PeriodId</c>。</para>
    /// </summary>
    private static readonly Dictionary<string, string> BindingExempt = new(StringComparer.Ordinal);

    [Fact]
    public void 每支已登錄的報表工具都要有畫面繫結()
    {
        // 少了繫結，常用查詢會「存得下去卻執行不了」——存檔那一刻不會有任何徵兆。
        // 更前面一層：報表畫面上的「我的常用查詢」整塊不會渲染，也沒有錯誤。
        foreach (var key in AllToolKeys)
        {
            if (BindingExempt.ContainsKey(key)) continue;
            Assert.True(ReportScreenBindings.Find(key) is not null, $"{key} 缺少畫面繫結");
        }

        // 自我檢查：母體解析不到就等於規則關閉。
        Assert.True(AllToolKeys.Length >= 17, $"工具母體只有 {AllToolKeys.Length} 支，母體來源可能壞了");
    }

    [Fact]
    public void 畫面繫結不得多出未登錄的工具()
    {
        var toolKeys = new HashSet<string>(AllToolKeys, StringComparer.Ordinal);

        foreach (var key in ReportScreenBindings.All.Keys)
            Assert.Contains(key, toolKeys);
    }

    [Fact]
    public void 豁免清單不得留下已經補上繫結的項目()
    {
        // 補了繫結卻忘了從豁免清單移除，下一個人看到的會是一份過期的「已知限制」。
        foreach (var (key, reason) in BindingExempt)
        {
            Assert.True(ReportScreenBindings.Find(key) is null,
                $"{key} 已有畫面繫結，請從豁免清單移除（原理由：{reason}）");
        }
    }
}
