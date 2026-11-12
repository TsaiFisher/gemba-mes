using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// <see cref="UomService.GetConversionRateAsync"/> 的查找優先序與失敗行為。
///
/// <para><b>這組測試存在的理由：</b>換算率是「交易單位 → 庫存基準單位」的唯一來源，
/// 而基準量又是超量守門的分母（見 <c>test-hardening-plan.md</c> §5-1）。
/// 這裡錯一個數量級，庫存與守門會一起錯，且畫面上單位仍然是對的，看不出來。</para>
/// </summary>
public class UomServiceTests : TestBase
{
    private UomService CreateService() => new(_context, new MemoryCache(new MemoryCacheOptions()));

    private void SeedGlobal(string from, string to, decimal numerator, decimal denominator = 1)
    {
        _context.UomConversions.Add(new UomConversion
        {
            FromUomCode = from, ToUomCode = to, Numerator = numerator, Denominator = denominator
        });
        _context.SaveChanges();
    }

    private void SeedProduct(string product, string from, string to, decimal numerator, decimal denominator = 1)
    {
        _context.ProductUomConversions.Add(new ProductUomConversion
        {
            ProductCode = product, FromUomCode = from, ToUomCode = to,
            Numerator = numerator, Denominator = denominator
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task 相同單位不查表直接回一()
    {
        var rate = await CreateService().GetConversionRateAsync("PRD001", "PC", "pc");
        Assert.Equal(1.0m, rate);
    }

    [Fact]
    public async Task 單位留空時視為不需換算()
    {
        // 採購單位留空的既有行為（見 PurchaseOrderFormVm 註解），不得因本次改動變成例外。
        var svc = CreateService();
        Assert.Equal(1.0m, await svc.GetConversionRateAsync("PRD001", "", "PC"));
        Assert.Equal(1.0m, await svc.GetConversionRateAsync("PRD001", "BOX", ""));
    }

    [Fact]
    public async Task 全域換算_正向與逆向都查得到()
    {
        SeedGlobal("KG", "G", 1000);

        var svc = CreateService();
        Assert.Equal(1000m, await svc.GetConversionRateAsync("PRD001", "KG", "G"));
        Assert.Equal(0.001m, await svc.GetConversionRateAsync("PRD001", "G", "KG"));
    }

    [Fact]
    public async Task 品號特定換算優先於全域()
    {
        // 「一箱幾入」是品號的性質不是單位的性質：全域說 10，A 品號實際是 12。
        SeedGlobal("BOX", "PC", 10);
        SeedProduct("PRD-A", "BOX", "PC", 12);

        var svc = CreateService();
        Assert.Equal(12m, await svc.GetConversionRateAsync("PRD-A", "BOX", "PC"));
        // 沒有品號特定資料的品號仍走全域，不受影響。
        Assert.Equal(10m, await svc.GetConversionRateAsync("PRD-B", "BOX", "PC"));
    }

    [Fact]
    public async Task 品號特定換算的逆向也優先於全域()
    {
        SeedGlobal("BOX", "PC", 10);
        SeedProduct("PRD-A", "BOX", "PC", 12);

        var rate = await CreateService().GetConversionRateAsync("PRD-A", "PC", "BOX");

        Assert.Equal(1m / 12m, rate);
    }

    [Fact]
    public async Task 查不到換算且單位不同時必須拋例外而不是回一()
    {
        // 改動前這裡靜默回 1.0：收貨 10 BOX 記成 10 PC，庫存少一個數量級且無任何徵兆。
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<UomConversionNotFoundException>(
            () => svc.GetConversionRateAsync("PRD001", "BOX", "PC"));

        Assert.Equal("PRD001", ex.ProductCode);
        Assert.Equal("BOX", ex.FromUom);
        Assert.Equal("PC", ex.ToUom);
        // 訊息要能直接指路，否則現場只會回報「存檔失敗」。
        Assert.Contains("BOX", ex.Message);
        Assert.Contains("PC", ex.Message);
    }

    [Fact]
    public async Task ConvertAsync_沿用同一條查找路徑()
    {
        SeedProduct("PRD-A", "BOX", "PC", 12);

        var svc = CreateService();
        Assert.Equal(24m, await svc.ConvertAsync("PRD-A", "BOX", "PC", 2));
        await Assert.ThrowsAsync<UomConversionNotFoundException>(
            () => svc.ConvertAsync("PRD-B", "BOX", "PC", 2));
    }

    [Fact]
    public async Task 分母為零的壞資料不得被當成有效換算()
    {
        // Denominator = 0 會讓 Numerator / Denominator 直接爆 DivideByZero，
        // 所以查找時就要跳過它，並繼續往下一層找。
        SeedProduct("PRD-A", "BOX", "PC", 12, 0);
        SeedGlobal("BOX", "PC", 10);

        var rate = await CreateService().GetConversionRateAsync("PRD-A", "BOX", "PC");

        Assert.Equal(10m, rate);
    }
}
