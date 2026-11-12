using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 採購單建行時的議定單價查詢（<c>GET /api/pur/vendor-price</c>，pur-domain.md §2.3）。
///
/// 這支端點是 `VendorPriceList` 主檔**唯一**的消費者：主檔本身有 CRUD smoke 測試，
/// 但「建好的價會不會在採購單上生效」一直沒有任何斷言。它失效的方向全都是靜默的
/// ——前端 `.catch(() => {})` 把錯誤吞掉，`if (!d.found) return` 把查不到當正常，
/// 使用者看到的只是「單價欄還是 0」，而那與「這筆料本來就沒議價」長得一模一樣。
///
/// 三段各自會壞的邏輯：①生效期間的區間比對 ②幣別相符 ③多筆命中時取哪一筆。
/// 其中幣別最貴：帶到別的幣別的價，金額會差一個匯率（本案 USD/NTD ≈ 31 倍），
/// 而數字本身看起來仍是個正常的價。
/// </summary>
public class VendorPriceLookupTests : TestBase
{
    private const string Supplier = "V001";
    private const string Product  = "RM-001";
    private const string Ccy      = "TWD";

    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly Mock<IDateTimeService> _dt = new();

    public VendorPriceLookupTests()
    {
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    private VendorPriceListController BuildController(bool canRead = true)
    {
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(canRead);

        return new VendorPriceListController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            new Mock<ICurrencyService>().Object,
            _dt.Object)
        {
            ControllerContext = GetControllerContextWithServices(permService: perm.Object),
        };
    }

    private void SeedPrice(decimal unitPrice, DateOnly validFrom, DateOnly? validTo = null,
                           bool isActive = true, string currencyCode = Ccy,
                           string productCode = Product, decimal? minQty = null)
    {
        _context.VendorPriceLists.Add(new VendorPriceList
        {
            SupplierCode = Supplier, ProductCode = productCode, CurrencyCode = currencyCode,
            UnitPrice = unitPrice, MinQty = minQty,
            ValidFrom = validFrom, ValidTo = validTo, IsActive = isActive,
        });
        _context.SaveChanges();
    }

    private async Task<(bool Found, decimal? UnitPrice, decimal? MinQty)> GetPriceAsync(
        string currencyCode = Ccy, DateOnly? orderDate = null)
    {
        var result = await BuildController().GetPrice(Supplier, Product, currencyCode, orderDate);
        var data = ((JsonResult)result).Value!;
        var t = data.GetType();

        var found = (bool)t.GetProperty("found")!.GetValue(data)!;
        if (!found) return (false, null, null);

        return (true,
            (decimal)t.GetProperty("UnitPrice")!.GetValue(data)!,
            (decimal?)t.GetProperty("MinQty")!.GetValue(data));
    }

    // ── 生效期間 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPrice_WithinValidPeriod_ReturnsThePrice()
    {
        SeedPrice(125m, validFrom: new DateOnly(2026, 1, 1), validTo: new DateOnly(2026, 12, 31));

        var r = await GetPriceAsync();

        Assert.True(r.Found);
        Assert.Equal(125m, r.UnitPrice);
    }

    /// <summary>訖日留空 ＝ 無期限有效（模型註解明寫這是合法狀態）。</summary>
    [Fact]
    public async Task GetPrice_OpenEndedPeriod_ReturnsThePrice()
    {
        SeedPrice(125m, validFrom: new DateOnly(2026, 1, 1), validTo: null);

        Assert.Equal(125m, (await GetPriceAsync()).UnitPrice);
    }

    /// <summary>
    /// 過期與未生效都必須查不到。這兩個方向要分開釘：
    /// 區間比對寫錯常常只壞一邊（把 &lt;= 寫成 &lt; 只影響邊界日，
    /// 把兩個條件寫成 OR 則兩邊都失效而「大部分情況看起來正常」）。
    /// </summary>
    [Fact]
    public async Task GetPrice_ExpiredPrice_IsNotFound()
    {
        SeedPrice(125m, validFrom: new DateOnly(2025, 1, 1), validTo: new DateOnly(2025, 12, 31));

        Assert.False((await GetPriceAsync()).Found);
    }

    [Fact]
    public async Task GetPrice_NotYetEffectivePrice_IsNotFound()
    {
        SeedPrice(125m, validFrom: new DateOnly(2027, 1, 1));

        Assert.False((await GetPriceAsync()).Found);
    }

