using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// <see cref="ReportToolCatalog"/> 的守門測試（§5.1、§8.1）。
///
/// 這裡最重要的兩支不是目錄本身的行為，而是
/// <c>工具宣告的權限動詞</c> 必須與 <c>該報表畫面現行的閘門</c> 逐支一致：
/// <c>CanRead</c> 恆為 <c>CanCreate</c> 的**真超集**，兩端不一致不會有任何錯誤訊息，
/// 只會讓看得到畫面的人與跑得動工具的人是兩群不同的人。
/// 2026-08-14 裁決後兩端恰好都是 <c>CanRead</c>——比對機制仍必須留著，
/// 因為「目前全站一致」是現況，不是契約。
/// </summary>
public class ReportToolCatalogTests : TestBase
{
    // ────────────────────────────────────────────────────────────────
    // 假工具：目錄本身的行為用假的測，不牽扯真工具的查詢邏輯
    // ────────────────────────────────────────────────────────────────

    private sealed class FakeTool : IReportToolDescriptor
    {
        public FakeTool(string key, ReportToolPermission permission)
        {
            ToolKey    = key;
            Permission = permission;
        }

        public string ToolKey     { get; }
        public string Description => $"fake:{ToolKey}";
        public ReportToolPermission Permission { get; }
        public bool ContainsMonetaryValues => false;   // 本測試不涉及機密閘門
        public bool RequiresPeriod => false;          // 本測試不涉及期間解算
        public Type RequestType   => typeof(ReportRequestBase);
        public Type ResponseType  => typeof(object);

        public Task<IReadOnlyList<object>> GetExportRowsAsync(
            ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<object>>([]);

        // 假工具不匯出，欄位清單為空即可——本批測試看的是目錄、權限與 schema。
        public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) => [];
    }

    private readonly Mock<IPermissionService> _perm = new();

    private ReportToolCatalog Catalog(params IReportToolDescriptor[] tools)
        => new(tools, _perm.Object);

