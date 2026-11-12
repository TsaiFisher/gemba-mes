using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.PUR.Services;
using MU = SmartFactory.MES.Modules.PUR.Services.OutsourceScrapCalculator.MaterialUsage;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// §5.2 委外收貨與超耗扣款資料流 — EP/BVA 測試。
/// 對應規格書 §5.2 全部 7 個案例（3 EP Valid + 1 EP Invalid + 3 BVA）。
/// </summary>
public class OutsourceScrapCalculatorTests
{
    // 基本參數（後續測試共用）
    private const decimal IssuedQty        = 1000m;   // 發料 1000 個
    private const decimal MaterialUnitCost = 50m;     // 原料單價 50 元
    private const decimal AllowedScrapRate = 0.05m;   // 合約允收 5%

    // ── EP Valid ─────────────────────────────────────────────────────────────

    /// <summary>
    /// EP Valid: 實際損耗率 &lt; 合約允收損耗率 → ExcessScrapPenalty = 0。
    /// 例：發料 1000，收回 960（損耗 4%）< 允收 5%。
    /// </summary>
    [Fact]
    public void Calculate_ActualScrapBelowAllowed_PenaltyZero()
    {
        decimal receivedQty    = 960m;     // 損耗率 = 1 - 960/1000 = 4%
        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        Assert.Equal(0m, penalty);
    }

    /// <summary>
    /// EP Valid: 實際損耗率 &gt; 合約允收損耗率 → ExcessScrapPenalty &gt; 0。
    /// 例：發料 1000，收回 900（損耗 10%），超出允收 5%。
    /// 超耗量 = (0.10 - 0.05) × 1000 = 50，罰款 = 50 × 50 = 2500。
    /// </summary>
    [Fact]
    public void Calculate_ActualScrapAboveAllowed_PenaltyPositive()
    {
        decimal receivedQty = 900m;     // 損耗率 = 10%
        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        Assert.Equal(2500m, penalty);   // (0.10 - 0.05) × 1000 × 50
    }

    /// <summary>
    /// EP Valid: 實際損耗率 = 100%（發出的原料全部損耗，成品實收 0）。
    /// 超耗量 = (1.0 - 0.05) × 1000 = 950，罰款 = 950 × 50 = 47500。
    /// </summary>
    [Fact]
    public void Calculate_FullLoss_AllMaterialScrapped_PenaltyMaxed()
    {
        decimal receivedQty = 0m;      // 損耗率 = 100%
        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        Assert.Equal(47500m, penalty);  // (1.0 - 0.05) × 1000 × 50
    }

    // ── EP Invalid ───────────────────────────────────────────────────────────

    /// <summary>
    /// EP Invalid: 實際損耗率 &lt; 0%（receivedQty &gt; issuedQty，超產或資料錯誤）
    /// → 拋出 InvalidOperationException。
    /// </summary>
    [Fact]
    public void Calculate_NegativeScrapRate_ThrowsInvalidOperation()
    {
        decimal receivedQty = 1100m;   // > IssuedQty，損耗率 = -10%

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutsourceScrapCalculator.Calculate(
                IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost));

