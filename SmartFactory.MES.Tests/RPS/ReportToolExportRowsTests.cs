using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 排程／匯出路徑真的取得到資料列（<c>IReportToolDescriptor.GetExportRowsAsync</c>）。
///
/// <para><b>這一組防的是什麼</b>：2026-09-14 Wave R 實測發現排程產出的
/// <c>InventoryValuation</c> 報表內容完全空白。原因是 <c>ReportToolRunner</c> 早先
/// 把工具的回應直接當 <c>IEnumerable</c> 攤平，遇到<b>包裝型別</b>就落進
/// 「不是集合 → 回零筆」那條路。六支工具裡有兩支是包裝型別
/// （<c>InventoryValuationResult(Page, GrandTotal)</c>、
/// <c>SlowMovingInventoryResult(Rows, TooMany)</c>），於是這兩支的排程一直在寄空檔案。</para>
///
/// <para>🚨 <b>為什麼畫面全綠也擋不住</b>：畫面走的是 <c>ExecuteAsync</c> 並自己取
/// <c>.Page</c>／<c>.Rows</c>，一路正確；只有排程與匯出會空。
/// <c>InvReportCharacterizationTests</c> 測的正是畫面那條路——它 30 幾支全綠，
/// 而使用者收到的是空白報表。**同一支工具的兩條出口，測了一條不等於測了另一條。**</para>
/// </summary>
public class ReportToolExportRowsTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly Today = new(2026, 5, 14);

    private readonly Mock<IDateTimeService>   _dt   = new();
    private readonly Mock<IPermissionService> _perm = new();

    public ReportToolExportRowsTests()
    {
        _context.CurrentPlantId = PlantId;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    private void AddProduct(string code, string name)
    {
        _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = name, StockUom = "PC", IsActive = true,
        });
        _context.SaveChanges();
    }

    private void AddInventory(string product, string location, decimal onHand, decimal unitCost)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product, LocationCode = location,
            LotNo = "", QtyOnHand = onHand, QtyReserved = 0, UnitCost = unitCost,
        });
        _context.SaveChanges();
    }

    /// <summary>以真工具組出 runner，走的是排程當下完全相同的那條路徑。</summary>
    private ReportToolRunner Runner(string? currentPlantId = null)
    {
        IReportToolDescriptor[] tools =
        [
            new InventoryValuationTool(_context),
            new SlowMovingInventoryTool(_context, _dt.Object),
        ];

        // 用真的 resx localizer：欄頭現在由工具宣告的資源鍵解析，
        // 換成替身就驗不到「鍵查不到時會靜默印出鍵本身」這一類問題。
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(o => o.ResourcesPath = "Resources")
            // 幣別服務吃 IMemoryCache（見 ReportToolCatalogTests 同一行的說明）。
            .AddMemoryCache()
            .BuildServiceProvider();

        return new ReportToolRunner(
            new ReportToolCatalog(tools, _perm.Object),
            sp,
            sp.GetRequiredService<IStringLocalizer<ProjectZReportHeaderResource>>(),
            NewRunnerDb(currentPlantId));
    }

    /// <summary>
    /// ReportToolRunner 從這裡讀 CurrentPlantId 去補 request.PlantId（契約：null ＝ 依執行 scope）。
    /// 本測試不驗廠別，給一個空的 InMemory 上下文即可——但**不能不給**：
    /// 讓它可為 null 的話，真正的呼叫端漏掉時同樣不會有聲音。
    /// </summary>
    private static ApplicationDbContext NewRunnerDb(string? currentPlantId = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) { CurrentPlantId = currentPlantId };

    private static string ParamsFor() => $"{{\"plantId\":\"{PlantId}\"}}";

    // ════════════════════════════════════════════════════════════════
    // 兩支包裝型別的工具——原缺陷的本體
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原缺陷的直接複現：這支在修好之前 <c>Headers</c> 與 <c>Rows</c> 都是空的，
    /// 而且**不拋例外、不留日誌**——收件匣裡是一份格式正確、零筆的 Excel。
    /// </summary>
    [Fact]
    public async Task InventoryValuation_排程匯出拿得到資料列而不是空白報表()
    {
        AddProduct("PRD-A", "甲料");
        AddProduct("PRD-B", "乙料");
        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 5m);
        AddInventory("PRD-B", "L1", onHand: 3m,  unitCost: 2.5m);

        var result = await Runner().RunAsync("InventoryValuation", ParamsFor(), user: null, timeZoneId: null);

        Assert.NotEmpty(result.Headers);
        Assert.Equal(2, result.Rows.Count);

        // 欄位與欄頭由工具的 GetExportColumns 宣告（2026-09-15 S-5 之前是反射取屬性名）。
        // 第一欄是品號，欄頭是**在地化後的字串**而不是 `ProductCode`——順帶釘住
        // 「投影的是資料列本身，不是包裝型別的 Page／GrandTotal 那兩個屬性」。
        Assert.Equal(7, result.Headers.Length);          // 與畫面匯出同樣七欄
        Assert.DoesNotContain("InventoryValuation.", result.Headers[0]);   // 不是資源鍵本身
        Assert.NotEqual("ProductCode", result.Headers[0]);                 // 也不是屬性名
        Assert.Contains(result.Rows, r => (string?)r[0] == "PRD-A");
        Assert.Contains(result.Rows, r => (string?)r[0] == "PRD-B");
    }

    /// <summary>
    /// 排程輸出的是<b>整份</b>。這支釘住的是第二層缺陷：畫面路徑的
    /// <c>PageSize ?? 20</c> 不可以滲進排程——否則修好「空白」之後，
    /// 症狀會從「一筆都沒有」變成「永遠只有 20 筆」，一樣無聲。
    /// </summary>
    [Fact]
    public async Task InventoryValuation_排程匯出不套用畫面的分頁預設值()
    {
        for (var i = 0; i < 25; i++)
        {
            AddProduct($"PRD-{i:00}", $"料{i:00}");
            AddInventory($"PRD-{i:00}", "L1", onHand: 1m, unitCost: 1m);
        }

        var result = await Runner().RunAsync("InventoryValuation", ParamsFor(), user: null, timeZoneId: null);

        Assert.Equal(25, result.Rows.Count);
    }

    /// <summary>
    /// <c>SlowMovingInventoryResult</c> 同樣是包裝型別（<c>Rows</c> ＋ <c>TooMany</c>）。
    /// 沒有異動紀錄者以哨兵值 9999 天計，必定超過預設門檻 90 天而上榜。
    /// </summary>
    [Fact]
    public async Task SlowMovingInventory_排程匯出拿得到資料列()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 4m, unitCost: 7m);

        var result = await Runner().RunAsync("SlowMovingInventory", ParamsFor(), user: null, timeZoneId: null);

        Assert.NotEmpty(result.Headers);
        Assert.Single(result.Rows);
    }

    /// <summary>
    /// 零筆是<b>合法</b>結果，不可與「工具壞掉」混為一談：這裡必須安靜地回零筆，
    /// 不拋例外。這支同時是上面那些測試的反面守門——若把「資料列取不到就拋」
    /// 寫成「零筆就拋」，正常的空報表會開始在收件匣裡變成失敗紀錄。
    ///
    /// <para><b>2026-09-15 起零筆仍要輸出欄頭</b>（S-5）。在此之前欄頭是反射產生的，
    /// 零筆時沒有樣本可反射、只能回空陣列；現在欄位由工具宣告，不需要樣本。
    /// 這不只是「順便做得到」——一份**有欄頭、零資料列**的檔案看得出「查過了但沒有資料」，
    /// 而一份連欄頭都沒有的空白檔，與「產出失敗」在收件匣裡長得一模一樣。</para>
    /// </summary>
    [Fact]
    public async Task 條件下沒有資料時安靜回零筆而不是失敗()
    {
        var result = await Runner().RunAsync("InventoryValuation", ParamsFor(), user: null, timeZoneId: null);

        Assert.Empty(result.Rows);
        Assert.NotEmpty(result.Headers);
    }

    // ════════════════════════════════════════════════════════════════
    // 互動路徑不得指定別的廠別（安全評估 2026-10-07 #2）
    // ════════════════════════════════════════════════════════════════

    private static System.Security.Claims.ClaimsPrincipal SomeUser() =>
        new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u1")], "Test"));

    /// <summary>
    /// 自然語言查詢時，模型被提問誘導填了別的廠別。略過全域廠別過濾的工具會照這個值查，
    /// 所以 runner 要擋——而且是擲例外，不是靜默改回目前廠別（那會讓使用者以為查到了 P2）。
    /// </summary>
    [Fact]
    public async Task 有使用者身分時_參數指定別的廠別_擋下()
    {
        var ex = await Assert.ThrowsAsync<ReportParamsInvalidException>(() =>
            Runner(currentPlantId: PlantId).RunAsync(
                "InventoryValuation", """{"plantId":"OTHER"}""", SomeUser(), timeZoneId: null));

        Assert.Contains("OTHER", ex.Message);
    }

    [Fact]
    public async Task 有使用者身分時_參數就是目前廠別或不給_照常執行()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 5m);

        var same = await Runner(currentPlantId: PlantId).RunAsync(
            "InventoryValuation", ParamsFor(), SomeUser(), timeZoneId: null);
        var omitted = await Runner(currentPlantId: PlantId).RunAsync(
            "InventoryValuation", "{}", SomeUser(), timeZoneId: null);

        Assert.NotEmpty(same.Rows);
        Assert.NotEmpty(omitted.Rows);
    }

    /// <summary>排程路徑（user 為 null）不受影響：派工器已把 CurrentPlantId 設成排程的廠別。</summary>
    [Fact]
    public async Task 沒有使用者身分_排程路徑不受廠別守門影響()
    {
        var result = await Runner(currentPlantId: "SOMEWHERE-ELSE").RunAsync(
            "InventoryValuation", ParamsFor(), user: null, timeZoneId: null);

        Assert.NotNull(result);
    }
}
