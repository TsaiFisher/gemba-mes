using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

public class CurrencyServiceTests : TestBase
{
    private readonly Mock<ISystemParamService> _mockSysParam;
    private readonly IMemoryCache _cache;

    public CurrencyServiceTests()
    {
        _mockSysParam = new Mock<ISystemParamService>();
        _mockSysParam.Setup(x => x.GetAsync(ProjectZSystemParamKeys.BaseCurrency))
                     .ReturnsAsync("TWD");
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    private CurrencyService CreateService() =>
        new CurrencyService(_context, _mockSysParam.Object, _cache);

    private async Task SeedCurrenciesAndRates()
    {
        _context.Currencies.AddRange(
            new Currency { CurrencyCode = "TWD", CurrencyName = "新台幣",  Symbol = "NT$", DecimalPlaces = 0, IsActive = true },
            new Currency { CurrencyCode = "USD", CurrencyName = "美元",    Symbol = "$",   DecimalPlaces = 2, IsActive = true },
            new Currency { CurrencyCode = "JPY", CurrencyName = "日圓",    Symbol = "¥",   DecimalPlaces = 0, IsActive = true },
            new Currency { CurrencyCode = "EUR", CurrencyName = "歐元",    Symbol = "€",   DecimalPlaces = 2, IsActive = false });
        _context.CurrencyRates.AddRange(
            // USD rates (1 USD = X TWD)
            new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2025, 1,  1), BuyRate = 30.50m, SellRate = 30.80m },
            new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2025, 3,  1), BuyRate = 31.25m, SellRate = 31.50m },
            new CurrencyRate { CurrencyCode = "USD", RateDate = new DateOnly(2025, 6,  1), BuyRate = 32.00m, SellRate = 32.30m },
            // JPY rates (1 JPY = X TWD)
            new CurrencyRate { CurrencyCode = "JPY", RateDate = new DateOnly(2025, 1,  1), BuyRate = 0.25m,  SellRate = 0.26m   });
        await _context.SaveChangesAsync();
    }

    // ── GetBaseCurrencyAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetBaseCurrencyAsync_ReturnsSysParamValue()
    {
        var svc = CreateService();
        var result = await svc.GetBaseCurrencyAsync();
        Assert.Equal("TWD", result);
    }

    // ── GetActiveCurrenciesAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetActiveCurrenciesAsync_ReturnsOnlyActive()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var list = await svc.GetActiveCurrenciesAsync();

        Assert.Equal(3, list.Count);                    // TWD, USD, JPY（EUR inactive）
        Assert.DoesNotContain(list, c => c.CurrencyCode == "EUR");
    }

    // ── GetRateAsync — 本幣捷徑 ───────────────────────────────────────────────

    [Fact]
    public async Task GetRateAsync_BaseCurrency_ReturnsOne()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("TWD", new DateOnly(2025, 5, 1));

        Assert.Equal(1m, rate);
    }

    // ── GetRateAsync — 取最近日期 ─────────────────────────────────────────────

    [Fact]
    public async Task GetRateAsync_ExactDate_ReturnsCorrectRate()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("USD", new DateOnly(2025, 3, 1), RateType.Buy);

        Assert.Equal(31.25m, rate);
    }

    [Fact]
    public async Task GetRateAsync_BetweenDates_ReturnsMostRecentPrior()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        // 2025-04-15 介於 3-01 和 6-01 之間，應取 3-01 那筆
        var rate = await svc.GetRateAsync("USD", new DateOnly(2025, 4, 15), RateType.Buy);

        Assert.Equal(31.25m, rate);
    }

    [Fact]
    public async Task GetRateAsync_BeforeAllRates_ReturnsNull()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("USD", new DateOnly(2024, 12, 31));

        Assert.Null(rate);
    }

    [Fact]
    public async Task GetRateAsync_SellType_ReturnsSellRate()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("USD", new DateOnly(2025, 3, 1), RateType.Sell);

        Assert.Equal(31.50m, rate);
    }

    [Fact]
    public async Task GetRateAsync_MidType_ReturnsMidRate()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("USD", new DateOnly(2025, 3, 1), RateType.Mid);

        var expected = (31.25m + 31.50m) / 2m;
        Assert.Equal(expected, rate);
    }

    [Fact]
    public async Task GetRateAsync_UnknownCurrency_ReturnsNull()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rate = await svc.GetRateAsync("XYZ", new DateOnly(2025, 3, 1));

        Assert.Null(rate);
    }

    // ── GetRateDateAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetRateDateAsync_ReturnsActualRateDate_NotDocDate()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var rateDate = await svc.GetRateDateAsync("USD", new DateOnly(2025, 4, 15));

        Assert.Equal(new DateOnly(2025, 3, 1), rateDate);  // 使用 3-01 的匯率
    }

    [Fact]
    public async Task GetRateDateAsync_BaseCurrency_ReturnsDocDate()
    {
        var svc = CreateService();
        var docDate = new DateOnly(2025, 5, 1);

        var rateDate = await svc.GetRateDateAsync("TWD", docDate);

        Assert.Equal(docDate, rateDate);
    }

    // ── GetSnapshotAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_ReturnsCorrectSnapshot()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var snap = await svc.GetSnapshotAsync("USD", new DateOnly(2025, 4, 15), RateType.Buy);

        Assert.NotNull(snap);
        Assert.Equal("USD", snap.CurrencyCode);
        Assert.Equal(new DateOnly(2025, 3, 1), snap.RateDate);
        Assert.Equal(31.25m, snap.Rate);
        Assert.Equal(RateType.Buy, snap.RateType);
    }

    [Fact]
    public async Task GetSnapshotAsync_NotFound_ReturnsNull()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var snap = await svc.GetSnapshotAsync("USD", new DateOnly(2024, 1, 1));

        Assert.Null(snap);
    }

    // ── ToForeignAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ToForeignAsync_ConvertsCorrectly_WithDecimalRounding()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        // 100 TWD ÷ 31.25 = 3.20 USD（2 小數位）
        var foreign = await svc.ToForeignAsync(100m, "USD", new DateOnly(2025, 3, 1));

        Assert.Equal(3.20m, foreign);
    }

    [Fact]
    public async Task ToForeignAsync_JPY_RoundsToZeroDecimalPlaces()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        // 100 TWD ÷ 0.25 = 400 JPY（0 小數位）
        var foreign = await svc.ToForeignAsync(100m, "JPY", new DateOnly(2025, 3, 1));

        Assert.Equal(400m, foreign);
    }

    [Fact]
    public async Task ToForeignAsync_NoRate_ReturnsNull()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var foreign = await svc.ToForeignAsync(100m, "USD", new DateOnly(2024, 1, 1));

        Assert.Null(foreign);
    }

    // ── ToBaseAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ToBaseAsync_ConvertsCorrectly()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        // 3.20 USD × 31.25 = 100 TWD（0 小數位）
        var baseAmt = await svc.ToBaseAsync(3.20m, "USD", new DateOnly(2025, 3, 1));

        Assert.Equal(100m, baseAmt);
    }

    [Fact]
    public async Task ToBaseAsync_NoRate_ReturnsNull()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var baseAmt = await svc.ToBaseAsync(100m, "USD", new DateOnly(2024, 1, 1));

        Assert.Null(baseAmt);
    }

    // ── RoundAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundAsync_UsesAwayFromZero()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        // USD 2 位小數，0.5 應進位
        var result = await svc.RoundAsync(1.235m, "USD");
        Assert.Equal(1.24m, result);

        // JPY 0 位小數
        var jpy = await svc.RoundAsync(99.5m, "JPY");
        Assert.Equal(100m, jpy);
    }

    // ── FormatAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FormatAsync_IncludesSymbolAndThousandSeparator()
    {
        await SeedCurrenciesAndRates();
        var svc = CreateService();

        var usd = await svc.FormatAsync(1234.5m, "USD");
        Assert.Equal("$ 1,234.50", usd);

        var jpy = await svc.FormatAsync(1234m, "JPY");
        Assert.Equal("¥ 1,234", jpy);
    }
}
