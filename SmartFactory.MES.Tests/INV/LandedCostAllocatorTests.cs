using SmartFactory.MES.Modules.INV.Services;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// §3.4 採購分攤費用（Landed Cost）分攤計算器 — EP/BVA 測試。
/// 涵蓋三種分攤基準（金額/數量/重量）、尾差補位、新單價回算與邊界/無效輸入。
/// </summary>
public class LandedCostAllocatorTests
{
    private static LandedCostInputLine Line(decimal amount, decimal qty, decimal weight, decimal unitCost = 0m)
        => new() { PurchaseAmount = amount, Quantity = qty, Weight = weight, OriginalUnitCost = unitCost };

    // ── EP Valid：依金額分攤 ─────────────────────────────────────────────────
    [Fact]
    public void Allocate_ByAmount_SplitsProportionally()
    {
        var lines = new[]
        {
            Line(amount: 600m, qty: 60m, weight: 10m, unitCost: 10m),
            Line(amount: 400m, qty: 40m, weight: 30m, unitCost: 10m),
        };

        var r = LandedCostAllocator.Allocate(1000m, lines, LandedCostBasis.Amount);

        Assert.Equal(600m, r[0].AllocatedCost);  // 1000 × 600/1000
        Assert.Equal(400m, r[1].AllocatedCost);  // 1000 × 400/1000
        Assert.Equal(1000m, r[0].AllocatedCost + r[1].AllocatedCost);
        // 新單價 = 原 10 + 分攤/數量
        Assert.Equal(10m + 600m / 60m, r[0].NewUnitCost);  // 20
        Assert.Equal(10m + 400m / 40m, r[1].NewUnitCost);  // 20
    }

    // ── EP Valid：依數量分攤 ─────────────────────────────────────────────────
    [Fact]
    public void Allocate_ByQuantity_SplitsProportionally()
    {
        var lines = new[]
        {
            Line(amount: 999m, qty: 30m, weight: 1m),
            Line(amount: 1m,   qty: 70m, weight: 1m),
        };

        var r = LandedCostAllocator.Allocate(1000m, lines, LandedCostBasis.Quantity);

        Assert.Equal(300m, r[0].AllocatedCost);  // 1000 × 30/100
        Assert.Equal(700m, r[1].AllocatedCost);  // 1000 × 70/100
    }

    // ── EP Valid：依重量分攤 ─────────────────────────────────────────────────
    [Fact]
    public void Allocate_ByWeight_SplitsProportionally()
    {
        var lines = new[]
        {
            Line(amount: 1m, qty: 1m, weight: 25m),
            Line(amount: 1m, qty: 1m, weight: 75m),
        };

        var r = LandedCostAllocator.Allocate(800m, lines, LandedCostBasis.Weight);

        Assert.Equal(200m, r[0].AllocatedCost);  // 800 × 25/100
        Assert.Equal(600m, r[1].AllocatedCost);  // 800 × 75/100
    }

    // ── BVA：尾差補進權重最大行，Σ 必等於總額 ───────────────────────────────
    [Fact]
    public void Allocate_RoundingResidual_GoesToLargestWeightLine_SumExact()
    {
        // 100 元分給 3 行等權，10/3 無法整除 → 33.3333 × 3 = 99.9999，尾差 0.0001 補最大權重行
        var lines = new[]
        {
            Line(amount: 50m, qty: 1m, weight: 1m),  // 最大權重（金額基準）
            Line(amount: 25m, qty: 1m, weight: 1m),
            Line(amount: 25m, qty: 1m, weight: 1m),
        };

        var r = LandedCostAllocator.Allocate(100m, lines, LandedCostBasis.Amount);

        Assert.Equal(100m, r[0].AllocatedCost + r[1].AllocatedCost + r[2].AllocatedCost);
        Assert.Equal(50m, r[0].AllocatedCost);  // 100 × 50/100
    }

    [Fact]
    public void Allocate_ThreeEqualWeights_SumStillExact()
    {
        var lines = new[]
        {
            Line(amount: 1m, qty: 1m, weight: 1m),
            Line(amount: 1m, qty: 1m, weight: 1m),
            Line(amount: 1m, qty: 1m, weight: 1m),
        };

        var r = LandedCostAllocator.Allocate(100m, lines, LandedCostBasis.Amount);

        Assert.Equal(100m, r.Sum(x => x.AllocatedCost));   // 33.3333×3 + 尾差 = 100
    }

    // ── BVA：總額為 0 → 全部分攤 0，新單價維持原值 ──────────────────────────
    [Fact]
    public void Allocate_ZeroTotal_AllZero_UnitCostUnchanged()
    {
        var lines = new[] { Line(amount: 100m, qty: 10m, weight: 1m, unitCost: 5m) };

        var r = LandedCostAllocator.Allocate(0m, lines, LandedCostBasis.Amount);

        Assert.Equal(0m, r[0].AllocatedCost);
        Assert.Equal(5m, r[0].NewUnitCost);
    }

    // ── BVA：數量為 0 的行 → 新單價維持原值（不可除以零）──────────────────────
    [Fact]
    public void Allocate_LineWithZeroQty_NewUnitCostKeepsOriginal()
    {
        var lines = new[]
        {
            Line(amount: 100m, qty: 0m,  weight: 1m, unitCost: 7m),
            Line(amount: 100m, qty: 10m, weight: 1m, unitCost: 7m),
        };

        var r = LandedCostAllocator.Allocate(200m, lines, LandedCostBasis.Amount);

        Assert.Equal(100m, r[0].AllocatedCost);  // 仍分攤金額
        Assert.Equal(7m, r[0].NewUnitCost);      // 但無法回算單價 → 維持原值
        Assert.Equal(7m + 100m / 10m, r[1].NewUnitCost);  // 17
    }

    // ── EP Invalid ───────────────────────────────────────────────────────────
    [Fact]
    public void Allocate_NegativeTotal_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => LandedCostAllocator.Allocate(-1m, new[] { Line(1m, 1m, 1m) }, LandedCostBasis.Amount));

    [Fact]
    public void Allocate_EmptyLines_Throws()
        => Assert.Throws<ArgumentException>(
            () => LandedCostAllocator.Allocate(100m, Array.Empty<LandedCostInputLine>(), LandedCostBasis.Amount));

    [Fact]
    public void Allocate_ZeroWeightSum_Throws()
    {
        // 依重量分攤，但所有行重量為 0 → 無法分攤
        var lines = new[] { Line(amount: 100m, qty: 1m, weight: 0m), Line(amount: 100m, qty: 1m, weight: 0m) };
        Assert.Throws<InvalidOperationException>(
            () => LandedCostAllocator.Allocate(100m, lines, LandedCostBasis.Weight));
    }
}
