using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Moq;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.MFG.Reporting;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA;

/// <summary>
/// <see cref="QaToolSchemaBuilder"/> 的守門測試（§8.4、§11.1）。
///
/// <para>兩個重點，都來自 2026-08-21 的實測而非推測：
/// ① **L1 過濾必須發生在組 schema 的階段**——實測目錄外提問時模型會把看得到的工具
/// 全部列給使用者，放到執行階段才擋等於目錄已經洩漏；
/// ② **該有列舉值的欄位必須帶 `enum`**——實測沒有 `enum` 的欄位會被憑空編出合法外的值。</para>
/// </summary>
public class QaToolSchemaBuilderTests
{
    private sealed class FakeTool : IReportToolDescriptor
    {
        public FakeTool(string key, Type? requestType = null, bool monetary = false)
        {
            ToolKey = key;
            RequestType = requestType ?? typeof(SampleRequest);
            ContainsMonetaryValues = monetary;
        }

        public string ToolKey { get; }
        public string Description => $"desc:{ToolKey}";
        public ReportToolPermission Permission => ReportToolPermission.CanRead;
        public bool ContainsMonetaryValues { get; }
        public bool RequiresPeriod => false;          // 本測試不涉及期間解算
        public Type RequestType { get; }
        public Type ResponseType => typeof(object);

        // 本測試不涉及匯出路徑；契約要求每支工具自己回答「資料列在哪」，故給空集合。
        public Task<IReadOnlyList<object>> GetExportRowsAsync(
            ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<object>>([]);

        // 假工具不匯出，欄位清單為空即可——本批測試看的是目錄、權限與 schema。
        public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) => [];
    }

    private sealed class SampleRequest : ReportRequestBase
    {
        public string? ProductCode { get; set; }
    }

    /// <summary>取一支已授權工具的參數 schema。刻意走公開的 <c>BuildAsync</c>——
    /// 測試只碰公開行為，不為了測試把實作細節開放出去。</summary>
    private static async Task<IReadOnlyDictionary<string, QaToolProperty>> PropsAsync()
        => (await Builder(("X", true)).BuildAsync()).Single().Properties;

    private static IQaToolSchemaBuilder Builder(params (string key, bool authorized)[] tools)
        => Builder(tools.Select(t => new ReportToolAccess(new FakeTool(t.key), t.authorized)).ToList());