    // ────────────────────────────────────────────────────────────────
    // 目錄行為
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void 重複ToolKey_建構即拋()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Catalog(
            new FakeTool("SalesRanking", ReportToolPermission.CanRead),
            new FakeTool("SalesRanking", ReportToolPermission.CanCreate)));

        Assert.Contains("SalesRanking", ex.Message);
    }

    [Fact]
    public void All_依ToolKey排序()
    {
        var catalog = Catalog(
            new FakeTool("Zebra",  ReportToolPermission.CanRead),
            new FakeTool("Alpha",  ReportToolPermission.CanRead),
            new FakeTool("Middle", ReportToolPermission.CanRead));

        Assert.Equal(["Alpha", "Middle", "Zebra"], catalog.All.Select(t => t.ToolKey));
    }

    [Fact]
    public void Find_未知鍵_回null()
    {
        var catalog = Catalog(new FakeTool("A", ReportToolPermission.CanRead));

        Assert.Null(catalog.Find("NotThere"));
        Assert.Null(catalog.Find(""));
        Assert.NotNull(catalog.Find("A"));
    }

    [Fact]
    public async Task IsAuthorizedAsync_未知鍵_不放行且不查權限()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        var catalog = Catalog(new FakeTool("A", ReportToolPermission.CanRead));

        Assert.False(await catalog.IsAuthorizedAsync("NotThere"));

        // 「查無此工具」不可退化成「不需檢查」——完全不該碰到權限服務就已否決。
        _perm.Verify(p => p.CanRead(It.IsAny<string>()),   Times.Never);
        _perm.Verify(p => p.CanCreate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CanCreate工具_使用者僅有CanRead_不放行()
    {
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(false);

        var catalog = Catalog(new FakeTool("OrderOverdue", ReportToolPermission.CanCreate));

        Assert.False(await catalog.IsAuthorizedAsync("OrderOverdue"));
        _perm.Verify(p => p.CanCreate("OrderOverdue"), Times.Once);
        _perm.Verify(p => p.CanRead(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task 動詞逐一分派到對應的權限方法()
    {
        _perm.Setup(p => p.CanRead("R")).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate("C")).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint("P")).ReturnsAsync(true);

        var catalog = Catalog(
            new FakeTool("R", ReportToolPermission.CanRead),
            new FakeTool("C", ReportToolPermission.CanCreate),
            new FakeTool("P", ReportToolPermission.CanPrint));

        Assert.True(await catalog.IsAuthorizedAsync("R"));
        Assert.True(await catalog.IsAuthorizedAsync("C"));
        Assert.True(await catalog.IsAuthorizedAsync("P"));

        _perm.Verify(p => p.CanRead("R"),   Times.Once);
        _perm.Verify(p => p.CanCreate("C"), Times.Once);
        _perm.Verify(p => p.CanPrint("P"),  Times.Once);
    }

    [Fact]
    public async Task GetAllWithAccessAsync_保留無權限項目而非過濾()
    {
        _perm.Setup(p => p.CanRead("Allowed")).ReturnsAsync(true);
        _perm.Setup(p => p.CanRead("Denied")).ReturnsAsync(false);

        var catalog = Catalog(
            new FakeTool("Allowed", ReportToolPermission.CanRead),
            new FakeTool("Denied",  ReportToolPermission.CanRead));

        var all = await catalog.GetAllWithAccessAsync();

        // §7.9 L2：無權限要顯示成「無權限」，不可從清單消失——
        // 使用者自己存的常用查詢憑空不見會被當成資料遺失。
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(a => a.Tool.ToolKey == "Allowed").IsAuthorized);
        Assert.False(all.Single(a => a.Tool.ToolKey == "Denied").IsAuthorized);
    }

    // ────────────────────────────────────────────────────────────────
    // 真工具：登錄完整性 + 與畫面閘門對齊
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 以各模組的 <c>IAppModule.RegisterServices</c> 實際建出容器再解析，
    /// 而不是自己 new 一份清單——這樣「工具寫好了但忘了在 Module 登錄」會被抓到。
    /// </summary>
    private static List<IReportToolDescriptor> ResolveRegisteredTools(ApplicationDbContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        services.AddSingleton(new Mock<IDateTimeService>().Object);
        services.AddSingleton(new Mock<IPermissionService>().Object);
        services.AddSingleton(new Mock<IUserContext>().Object);
        // RPT 的工具鏈（ContributionMarginService 等）另外吃系統參數服務。
        // GetAllAsync 必須回空字典而非 Mock 預設的 null——正式實作回的是空字典，
        // 回 null 會讓「參數尚未設定」在測試裡變成 NRE，那是假的失敗。
        var sysParam = new Mock<ISystemParamService>();
        sysParam.Setup(s => s.GetAllAsync()).ReturnsAsync(new Dictionary<string, string?>());
        services.AddSingleton(sysParam.Object);
        services.AddLogging();
        // 工具可能注入欄頭資源（工單進度的「逾期」欄值本身是在地化的）。
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        // 幣別服務是真的實作（不是 mock）——模組的 RegisterServices 登錄的就是它，
        // 而它吃 IMemoryCache。少了這一行，任何注入 ICurrencyService 的工具都會在
        // BuildServiceProvider 解析時拋，而錯誤訊息指向 CurrencyService 而不是那支工具。
        services.AddMemoryCache();

        var config = new ConfigurationBuilder().Build();
        // 模組清單是單一來源（含 BAS——RPT 財報吃它的 IAccountClassifier）：
        ReportToolKeys.RegisterToolModules(services, config);

        using var sp = services.BuildServiceProvider();
        return sp.GetServices<IReportToolDescriptor>().ToList();
    }

    /// <summary>
    /// 已抽出的工具清單。**刻意寫死而不是「數一數有幾支」**：這條測試要擋的是
    /// 「工具寫好了卻忘記在 Module 裡登錄」——那種漏法不會編譯失敗，
    /// 只會讓它在排程與常用查詢的下拉裡默默不存在。清單寫死才擋得到。
    /// </summary>
    private static readonly string[] ExpectedToolKeys =
    [
        // 依 Ordinal 排序（斷言比對的是排序後的實際清單）。
        // 註記的批次：Ⓐ＝Phase 0-B 首批六支（INV 3 ＋ ORD 3）、
        //            Ⓑ＝2026-09-15 批次 1（§6.1-3，PUR 1 ＋ ORD 1 ＋ MFG 3）
        //            Ⓒ＝RPT 六支（一直有註冊，2026-09-21 才補進本清單）
        //            Ⓓ＝2026-09-23：委外價格分析（新報表）＋三支既有報表補工具化
        //            Ⓔ＝2026-09-23：試算表（ACT，關帳前的科目層級對帳）
        "BalanceSheet",        // Ⓒ
        "ContributionMargin",  // Ⓒ
        "DefectRate",          // Ⓑ
        "DeliveryRate",        // Ⓑ
        "GrossMargin",         // Ⓒ
        "InventoryValuation",  // Ⓐ
        "MarginAnalysis",      // Ⓒ
        "MrpAchievement",      // Ⓑ
        "OrderOverdue",        // Ⓐ
        "OutsourcePerformance",// Ⓒ
        "OutsourcePrice",      // Ⓓ
        "ProfitLoss",          // Ⓒ
        "PurchaseAmount",      // Ⓓ
        "PurchaseOverdue",     // Ⓑ
        "PurchasePrice",       // Ⓓ
        "ReturnRate",          // Ⓐ
        "SalesRanking",        // Ⓐ
        "SlowMovingInventory", // Ⓐ
        "StockShortage",       // Ⓐ
        "SupplierDeliveryRate",// Ⓓ
        "TrialBalance",        // Ⓔ
        "WorkOrderProgress",   // Ⓑ
    ];

    [Fact]
    public void 已抽出的報表工具皆已登錄且ToolKey唯一()
    {
        var tools = ResolveRegisteredTools(_context);

        Assert.Equal(ExpectedToolKeys, tools.Select(t => t.ToolKey).OrderBy(k => k, StringComparer.Ordinal));

        // 建構不拋＝ToolKey 無重複（重複時 ReportToolCatalog 建構子會丟）
        var catalog = new ReportToolCatalog(tools, new Mock<IPermissionService>().Object);
        Assert.Equal(ExpectedToolKeys.Length, catalog.All.Count);
    }

    /// <summary>
    /// 每支工具的顯示名稱必須在 <c>ProjectZSharedResource</c> 的<b>兩個</b>語系都查得到。
    ///
    /// <para>排程設定的報表下拉以 <c>ToolKey</c> 當資源鍵取顯示名（鍵＝ToolKey＝MenuKey）。
    /// 缺鍵時 <c>IStringLocalizer</c> <b>不會拋例外，而是回傳鍵名本身</b>——
    /// 畫面上就變成 "InventoryValuation" 這種內部識別字，看起來像沒翻譯，
    /// 實際上是資源缺鍵，而且切語言也不會變。2026-09-14 Wave R 回報。</para>
    ///
    /// <para>⚠️ 兩個語系分開檢查而不是只查一個：漏的通常只有一邊，
    /// 而只查 zh-TW 的話英文介面缺鍵永遠不會紅。</para>
    /// </summary>
    [Fact]
    public void 每支工具的顯示名稱在兩個語系都有資源鍵()
    {
        var tools = ResolveRegisteredTools(_context);
        var root  = SmartFactory.MES.Tests.Conventions.ConventionScan.RepoRoot();

        foreach (var culture in new[] { "zh-TW", "en-US" })
        {
            var path = Path.Combine(root, "Resources", $"ProjectZSharedResource.{culture}.resx");
            Assert.True(File.Exists(path), $"找不到資源檔 {path}");

            var xml = File.ReadAllText(path);

            foreach (var tool in tools)
            {
                Assert.True(
                    xml.Contains($"name=\"{tool.ToolKey}\"", StringComparison.Ordinal),
                    $"工具 {tool.ToolKey} 在 {culture} 缺少顯示名稱資源鍵——" +
                    "排程的報表下拉會直接顯示 ToolKey 這個內部識別字，且不隨語言切換。");
            }
        }
    }

    /// <summary>
    /// 每支已登錄的工具，<c>GetExportRowsAsync</c> 都要**真的跑得起來並回得出一個清單**
    /// （空 DB 下就是零筆）。
    ///
    /// <para><b>為什麼補這一條</b>：2026-09-15 `origin/main` 有 40 分鐘是編不過的。
    /// PR #153 在 <c>IReportToolDescriptor</c> 加了必要成員 <c>GetExportRowsAsync</c>，
    /// PR #155 的五支新工具是在那之前分支的、沒有實作它。兩支各自 CI 全綠、
    /// 檔案也沒有交集所以合併得乾乾淨淨——**合起來才編不過**。
    /// 編譯錯誤本身由建置擋，測試擋不到；但這條擋得到它的近親：
    /// 實作了卻走錯路徑（回空、拋 NotImplemented、把包裝型別當集合），
    /// 那一類**不會編譯失敗，只會讓收件匣收到一份空白報表**——#153 修的正是那個。</para>
    /// </summary>
    [Fact]
    public async Task 每支工具都回得出匯出列()
    {
        // 會計期間導向的工具（ProfitLoss／BalanceSheet）**刻意不退回日曆月**：查無對應
        // 期間時直接拋，以免產出與帳務口徑不符的數字。測試期間要對應得上，否則這裡
        // 擋到的會是「沒種資料」而不是工具本身有問題。
        // 會計期間是 IPlantEntity，全域查詢過濾壓著 PlantId == CurrentPlantId；
        // 不設這個，種進去的期間自己查不到（症狀與「沒種」一模一樣）。
        _context.CurrentPlantId = "P001";
        _context.AccountingPeriods.Add(new SmartFactory.MES.Modules.ACT.Models.AccountingPeriod
        {
            PeriodId  = "202601",
            PlantId   = "P001",
            Year      = 2026,
            Month     = 1,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate   = new DateOnly(2026, 1, 31),
        });
        await _context.SaveChangesAsync();

        var tools = ResolveRegisteredTools(_context);

        foreach (var tool in tools)
        {
            var request = (ReportRequestBase)Activator.CreateInstance(tool.RequestType)!;
            request.PlantId = "P001";
            // 給絕對期間而不是相對期間：相對期間要讀時鐘，而這裡的 IDateTimeService
            // 是替身，回的是 default(DateOnly)（0001-01-01），往前推一個月會直接爆掉。
            // 需要期間的工具拿得到區間，不需要的忽略它——兩邊都走得完。
            request.AbsolutePeriod = new DateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

            var rows = await tool.GetExportRowsAsync(request, user: null, CancellationToken.None);

            Assert.True(rows is not null, $"工具 {tool.ToolKey} 的 GetExportRowsAsync 回了 null");
        }

        // 自我檢查：解析不到工具的話上面的迴圈一次都不會跑，規則形同關閉。
        Assert.Equal(ExpectedToolKeys.Length, tools.Count);
    }

    [Fact]
    public void 每支工具的ToolKey等於其Controller的MenuKey()
    {
        var tools = ResolveRegisteredTools(_context);
        var asm   = typeof(SmartFactory.MES.SharedResource).Assembly;

        foreach (var tool in tools)
        {
            var controller = asm.GetTypes()
                .SingleOrDefault(t => t.Name == tool.ToolKey + "Controller");
            Assert.True(controller is not null,
                $"找不到工具 {tool.ToolKey} 對應的 Controller（命名須為 {tool.ToolKey}Controller）");

            var menuKey = controller!
                .GetField("MenuKey", BindingFlags.NonPublic | BindingFlags.Public |
                                     BindingFlags.Static | BindingFlags.FlattenHierarchy)
                ?.GetRawConstantValue() as string;

            // ToolKey 同時被 ReportToolCatalog 當成 MenuKey 拿去查權限；
            // 兩者一旦不一致，權限會查到一個不存在的選單鍵而靜默 default-deny 或放行。
            Assert.Equal(menuKey, tool.ToolKey);
        }
    }

    [Fact]
    public void 每支工具宣告的動詞等於其Controller_Index的閘門()
    {
        var tools = ResolveRegisteredTools(_context);
        var root  = SmartFactory.MES.Tests.Conventions.ConventionScan.RepoRoot();

        // Index 的閘門是檔案中第一個 `Can*(MenuKey)) return Forbid()`；
        // 其後那個是 Export 的 CanPrint，不是本測試要比對的對象。
        var gateRx = new Regex(@"_permissionService\.(Can\w+)\(MenuKey\)\)\s*return Forbid\(\)",
                               RegexOptions.Compiled);

        foreach (var tool in tools)
        {
            var file = Directory
                .EnumerateFiles(Path.Combine(root, "Modules"),
                                tool.ToolKey + "Controller.cs", SearchOption.AllDirectories)
                .Single();

            var match = gateRx.Match(File.ReadAllText(file));
            Assert.True(match.Success, $"{tool.ToolKey}Controller 找不到 Index 閘門");

            var screenGate = match.Groups[1].Value;   // CanRead / CanCreate
            Assert.Equal(screenGate, tool.Permission.ToString());
        }
    }
}
