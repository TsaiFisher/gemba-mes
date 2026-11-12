using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// Phase 4-1 精度邊界表。
///
/// 全系統的數量精度由 <see cref="IUomService.RoundQtyAsync"/> 決定、金額精度由
/// <see cref="ICurrencyService.RoundAsync"/> 決定，兩者都是「查主檔小數位數 →
/// Math.Round(AwayFromZero)」。所有單據存檔前都會呼叫它們，因此這兩支的邊界行為
/// 等於全系統的邊界行為——這裡用等價劃分把輸入切成
/// 〈一般值 / 中點值 / 超精度值 / 負值〉，把小數位數切成
/// 〈0 / 中間 / 儲存上限 / 超出上限 / 未定義 / 已停用 / 非法〉，逐格釘住。
///
/// 儲存上限 4 位的依據：全庫金額與數量欄位以 numeric(18,4) 為主（241 欄），
/// 應用層算得比 4 位更細也存不進去，PostgreSQL 會再無聲四捨五入一次。
/// </summary>
public class PrecisionBoundaryTests : TestBase
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ISystemParamService> _mockSysParam = new();

    public PrecisionBoundaryTests()
    {
        _mockSysParam.Setup(x => x.GetAsync(ProjectZSystemParamKeys.BaseCurrency))
                     .ReturnsAsync("TWD");
    }

    private UomService CreateUomService() => new(_context, _cache);
    private CurrencyService CreateCurrencyService() => new(_context, _mockSysParam.Object, _cache);

    private async Task SeedUomAsync(string code, int dp, bool isActive = true)
    {
        _context.Uoms.Add(new Uom
        {
            UomCode = code,
            UomName = code,
            DecimalPlaces = dp,
            IsActive = isActive
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedCurrencyAsync(string code, int dp, bool isActive = true)
    {
        _context.Currencies.Add(new Currency
        {
            CurrencyCode = code,
            CurrencyName = code,
            Symbol = code,
            DecimalPlaces = dp,
            IsActive = isActive
        });
        await _context.SaveChangesAsync();
    }

    // 小數字面值不能當 attribute 常數（C# 只認 double），一律以字串傳入再轉 decimal，
    // 免得 0.05 這種在 double 裡不精確的值在進到 Math.Round 之前就已經失真。
    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    // ── A. 數量：小數位數 × 輸入值 的邊界矩陣 ────────────────────────────────

    [Theory]
    // dp=0（PCS 型：整數單位）
    [InlineData(0, "0.4", "0")]
    [InlineData(0, "0.5", "1")]          // 中點：AwayFromZero，不是銀行家捨入的 0
    [InlineData(0, "1.5", "2")]          // 中點：銀行家捨入會給 2（偶數），這裡剛好同值
    [InlineData(0, "2.5", "3")]          // 中點：銀行家捨入會給 2，這格才分得出兩者
    [InlineData(0, "-0.5", "-1")]        // 負中點：AwayFromZero 往負方向遠離 0
    [InlineData(0, "0.9999", "1")]
    // dp=1
    [InlineData(1, "0.04", "0")]
    [InlineData(1, "0.05", "0.1")]       // 中點
    [InlineData(1, "0.15", "0.2")]       // 中點：銀行家捨入會給 0.2；下一格才分得出
    [InlineData(1, "0.25", "0.3")]       // 中點：銀行家捨入會給 0.2
    [InlineData(1, "-0.05", "-0.1")]
    // dp=4（儲存上限）
    [InlineData(4, "0.00004", "0")]
    [InlineData(4, "0.00005", "0.0001")] // 中點
    [InlineData(4, "1.23455", "1.2346")] // 超精度：第 5 位進位
    [InlineData(4, "1.23454", "1.2345")] // 超精度：第 5 位捨去
    [InlineData(4, "-0.00005", "-0.0001")]
    // 零與極大值：不因四捨五入而改變
    [InlineData(4, "0", "0")]
    [InlineData(4, "99999999999999.9999", "99999999999999.9999")]
    public async Task RoundQtyAsync_MatchesPrecisionBoundaryTable(int dp, string input, string expected)
    {
        await SeedUomAsync("U", dp);
        var svc = CreateUomService();

        var actual = await svc.RoundQtyAsync("U", D(input));

        Assert.Equal(D(expected), actual);
    }

    /// <summary>
    /// 中點進位必須是 AwayFromZero，不能是 .NET 預設的 ToEven（銀行家捨入）。
    /// 這條單獨拉出來，是因為 <c>Math.Round(x, dp)</c> 少寫第三個參數就會靜默切成
    /// ToEven，而 2.5 → 2 這種結果在單筆看起來完全合理，只有累計對帳時才會浮現。
    /// 前端 <c>currency-format.js</c> 的 roundHalfAway 也對齊同一約定。
    /// </summary>
    [Fact]
    public async Task RoundQtyAsync_UsesAwayFromZero_NotBankersRounding()
    {
        await SeedUomAsync("U", 0);
        var svc = CreateUomService();

        Assert.Equal(3m, await svc.RoundQtyAsync("U", 2.5m));   // ToEven 會給 2
        Assert.Equal(1m, await svc.RoundQtyAsync("U", 0.5m));   // ToEven 會給 0
        Assert.Equal(-3m, await svc.RoundQtyAsync("U", -2.5m)); // ToEven 會給 -2
    }

    // ── B. 數量：小數位數本身的等價類 ────────────────────────────────────────

    /// <summary>
    /// 未定義單位（空字串 / null / 查無此代碼）一律當 dp=0。
    ///
    /// 注意 <see cref="IUomService.RoundQtyAsync"/> 的 XML 註解寫的是
    /// 「空代碼或找不到時回傳原值」，與實作不符——實作走
    /// <c>GetDecimalPlacesAsync</c> 的 0 回退，等於把小數直接四捨五入掉。
    /// 這裡釘住「實作」而非註解：呼叫端已經全部依實作行為運作，改成回原值反而會讓
    /// 未設定單位的品項繞過所有精度控制。註解已一併修正。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NO-SUCH-UOM")]
    public async Task RoundQtyAsync_UndefinedUom_FallsBackToIntegerPrecision(string? uomCode)
    {
        await SeedUomAsync("U", 4);
        var svc = CreateUomService();

        Assert.Equal(2m, await svc.RoundQtyAsync(uomCode, 1.5m));
    }

    /// <summary>
    /// 停用的單位仍須保有原小數位數。
    ///
    /// 小數位數是「這個單位怎麼記數」的性質，不是「這個單位還能不能挑選」。
    /// 過去 dp 對照表跟著下拉選單一起濾 IsActive，於是把 KG（dp=4）停用之後，
    /// 所有既有單據重新存檔時 1.2345 KG 會被當成整數單位捨成 1——庫存憑空少掉
    /// 0.2345，而且畫面上單位還是 KG，看不出任何異狀。
    /// IsActive 只該擋在下拉選單那一層。
    /// </summary>
    [Fact]
    public async Task RoundQtyAsync_InactiveUom_KeepsItsDecimalPlaces()
    {
        await SeedUomAsync("KG", 4, isActive: false);
        var svc = CreateUomService();

        Assert.Equal(1.2345m, await svc.RoundQtyAsync("KG", 1.2345m));
        Assert.Equal(4, await svc.GetDecimalPlacesAsync("KG"));
    }

    /// <summary>
    /// 主檔裡的非法小數位數不得讓四捨五入整個炸掉。
    ///
    /// <c>Math.Round(qty, -1)</c> 會丟 ArgumentOutOfRangeException，而它是在存檔路徑
    /// 深處被呼叫的——一筆負數小數位數的單位主檔，會讓所有用到該單位的單據存檔
    /// 一律 500。輸入端已加 [Range(0,4)] 擋住，這裡守的是「資料已經爛掉」的情形。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-99)]
    public async Task RoundQtyAsync_NegativeDecimalPlacesInMaster_DoesNotThrow(int dp)
    {
        await SeedUomAsync("BAD", dp);
        var svc = CreateUomService();

        var actual = await svc.RoundQtyAsync("BAD", 1.5m);

        Assert.Equal(2m, actual);   // 退回 dp=0
    }

    /// <summary>
    /// 超過儲存上限的小數位數要夾回 4 位。
    ///
    /// 金額與數量欄位是 numeric(18,4)，dp 設到 6 時應用層會算出 1.234567，
    /// 送進 PostgreSQL 又被無聲四捨五入成 1.2346——「應用層算的」與「資料庫存的」
    /// 從此不同值，之後任何以應用層重算去比對 DB 的稽核都會出現無法解釋的尾差。
    /// 夾在應用層，兩邊才會是同一個數。
    /// </summary>
    [Fact]
    public async Task RoundQtyAsync_DecimalPlacesBeyondStorage_ClampedToColumnScale()
    {
        await SeedUomAsync("OVER", 6);
        var svc = CreateUomService();

        Assert.Equal(1.2346m, await svc.RoundQtyAsync("OVER", 1.2345678m));
    }

    /// <summary>
    /// 主檔輸入端的小數位數範圍守門：0–4 合法，負值與超出儲存上限一律擋下。
    /// 畫面上的 <c>min="0" max="4"</c> 只是瀏覽器提示，直接 POST 就繞過了，
    /// 真正的防線必須在模型上。
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(-1, false)]
    [InlineData(5, false)]
    [InlineData(10, false)]
    public void DecimalPlaces_ServerSideRangeGuard(int dp, bool expectValid)
    {
        AssertDpValidity(new Uom { UomCode = "U", UomName = "U", DecimalPlaces = dp }, expectValid);
        AssertDpValidity(new Currency { CurrencyCode = "C", CurrencyName = "C", DecimalPlaces = dp }, expectValid);
    }

    private static void AssertDpValidity(object model, bool expectValid)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

        var dpInvalid = results.Any(r => r.MemberNames.Contains(nameof(Uom.DecimalPlaces)));
        Assert.Equal(expectValid, !dpInvalid);
    }

    // ── C. 金額：幣別小數位數的邊界矩陣 ──────────────────────────────────────

    [Theory]
    // dp=0（日圓型：不記小數）
    [InlineData(0, "1234.4", "1234")]
    [InlineData(0, "1234.5", "1235")]     // 中點
    [InlineData(0, "-1234.5", "-1235")]
    // dp=2（最常見）
    [InlineData(2, "1.004", "1.00")]
    [InlineData(2, "1.005", "1.01")]      // 中點：double 會因 1.005 存不精確而給 1.00
    [InlineData(2, "-1.005", "-1.01")]
    // dp=4（儲存上限）
    [InlineData(4, "1.00004", "1.0000")]
    [InlineData(4, "1.00005", "1.0001")]  // 中點
    [InlineData(4, "0", "0")]
    public async Task RoundAsync_MatchesPrecisionBoundaryTable(int dp, string input, string expected)
    {
        await SeedCurrencyAsync("X", dp);
        var svc = CreateCurrencyService();

        var actual = await svc.RoundAsync(D(input), "X");

        Assert.Equal(D(expected), actual);
    }

    /// <summary>未建立的幣別回退 2 位（與 CurrencyStepHelper.DefaultDp 一致）。</summary>
    [Fact]
    public async Task RoundAsync_UnknownCurrency_FallsBackToTwoDecimalPlaces()
    {
        var svc = CreateCurrencyService();

        Assert.Equal(1.24m, await svc.RoundAsync(1.2356m, "NO-SUCH-CURRENCY"));
    }

    /// <summary>
    /// 停用的幣別同樣要保有原小數位數，理由與停用單位相同：
    /// 停用日圓之後，既有日圓單據的金額會從 dp=0 變成 dp=2，
    /// 對外幣帳的重算結果整批位移。
    /// </summary>
    [Fact]
    public async Task RoundAsync_InactiveCurrency_KeepsItsDecimalPlaces()
    {
        await SeedCurrencyAsync("JPY", 0, isActive: false);
        var svc = CreateCurrencyService();

        Assert.Equal(1235m, await svc.RoundAsync(1234.5m, "JPY"));
    }

    /// <summary>停用幣別不得再出現在下拉選單——IsActive 該擋的是這一層。</summary>
    [Fact]
    public async Task GetActiveCurrenciesAsync_StillExcludesInactive()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("JPY", 0, isActive: false);
        var svc = CreateCurrencyService();

        var list = await svc.GetActiveCurrenciesAsync();

        Assert.Single(list);
        Assert.Equal("TWD", list[0].CurrencyCode);
    }

    // ── D. 匯率：極值與方向 ──────────────────────────────────────────────────

    private async Task SeedRateAsync(string code, decimal buy, decimal sell)
    {
        _context.CurrencyRates.Add(new CurrencyRate
        {
            CurrencyCode = code,
            RateDate = new DateOnly(2026, 1, 1),
            BuyRate = buy,
            SellRate = sell
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 匯率方向：ExchangeRate = 本幣/外幣，本幣 = 外幣 × Rate（乘法）。
    /// 方向寫反時金額差的是 Rate²，在 Rate≈1 的幣別上幾乎看不出來，
    /// 所以這裡刻意用 Rate=32 讓兩個方向差三個數量級。
    /// </summary>
    [Fact]
    public async Task ExchangeRate_DirectionIsMultiplyToBase()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("USD", 2);
        await SeedRateAsync("USD", 32m, 32m);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        Assert.Equal(3200m, await svc.ToBaseAsync(100m, "USD", date));
        Assert.Equal(3.13m, await svc.ToForeignAsync(100m, "USD", date));   // 100 / 32 = 3.125 → 3.13
    }

    /// <summary>
    /// 極小匯率：除法會把金額放大四個數量級，且結果仍須依外幣小數位數收斂。
    /// </summary>
    [Fact]
    public async Task ExchangeRate_ExtremelySmallRate_ConvertsWithoutOverflow()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("TINY", 4);
        await SeedRateAsync("TINY", 0.0001m, 0.0001m);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        Assert.Equal(1_000_000m, await svc.ToForeignAsync(100m, "TINY", date));
        Assert.Equal(0.01m, await svc.ToBaseAsync(100m, "TINY", date));
    }

    /// <summary>極大匯率：乘法方向不得溢位，且本幣仍收斂到本幣位數。</summary>
    [Fact]
    public async Task ExchangeRate_ExtremelyLargeRate_ConvertsWithoutOverflow()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("HUGE", 4);
        await SeedRateAsync("HUGE", 1_000_000m, 1_000_000m);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        Assert.Equal(100_000_000m, await svc.ToBaseAsync(100m, "HUGE", date));
        Assert.Equal(0.0001m, await svc.ToForeignAsync(100m, "HUGE", date));
    }

    /// <summary>
    /// 匯率 0 與負數視同「沒有匯率」而非「換算結果為 0」。
    /// 回 0 的話單據金額會整張變成 0 卻照樣過帳；回 null 才會被呼叫端擋下來要求先建匯率。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-32)]
    public async Task GetRateAsync_NonPositiveRate_TreatedAsMissing(int rate)
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("BAD", 2);
        await SeedRateAsync("BAD", rate, rate);
        var svc = CreateCurrencyService();

        Assert.Null(await svc.GetRateAsync("BAD", new DateOnly(2026, 6, 1)));
        Assert.Null(await svc.ToBaseAsync(100m, "BAD", new DateOnly(2026, 6, 1)));
    }

    /// <summary>
    /// 換算往返不是等冪的——本幣 → 外幣 → 本幣會因兩次四捨五入而回不到原值。
    ///
    /// 釘住這條是因為它常被誤當成 bug 回報：3200 TWD 換成 100 USD 再換回來是 3200，
    /// 但 3210 換過去是 100.31 USD、換回來變成 3209.92。任何「重新換算一次來核對
    /// 原始金額」的邏輯都會踩到，正確做法是存匯率快照、以快照重算而非往返。
    /// </summary>
    [Fact]
    public async Task ExchangeRate_RoundTrip_IsNotLossless()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("USD", 2);
        await SeedRateAsync("USD", 32m, 32m);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        var foreign = await svc.ToForeignAsync(3210m, "USD", date);
        var back = await svc.ToBaseAsync(foreign!.Value, "USD", date);

        Assert.Equal(100.31m, foreign);
        Assert.Equal(3209.92m, back);
        Assert.NotEqual(3210m, back);
    }

    /// <summary>
    /// 中間匯率兩邊都有值時才算得出來。
    ///
    /// 原本 `Rate > 0` 的守門發生在**算完平均之後**，於是只維護單邊時
    /// `(Buy + 0) / 2` 得到「單邊的一半」，那個值大於 0、順利通過守門一路用下去：
    /// 成本與存貨評價（規格指定用 Mid）直接少一半，而且沒有任何地方會報錯。
    /// 現在缺一邊回 null，與「查無匯率」走同一條路徑，由呼叫端擋下單據。
    /// </summary>
    [Theory]
    [InlineData(0, 32, null)]      // 只有賣匯：原本回 16
    [InlineData(30, 0, null)]      // 只有買匯：原本回 15
    [InlineData(0, 0, null)]       // 兩邊皆無
    [InlineData(30, 32, 31)]       // 正常：仍取平均
    public async Task GetRateAsync_MidType_RequiresBothLegs(int buy, int sell, int? expected)
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("MID", 2);
        await SeedRateAsync("MID", buy, sell);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        Assert.Equal((decimal?)expected, await svc.GetRateAsync("MID", date, RateType.Mid));
    }

    /// <summary>單邊守門本來就正確，改動不得波及：買匯 0 取 Buy 仍是 null，賣匯有值取 Sell 仍拿得到。</summary>
    [Fact]
    public async Task GetRateAsync_SingleLegTypes_Unaffected()
    {
        await SeedCurrencyAsync("TWD", 2);
        await SeedCurrencyAsync("HALF", 2);
        await SeedRateAsync("HALF", 0m, 32m);
        var svc = CreateCurrencyService();
        var date = new DateOnly(2026, 6, 1);

        Assert.Null(await svc.GetRateAsync("HALF", date, RateType.Buy));
        Assert.Equal(32m, await svc.GetRateAsync("HALF", date, RateType.Sell));
    }
}