    private static IQaToolSchemaBuilder Builder(
        IReadOnlyList<ReportToolAccess> access, bool? allowMonetary = null)
    {
        var m = new Mock<IReportToolAccessBatch>();
        m.Setup(a => a.GetAccessAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(access);

        return new QaToolSchemaBuilder(m.Object, Config(allowMonetary));
    }

    /// <summary>
    /// <paramref name="allowMonetary"/> 為 <c>null</c> 時**完全不設這個鍵**——
    /// 那才是「沒有人設定過」的形狀，也是預設值真正要守的那一種。
    /// 顯式寫 <c>false</c> 只證明「設 false 會生效」，證不到預設值是什麼。
    /// </summary>
    private static IConfiguration Config(bool? allowMonetary)
    {
        var b = new ConfigurationBuilder();
        if (allowMonetary is { } v)
            b.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [QaToolSchemaBuilder.AllowMonetaryKey] = v ? "true" : "false",
            });
        return b.Build();
    }

    // ────────────────────────────────────────────────────────────────
    // L1 過濾
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task 未授權的工具_不進schema()
    {
        var defs = await Builder(("SalesRanking", false), ("StockShortage", true)).BuildAsync();

        // 名稱本身就是資訊：未授權的工具連名字都不該出現在送給模型的 schema 裡。
        Assert.Equal(["StockShortage"], defs.Select(d => d.Name));
    }

    [Fact]
    public async Task 全部未授權_回空清單而不是全部()
    {
        var defs = await Builder(("A", false), ("B", false)).BuildAsync();

        Assert.Empty(defs);
    }

    // ────────────────────────────────────────────────────────────────
    // 參數排除（安全）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlantId_絕不可出現在schema()
    {
        var props = await PropsAsync();

        // 🚨 PlantId 的 null 語意是「依執行 scope 的授權範圍」，不是「查全部廠」。
        // 把它交給模型填等於給出一個跨廠查詢的參數。
        Assert.DoesNotContain("plantId", props.Keys);
    }

    [Theory]
    [InlineData("timeZoneId")]   // 系統決定，模型填會讓「今天」差一天
    [InlineData("pageNumber")]   // 呼叫端／排程控制
    [InlineData("pageSize")]
    [InlineData("sortBy")]       // 實測被編出 "salesAmount"；工具 Description 已載明預設排序
    [InlineData("absolutePeriod")] // 複合型別，且 Period 依契約優先
    [InlineData("dateBasis")]      // live 實測：問題沒提基準，模型自己填 OrderDate，默默改掉報表行為
    public async Task 排除清單內的參數_不進schema(string name)
    {
        var props = await PropsAsync();

        Assert.DoesNotContain(name, props.Keys);
    }

    // ────────────────────────────────────────────────────────────────
    // 列舉值（準確率的關鍵）
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Period_帶完整enum值()
    {
        var props = await PropsAsync();

        var period = props["period"];
        Assert.Equal("string", period.Type);
        Assert.NotNull(period.EnumValues);
        Assert.Contains("LastMonth", period.EnumValues!);
        Assert.Equal(Enum.GetNames<RelativePeriod>().Length, period.EnumValues!.Count);
    }

    [Fact]
    public async Task 列舉型參數仍會帶enum值_以period為證()
    {
        // dateBasis 原本是這支測試的對象，2026-08-21 被移入排除清單（見上）。
        // 但「列舉型別要產生 enum」這條規則本身仍必須守住，改用 period 當證人——
        // 它是實測中唯一穩定填對的參數，而穩定的原因正是有 enum。
        var props = await PropsAsync();

        Assert.NotNull(props["period"].EnumValues);
        Assert.All(props["period"].EnumValues!, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public async Task 型別對應_整數與布林與日期()
    {
        var props = await PropsAsync();

        Assert.Equal("integer", props["topN"].Type);
        Assert.Equal("boolean", props["useLocalCurrency"].Type);
        Assert.Equal("date",    props["asOfDate"].Format);
        Assert.Equal("string",  props["productCode"].Type);   // 子類別特有屬性也要收進來
    }

    [Fact]
    public async Task Description原封帶入_那是模型選工具的依據()
    {
        var defs = await Builder(("StockShortage", true)).BuildAsync();

        Assert.Equal("desc:StockShortage", defs.Single().Description);
    }

    // ────────────────────────────────────────────────────────────────
    // 真實工具的 Request 型別：防止日後新增的參數悄悄進到 schema
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 反射是「有什麼就送什麼」，所以**新增一個 Request 屬性就等於新增一個模型可填的參數**，
    /// 而且不會有任何測試失敗來提醒你。這一支把六支真實工具釘住：
    /// 排除清單永遠不得出現，`period` 永遠要帶列舉值。
    /// </summary>
    [Theory]
    [InlineData(typeof(SalesRankingRequest))]
    [InlineData(typeof(ReturnRateRequest))]
    [InlineData(typeof(OrderOverdueRequest))]
    [InlineData(typeof(InventoryValuationRequest))]
    [InlineData(typeof(SlowMovingInventoryRequest))]
    [InlineData(typeof(StockShortageRequest))]
    public async Task 真實工具_排除清單不得外洩且period帶enum(Type requestType)
    {
        var m = new Mock<IReportToolAccessBatch>();
        m.Setup(a => a.GetAccessAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync([new ReportToolAccess(new FakeTool("T", requestType), true)]);

        var props = (await new QaToolSchemaBuilder(m.Object, Config(null)).BuildAsync())
            .Single().Properties;

        foreach (var banned in new[]
                 { "plantId", "timeZoneId", "pageNumber", "pageSize", "sortBy", "absolutePeriod" })
            Assert.DoesNotContain(banned, props.Keys);

        Assert.NotNull(props["period"].EnumValues);
    }

    // ────────────────────────────────────────────────────────────────
    // 機密閘門（§十三 Phase 2：含金額的報表第一版不進 NL 目錄）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// **預設就要擋住**，不需要任何組態。這一支釘的是「沒有人設定過」的那個狀態——
    /// 一個預設值只有在沒被設定時才會被用到，而那正是正式機的常態。
    /// </summary>
    [Fact]
    public async Task 未設定時_含金額的工具不進schema()
    {
        var defs = await Builder([
            new ReportToolAccess(new FakeTool("SalesRanking", monetary: true), true),
            new ReportToolAccess(new FakeTool("StockShortage"), true),
        ]).BuildAsync();

        Assert.Equal(["StockShortage"], defs.Select(d => d.Name));
    }

    /// <summary>
    /// 🚨 擋掉的必須是**整個定義**，不是只擋執行。名字本身就是資訊：
    /// 實測模型拒答時會把它看得到的工具全部列給使用者看（§11.1），
    /// 所以「SalesRanking」這個字串一旦進了 schema，目錄就已經洩漏了。
    /// </summary>
    [Fact]
    public async Task 被擋的工具_名稱與描述都不得出現()
    {
        var defs = await Builder([
            new ReportToolAccess(new FakeTool("SalesRanking", monetary: true), true),
        ]).BuildAsync();

        Assert.Empty(defs);
    }

    [Fact]
    public async Task 開啟AllowMonetaryTools_含金額的工具才進schema()
    {
        var access = new[]
        {
            new ReportToolAccess(new FakeTool("SalesRanking", monetary: true), true),
            new ReportToolAccess(new FakeTool("StockShortage"), true),
        };

        var defs = await Builder(access, allowMonetary: true).BuildAsync();

        Assert.Equal(["SalesRanking", "StockShortage"], defs.Select(d => d.Name).OrderBy(n => n));
    }

    /// <summary>
    /// **兩道閘門是 AND，不是 OR。** 放行機密不等於放行權限——
    /// 這一支存在的理由是：若哪天有人把兩個條件寫成同一個判斷，
    /// 症狀會是「開了 AllowMonetaryTools 之後，沒權限的人也問得到」，
    /// 而那不會有任何錯誤訊息。
    /// </summary>
    [Fact]
    public async Task 開啟AllowMonetaryTools_仍不放行未授權的工具()
    {
        var access = new[]
        {
            new ReportToolAccess(new FakeTool("SalesRanking", monetary: true), false),
        };

        Assert.Empty(await Builder(access, allowMonetary: true).BuildAsync());
    }

    /// <summary>
    /// 已抽出之真實工具的機密判定逐支釘住。判準是可查證的事實——**回傳型別裡有沒有金額欄位**
    /// （§八「含金額＝機密」，母體見 <c>report-permission-inventory.md</c>）——
    /// 而不是主觀分級，所以釘得住。
    ///
    /// <para>新工具漏宣告時這裡不會紅（它只列被列出的那幾支），但漏宣告根本編不過：
    /// <c>ContainsMonetaryValues</c> 刻意沒有預設實作，與 <c>Permission</c> 同一個理由。</para>
    /// </summary>
    [Theory]
    [InlineData(typeof(SalesRankingTool), true)]           // TotalAmount
    [InlineData(typeof(InventoryValuationTool), true)]     // UnitCost / TotalValue
    [InlineData(typeof(SlowMovingInventoryTool), true)]    // UnitCost / InventoryValue
    [InlineData(typeof(OrderOverdueTool), false)]          // 只有數量與逾期天數
    [InlineData(typeof(ReturnRateTool), false)]            // 只有出貨量／退貨量／退貨率
    [InlineData(typeof(StockShortageTool), false)]         // 只有各種數量
    // 2026-09-15 批次 1（§6.1-3）新增五支，全部不含金額：
    [InlineData(typeof(PurchaseOverdueTool), false)]       // 採購量／已到量／未到量／逾期天數
    [InlineData(typeof(DeliveryRateTool), false)]          // 只有筆數與達成率
    [InlineData(typeof(WorkOrderProgressTool), false)]     // 計劃量／完工量／進度百分比
    [InlineData(typeof(DefectRateTool), false)]            // 合格量／不良量／不良率
    [InlineData(typeof(MrpAchievementTool), false)]        // 需求量／實際量／短缺量
    public void 真實工具的機密判定(Type toolType, bool expected)
    {
        var prop = typeof(IReportToolDescriptor).GetProperty(
            nameof(IReportToolDescriptor.ContainsMonetaryValues))!;

        var map = toolType.GetInterfaceMap(typeof(IReportToolDescriptor));
        var getter = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, prop.GetMethod)];

        // 顯式介面實作 → 用未初始化的實例叫 getter 即可（它是常數，不碰任何欄位）。
        var instance = System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(toolType);

        Assert.Equal(expected, (bool)getter.Invoke(instance, null)!);
    }
}
