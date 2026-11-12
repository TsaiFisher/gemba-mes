using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 存貨庫齡分桶計算（roadmap-fin-cst-sub.md §5.3）單元測試。
/// 固定六桶：0–30 / 31–60 / 61–90 / 91–180 / 181–360 / 360+。
/// </summary>
public class InventoryAgingCalculatorTests
{
    [Fact]
    public void Bucketize_Empty_ReturnsSixZeroBuckets()
    {
        var rows = InventoryAgingCalculator.Bucketize(Array.Empty<(int, decimal)>());
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.ItemCount));
        Assert.All(rows, r => Assert.Equal(0m, r.InventoryValue));
        Assert.All(rows, r => Assert.Equal(0m, r.ValuePercent));
        Assert.Equal(new[] { "0–30", "31–60", "61–90", "91–180", "181–360", "360+" },
                     rows.Select(r => r.BucketLabel).ToArray());
    }

    [Theory]
    [InlineData(0, 0)]       // 邊界：今日剛異動 → 首桶
    [InlineData(30, 0)]      // 邊界：30 天 → 首桶
    [InlineData(31, 1)]      // 邊界：31 天 → 第二桶
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(90, 2)]
    [InlineData(91, 3)]
    [InlineData(180, 3)]
    [InlineData(181, 4)]
    [InlineData(360, 4)]
    [InlineData(361, 5)]     // 360+
    [InlineData(9999, 5)]    // 從未異動 → 360+
    [InlineData(-3, 0)]      // 負值（異動日在未來，異常）→ 首桶，不拋例外
    public void Bucketize_IdleDays_MapsToExpectedBucketIndex(int idleDays, int expectedIndex)
    {
        var rows = InventoryAgingCalculator.Bucketize(new[] { (idleDays, 100m) });
        Assert.Equal(1, rows[expectedIndex].ItemCount);
        Assert.Equal(100m, rows[expectedIndex].InventoryValue);
        // 其餘桶皆空
        for (int i = 0; i < rows.Count; i++)
            if (i != expectedIndex) Assert.Equal(0, rows[i].ItemCount);
    }

    [Fact]
    public void Bucketize_AggregatesCountValue_AndComputesPercent()
    {
        // 首桶 2 筆共 300；91–180 桶 1 筆 100；總額 400。
        var rows = InventoryAgingCalculator.Bucketize(new[]
        {
            (10, 200m), (20, 100m),   // 0–30
            (120, 100m),              // 91–180
        });

        Assert.Equal(2, rows[0].ItemCount);
        Assert.Equal(300m, rows[0].InventoryValue);
        Assert.Equal(75m, rows[0].ValuePercent);     // 300/400
        Assert.Equal(1, rows[3].ItemCount);
        Assert.Equal(100m, rows[3].InventoryValue);
        Assert.Equal(25m, rows[3].ValuePercent);     // 100/400
        Assert.Equal(100m, rows.Sum(r => r.ValuePercent));
    }
}