        PzAssert.Message("Pur_NegativeScrapRateFmt", ex.Message);
    }

    /// <summary>
    /// EP Invalid: issuedQty ≤ 0 → ArgumentOutOfRangeException。
    /// </summary>
    [Fact]
    public void Calculate_ZeroIssuedQty_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OutsourceScrapCalculator.Calculate(0m, 0m, AllowedScrapRate, MaterialUnitCost));
    }

    /// <summary>
    /// EP Invalid: receivedQty &lt; 0 → ArgumentOutOfRangeException。
    /// </summary>
    [Fact]
    public void Calculate_NegativeReceivedQty_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OutsourceScrapCalculator.Calculate(IssuedQty, -1m, AllowedScrapRate, MaterialUnitCost));
    }

    // ── BVA ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// BVA 允收臨界點: 實際損耗率 = 合約允收損耗率（精確相等）→ penalty = 0。
    /// 發料 1000，收回 950（損耗 5% = 允收 5%）。
    /// </summary>
    [Fact]
    public void Calculate_ActualScrapExactlyAtAllowedRate_PenaltyExactlyZero()
    {
        decimal receivedQty = 950m;    // 損耗率 = 1 - 950/1000 = 5.00% = AllowedScrapRate
        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        Assert.Equal(0m, penalty);
    }

    /// <summary>
    /// BVA 超耗臨界點: 實際損耗率 = 允收損耗率 + 0.000001（0.0001%）。
    /// 驗證扣款計算精度與 AwayFromZero 四捨五入邏輯（decimal 不截斷）。
    /// 超耗率 = 0.000001，超耗量 = 0.000001 × 1000 = 0.001，罰款 = 0.001 × 50 = 0.05。
    /// </summary>
    [Fact]
    public void Calculate_ScrapExceedsAllowedByMinimalAmount_PrecisionCorrect()
    {
        // 使損耗率比允收多 0.000001（即 0.0001%）
        // receivedQty = issuedQty × (1 - allowedScrapRate - 0.000001)
        //             = 1000 × (1 - 0.05 - 0.000001) = 1000 × 0.949999 = 949.999
        decimal receivedQty = 949.999m;
        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        // 實際損耗率 = 1 - 949.999/1000 = 0.050001
        // 超耗率 = 0.050001 - 0.05 = 0.000001
        // 超耗量 = 0.000001 × 1000 = 0.001，罰款 = 0.001 × 50 = 0.05
        Assert.Equal(0.05m, penalty, precision: 4);
    }

    /// <summary>
    /// BVA 扣款超額臨界點: ExcessScrapPenalty &gt; 加工費總額。
    /// 驗證計算器直接回傳超額罰款（呼叫端自行決定截斷或轉 AR），不自動截斷。
    /// 場景：允收 5%，實際損耗 80%，加工費僅 1000 元，但超耗罰款遠超過。
    /// 超耗率 = 0.80 - 0.05 = 0.75，超耗量 = 0.75 × 1000 = 750，
    /// 罰款 = 750 × 50 = 37500（遠大於假設加工費 1000）。
    /// </summary>
    [Fact]
    public void Calculate_PenaltyExceedsProcessingFee_ReturnsFullPenalty()
    {
        decimal receivedQty    = 200m;       // 損耗率 = 80%
        decimal processingFee  = 1000m;      // 假設本單加工費（呼叫端資訊）

        decimal penalty = OutsourceScrapCalculator.Calculate(
            IssuedQty, receivedQty, AllowedScrapRate, MaterialUnitCost);

        // 計算器回傳完整罰款，不截斷
        Assert.True(penalty > processingFee,
            $"超耗罰款 {penalty} 應大於加工費 {processingFee}（呼叫端自行處理超額部分）");
        Assert.Equal(37500m, penalty);   // (0.80 - 0.05) × 1000 × 50
    }

    // ── 多料 BOM（§1.5 逐料 AVCO） ─────────────────────────────────────────────

    /// <summary>
    /// 單料、BOM 用量 1:1：等同單料 Calculate。發料 1000、成品 900、允收 5%、AVCO 50。
    /// 預期耗用 = 900×1 = 900 → 損耗率 10% → 超耗 5% → 罰款 (0.10-0.05)×1000×50 = 2500。
    /// </summary>
    [Fact]
    public void CalculateMultiMaterial_SingleMaterial_MatchesSingleFormula()
    {
        var penalty = OutsourceScrapCalculator.CalculateMultiMaterial(
            receivedFinishedQty: 900m,
            allowedScrapRate: 0.05m,
            materials: new[] { new MU(IssuedQty: 1000m, AvcoUnitCost: 50m, BomUsagePerUnit: 1m) });

        Assert.Equal(2500m, penalty);
    }

    /// <summary>
    /// 逐料 AVCO 計價：兩種原料各自 AVCO/BOM 用量，罰款分別計後加總。
    /// 成品 100；料A 發 250、AVCO 8、BOM 用量 2（預期 200，損耗 50，超耗率=50/250-0.05=0.15，超耗量=0.15×250=37.5，罰款 37.5×8=300）；
    /// 料B 發 120、AVCO 30、BOM 用量 1（預期 100，損耗 20，超耗率=20/120-0.05≈0.11667，超耗量≈14，罰款≈420）。
    /// </summary>
    [Fact]
    public void CalculateMultiMaterial_TwoMaterials_SumsPerMaterialAvco()
    {
        var penalty = OutsourceScrapCalculator.CalculateMultiMaterial(
            receivedFinishedQty: 100m,
            allowedScrapRate: 0.05m,
            materials: new[]
            {
                new MU(IssuedQty: 250m, AvcoUnitCost: 8m,  BomUsagePerUnit: 2m),
                new MU(IssuedQty: 120m, AvcoUnitCost: 30m, BomUsagePerUnit: 1m),
            });

        // 料A: (1 - 200/250 - 0.05) × 250 × 8 = (0.20-0.05)×250×8 = 0.15×2000 = 300
        // 料B: (1 - 100/120 - 0.05) × 120 × 30 = (0.166667-0.05)×3600 = 0.116667×3600 ≈ 420.0012 → Round4 420.0012
        decimal expectedA = 300m;
        decimal expectedB = Math.Round((1m - 100m/120m - 0.05m) * 120m * 30m, 4, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedA + expectedB, penalty);
    }

    /// <summary>損耗未超允收的料不貢獻罰款；BOM 用量使預期耗用 ≥ 發料量者以 0 計。</summary>
    [Fact]
    public void CalculateMultiMaterial_WithinAllowanceOrOverConsumed_ContributesZero()
    {
        var penalty = OutsourceScrapCalculator.CalculateMultiMaterial(
            receivedFinishedQty: 100m,
            allowedScrapRate: 0.05m,
            materials: new[]
            {
                new MU(IssuedQty: 100m, AvcoUnitCost: 10m, BomUsagePerUnit: 0.96m), // 預期 96，損耗率 4% < 5% → 0
                new MU(IssuedQty: 100m, AvcoUnitCost: 10m, BomUsagePerUnit: 1.5m),  // 預期 150 ≥ 100 → 0（無損耗）
                new MU(IssuedQty: 0m,   AvcoUnitCost: 10m, BomUsagePerUnit: 1m),    // 無發料 → 略過
            });

        Assert.Equal(0m, penalty);
    }

    /// <summary>空清單/成品量 0：無罰款；成品量負值拋例外。</summary>
    [Fact]
    public void CalculateMultiMaterial_EdgeCases()
    {
        Assert.Equal(0m, OutsourceScrapCalculator.CalculateMultiMaterial(0m, 0.05m, Array.Empty<MU>()));
        Assert.Equal(0m, OutsourceScrapCalculator.CalculateMultiMaterial(100m, 0.05m, Array.Empty<MU>()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OutsourceScrapCalculator.CalculateMultiMaterial(-1m, 0.05m, Array.Empty<MU>()));
    }
}