    /// <summary>邊界日含當日（<c>ValidFrom &lt;= date &amp;&amp; ValidTo &gt;= date</c>，兩端都是閉區間）。</summary>
    [Fact]
    public async Task GetPrice_OnTheBoundaryDates_IsIncluded()
    {
        SeedPrice(125m, validFrom: Today, validTo: Today);

        Assert.Equal(125m, (await GetPriceAsync()).UnitPrice);
    }

    /// <summary>
    /// 單價是**按單據日期**查的，不是按今天。後補一張上個月的採購單時，
    /// 要套用的是當時談的價——這是 `orderDate` 參數存在的唯一理由，
    /// 而它很容易在重構時被當成多餘參數拿掉（拿掉之後只有補單會錯）。
    /// </summary>
    [Fact]
    public async Task GetPrice_UsesTheOrderDate_NotToday()
    {
        SeedPrice(100m, validFrom: new DateOnly(2026, 1, 1),  validTo: new DateOnly(2026, 6, 30));
        SeedPrice(125m, validFrom: new DateOnly(2026, 7, 1));

        Assert.Equal(100m, (await GetPriceAsync(orderDate: new DateOnly(2026, 3, 15))).UnitPrice);
        Assert.Equal(125m, (await GetPriceAsync()).UnitPrice);   // orderDate 省略 → 取今天
    }

    // ── 停用與幣別 ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPrice_InactivePrice_IsNotFound()
    {
        SeedPrice(125m, validFrom: new DateOnly(2026, 1, 1), isActive: false);

        Assert.False((await GetPriceAsync()).Found);
    }

    /// <summary>
    /// 🚨 幣別必須完全相符，不得退回其他幣別的價。
    /// 帶錯幣別的後果是金額差一個匯率倍數，而數字本身看起來是個正常的單價
    /// ——採購單上不會有任何異常，要到 AP 對帳才發現。
    /// </summary>
    [Fact]
    public async Task GetPrice_PriceInAnotherCurrency_IsNotFound()
    {
        SeedPrice(4m, validFrom: new DateOnly(2026, 1, 1), currencyCode: "USD");

        Assert.False((await GetPriceAsync(currencyCode: "TWD")).Found);
    }

    // ── 多筆命中 ────────────────────────────────────────────────────────

    /// <summary>
    /// 同一組（廠商×品號×幣別）可以有多段生效期間重疊（唯一鍵含 ValidFrom），
    /// 命中多筆時取**生效起日最新**的那一筆＝最近一次談的價。
    /// seed 順序刻意讓舊價在後，這樣「沒排序、直接取第一筆」會紅。
    /// </summary>
    [Fact]
    public async Task GetPrice_MultipleValidPrices_TakesTheLatestValidFrom()
    {
        SeedPrice(100m, validFrom: new DateOnly(2026, 1, 1));
        SeedPrice(125m, validFrom: new DateOnly(2026, 9, 1));
        SeedPrice(110m, validFrom: new DateOnly(2026, 5, 1));

        Assert.Equal(125m, (await GetPriceAsync()).UnitPrice);
    }

    /// <summary>
    /// `MinQty` 要隨單價一起回傳。⚠️ 採購單前端目前**收了但沒有用**
    /// （低於最小訂購量照樣帶這個價、無提示），所以這一支守的是 API 契約，
    /// 不是畫面行為——前端要補提示時不必再改後端。
    /// </summary>
    [Fact]
    public async Task GetPrice_ReturnsMinQtyAlongsideThePrice()
    {
        SeedPrice(125m, validFrom: new DateOnly(2026, 1, 1), minQty: 500m);

        var r = await GetPriceAsync();

        Assert.Equal(125m, r.UnitPrice);
        Assert.Equal(500m, r.MinQty);
    }

    // ── 權限 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 議定單價屬採購敏感資料：兩個合法呼叫端（VendorPriceList / PurchaseOrder）
    /// 都沒有讀權限時必須擋下。
    ///
    /// ⚠️ 這一擋在畫面上是**靜默**的：前端 `.catch(() => {})` 會把 403 吞掉，
    /// 使用者只會覺得「主檔明明建好了卻不生效」。擋是對的，但這條測試同時是
    /// 那個症狀的文件——排查時先看角色權限，不要先去查生效日期。
    /// </summary>
    [Fact]
    public async Task GetPrice_WithoutReadPermissionOnEitherScreen_IsForbidden()
    {
        SeedPrice(125m, validFrom: new DateOnly(2026, 1, 1));

        var result = await BuildController(canRead: false)
            .GetPrice(Supplier, Product, Ccy, orderDate: null);

        Assert.IsType<ForbidResult>(result);
    }
}
