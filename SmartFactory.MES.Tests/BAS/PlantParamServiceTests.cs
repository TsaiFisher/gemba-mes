using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 廠別參數解析（`plant-scoped-system-params.md` §3.2–3.4）：
/// 本廠覆寫 &gt; 全域值 &gt; null（內建預設由呼叫端套）。
///
/// 快取用**真的** <c>MemoryCache</c> 而非 mock——這裡最容易出錯的不是查詢邏輯，
/// 是「寫入後有沒有把快取清掉」，用 mock 蓋掉就驗不到了。
/// </summary>
public class PlantParamServiceTests : TestBase
{
    private const string PlantA = "P001";
    private const string PlantB = "P002";
    private const string Key = "inv_simplified_mode";

    private readonly Mock<ISystemParamService> _global = new();
    private readonly PlantParamService _svc;

    public PlantParamServiceTests()
    {
        _svc = new PlantParamService(_context, _global.Object, new MemoryCache(new MemoryCacheOptions()));
    }

    private void SeedOverride(string plantId, string? value, string key = Key)
    {
        _context.PlantParams.Add(new PlantParam { Key = key, PlantId = plantId, Value = value });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Get_PlantOverride_Wins()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");
        SeedOverride(PlantA, "true");

        Assert.Equal("true", await _svc.GetAsync(Key, PlantA));
    }

    /// <summary>值的前後空白在讀取端去掉：" true" 原本被各呼叫端的 Equals("true") 當成 false。</summary>
    [Fact]
    public async Task Get_TrimsWhitespace_BothLayers()
    {
        SeedOverride(PlantA, " true ");
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("\tfalse ");

        Assert.Equal("true",  await _svc.GetAsync(Key, PlantA));
        Assert.Equal("false", await _svc.GetAsync(Key, PlantB));
    }

    [Fact]
    public async Task Get_NoOverride_FallsBackToGlobal()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");
        SeedOverride(PlantA, "true");

        // B 廠沒有覆寫 → 吃全域
        Assert.Equal("false", await _svc.GetAsync(Key, PlantB));
    }

    [Fact]
    public async Task Get_NeitherLayerSet_ReturnsNull()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync((string?)null);

        // 兩層都沒有 → null，由呼叫端套程式內建預設（既有規則：未設定＝舊行為）
        Assert.Null(await _svc.GetAsync(Key, PlantA));
    }

    /// <summary>
    /// 「該廠明確設為空值」與「該廠沒有覆寫」是兩件事：前者有列、值為 null，
    /// 必須回 null 而**不是**回頭吃全域值，否則三態就塌回兩態了。
    /// </summary>
    [Fact]
    public async Task Get_ExplicitNullOverride_DoesNotFallBackToGlobal()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");
        SeedOverride(PlantA, null);

        Assert.Null(await _svc.GetAsync(Key, PlantA));
    }

    [Fact]
    public async Task Get_NoPlantContext_UsesGlobal()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");
        SeedOverride(PlantA, "true");

        Assert.Equal("false", await _svc.GetAsync(Key, null));
        Assert.Equal("false", await _svc.GetAsync(Key, ""));
    }

    [Fact]
    public async Task HasOverride_DistinguishesInheritFromOverride()
    {
        SeedOverride(PlantA, null);   // 明確設空，仍算覆寫

        Assert.True(await _svc.HasOverrideAsync(Key, PlantA));
        Assert.False(await _svc.HasOverrideAsync(Key, PlantB));
        Assert.False(await _svc.HasOverrideAsync(Key, null));
    }

    [Fact]
    public async Task GetOverrides_ReturnsOnlyThatPlant()
    {
        SeedOverride(PlantA, "true");
        SeedOverride(PlantB, "false");
        SeedOverride(PlantA, "FIFO", "inv_default_cost_method");

        var a = await _svc.GetOverridesAsync(PlantA);

        Assert.Equal(2, a.Count);
        Assert.Equal("true", a[Key]);
        Assert.Equal("FIFO", a["inv_default_cost_method"]);
    }

    [Fact]
    public async Task Set_ThenGet_SeesNewValue_CacheInvalidated()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");

        // 先讀一次把快取暖起來——沒有清快取的話，下面那次讀會拿到舊的 "false"
        Assert.Equal("false", await _svc.GetAsync(Key, PlantA));

        await _svc.SetAsync(Key, PlantA, "true");

        Assert.Equal("true", await _svc.GetAsync(Key, PlantA));
    }

    [Fact]
    public async Task Set_Twice_UpdatesInPlace()
    {
        await _svc.SetAsync(Key, PlantA, "true");
        await _svc.SetAsync(Key, PlantA, "false");

        Assert.Equal("false", await _svc.GetAsync(Key, PlantA));
        Assert.Single(_context.PlantParams.Where(p => p.Key == Key && p.PlantId == PlantA));
    }

    [Fact]
    public async Task Clear_ReturnsToInheritingGlobal()
    {
        _global.Setup(g => g.GetAsync(Key)).ReturnsAsync("false");
        await _svc.SetAsync(Key, PlantA, "true");
        Assert.Equal("true", await _svc.GetAsync(Key, PlantA));

        await _svc.ClearAsync(Key, PlantA);

        Assert.False(await _svc.HasOverrideAsync(Key, PlantA));
        Assert.Equal("false", await _svc.GetAsync(Key, PlantA));
    }

    [Fact]
    public async Task Clear_NonExistent_IsNoOp()
    {
        await _svc.ClearAsync(Key, PlantA);   // 不應拋例外

        Assert.False(await _svc.HasOverrideAsync(Key, PlantA));
    }
}
