using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 生產與委外流程（Plan-to-Produce）資料一致性檢核測試。
/// 對應規格書 §1.3 三大檢核規則與 §5.7 / §5.8 EP/BVA 案例。
///
/// §5.7 — 工單標準成本計算 EP/BVA 補充（WorkOrderCostCalculator）
/// §5.8 — WIP 在製品平衡公式（WipBalanceCalculator）
/// </summary>
public class WipReconciliationTests
{
    private static Dictionary<string, (decimal Labor, decimal Machine)> Rates(
        params (string Code, decimal Labor, decimal Machine)[] entries)
        => entries.ToDictionary(e => e.Code, e => (e.Labor, e.Machine));

    // ════════════════════════════════════════════════════════════════════════
    // §5.7 — 工單標準成本 EP/BVA 補充（WorkOrderCostCalculator）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// BVA 零產量邊界點: quantity = 0，UnitTime × 0 = 0，但 SetupTime 仍計費；
    /// 材料成本與數量無關（RequiredQty 為整批需求）。
    /// §5.7 BVA 第 1 案例（工單量為 0 時 SetupCost 仍計入）。
    /// </summary>
    [Fact]
    public void ComputeStandard_ZeroQuantity_SetupCostStillApplies()
    {
        var materials = new[]
        {
            new WorkOrderMaterial { ProductCode = "M1", RequiredQty = 10m, UnitCost = 5m },
        };
        var ops = new[]
        {
            new WorkOrderOperation
            {
                WorkCenterId = "WC1",
                SetupTime    = 2m,        // 固定換線時間 2 小時
                UnitTime     = 0.5m,      // 每件 0.5 小時
                TimeUnit     = OperationTimeUnit.Hour,
                SetupTimeUnit = OperationTimeUnit.Hour,
            },
        };

        var (mat, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 0m, materials, ops, Rates(("WC1", 100m, 200m)));

        // Material = 10 × 5 = 50（與數量無關）
        Assert.Equal(50m, mat);
        // Labor = (SetupTime + UnitTime × 0) × rate = 2 × 100 = 200
        Assert.Equal(200m, lab);
        // Overhead = 2 × 200 = 400
        Assert.Equal(400m, ovh);
    }

    /// <summary>
    /// BVA 秒換算精度: TimeUnit = Second，3600 秒 = 1 小時。
    /// §5.7 BVA 第 2 案例（秒→小時換算不截斷）。
    /// </summary>
    [Fact]
    public void ComputeStandard_SecondUnit_CorrectlyConvertsToHours()
    {
        var ops = new[]
        {
            new WorkOrderOperation
            {
                WorkCenterId = "WC1",
                SetupTime    = 0m,
                UnitTime     = 360m,       // 每件 360 秒 = 0.1 小時
                TimeUnit     = OperationTimeUnit.Second,
            },
        };

        var (_, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 10m, Array.Empty<WorkOrderMaterial>(), ops, Rates(("WC1", 600m, 1200m)));

        // 工時 = (0 + 360 × 10) 秒 ÷ 3600 = 1 小時
        Assert.Equal(600m,  lab);   // 1 × 600
        Assert.Equal(1200m, ovh);   // 1 × 1200
    }

    /// <summary>
    /// EP Valid: 多工作中心各有獨立費率，成本分別計算後加總。
    /// §5.7 EP Valid（多工作中心費率加總一致性）。
    /// </summary>
    [Fact]
    public void ComputeStandard_MultipleWorkCenters_SeparateRatesAccumulate()
    {
        var ops = new[]
        {
            new WorkOrderOperation
            {
                WorkCenterId = "WC_CNC",
                SetupTime    = 0m,
                UnitTime     = 1m,          // 1 小時/件
                TimeUnit     = OperationTimeUnit.Hour,
            },
            new WorkOrderOperation
            {
                WorkCenterId = "WC_INS",
                SetupTime    = 0m,
                UnitTime     = 30m,         // 30 分鐘/件
                TimeUnit     = OperationTimeUnit.Minute,
            },
        };

        var (_, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 4m,
            Array.Empty<WorkOrderMaterial>(),
            ops,
            Rates(("WC_CNC", 200m, 400m), ("WC_INS", 100m, 0m)));

        // WC_CNC: (0 + 1×4) 小時 → lab=800, ovh=1600
        // WC_INS: (0 + 30×4) 分 = 120分 = 2小時 → lab=200, ovh=0
        Assert.Equal(1000m, lab);   // 800 + 200
        Assert.Equal(1600m, ovh);   // 1600 + 0
    }

    // ════════════════════════════════════════════════════════════════════════
    // §5.8 — WIP 在製品平衡公式（WipBalanceCalculator）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EP Valid: 簡單投入→產出，期末 WIP = 期初 + 發料 - 完工入庫。
    /// §5.8 EP Valid 第 1 案例（§1.3.C.1 公式驗證）。
    /// </summary>
    [Fact]
    public void ComputeEndingWip_SimpleFlow_BalancesCorrectly()
    {
        // 期初 0 + 發料 100 - 完工 80 = 期末 20
        var endWip = WipBalanceCalculator.ComputeEndingWip(
            beginningWip:      0m,
            netMaterialIssued: 100m,
            laborAndOverhead:  0m,
            completionCost:    80m);

        Assert.Equal(20m, endWip);
    }

    /// <summary>
    /// EP Valid: 含人工製費與報廢，期末 WIP 正確計算。
    /// §5.8 EP Valid 第 2 案例（完整公式驗證）。
    /// </summary>
    [Fact]
    public void ComputeEndingWip_WithLaborAndScrap_CorrectBalance()
    {
        // 期初 50 + 發料 200 + 人工/製費 80 - 完工 250 - 報廢 20 = 60
        var endWip = WipBalanceCalculator.ComputeEndingWip(
            beginningWip:      50m,
            netMaterialIssued: 200m,
            laborAndOverhead:  80m,
            completionCost:    250m,
            scrapCost:         20m);

        Assert.Equal(60m, endWip);
    }

    /// <summary>
    /// EP Valid: 完全完工（所有投入都轉為完工入庫），期末 WIP 精確歸零。
    /// §5.8 EP Valid 第 3 案例（WIP 全額結清，無浮點誤差）。
    /// </summary>
    [Fact]
    public void ComputeEndingWip_FullCompletion_EndingWipExactlyZero()
    {
        // 期初 0 + 發料 500 + 人工 100 - 完工 600 = 0
        var endWip = WipBalanceCalculator.ComputeEndingWip(
            beginningWip:      0m,
            netMaterialIssued: 500m,
            laborAndOverhead:  100m,
            completionCost:    600m);

        Assert.Equal(0m, endWip);
    }

    /// <summary>
    /// EP Invalid: 淨發料金額 &lt; 0（退料超過發料，資料異常）→ ArgumentOutOfRangeException。
    /// §5.8 EP Invalid 案例（退料超量異常偵測）。
    /// </summary>
    [Fact]
    public void ComputeEndingWip_NegativeMaterialIssued_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WipBalanceCalculator.ComputeEndingWip(
                beginningWip:      0m,
                netMaterialIssued: -10m,   // 不合法：退料超過發料
                laborAndOverhead:  0m,
                completionCost:    0m));
    }

    /// <summary>
    /// BVA 差異歸零臨界點: Actual = Standard → 三項差異全部 = 0，總差異 = 0。
    /// §5.8 BVA 第 1 案例（標準成本與實際成本完全一致時無差異）。
    /// </summary>
    [Fact]
    public void ComputeTotalVariance_ActualEqualsStandard_ZeroVariance()
    {
        var variance = WipBalanceCalculator.ComputeTotalVariance(
            actualMaterial:   500m, standardMaterial:   500m,
            actualLabor:      200m, standardLabor:      200m,
            actualOverhead:   100m, standardOverhead:   100m);

        Assert.Equal(0m, variance);
    }

    /// <summary>
    /// BVA 正差異臨界點: 實際成本 > 標準成本 → 總差異 &gt; 0（超支），各項分別計算後加總。
    /// §5.8 BVA 第 2 案例（超支偵測精度）。
    /// </summary>
    [Fact]
    public void ComputeTotalVariance_ActualExceedsStandard_PositiveVariance()
    {
        // 料差 +50，人工效率差 +30，製費費率差 −20 → 總差異 = 60
        var variance = WipBalanceCalculator.ComputeTotalVariance(
            actualMaterial:   550m, standardMaterial: 500m,    // +50
            actualLabor:      230m, standardLabor:    200m,    // +30
            actualOverhead:    80m, standardOverhead: 100m);   // -20

        Assert.Equal(60m, variance);
    }

    /// <summary>
    /// BVA 完工量為 0 時 WIP 不歸零: 有發料但完工量 0，期末 WIP = 期初 + 發料（資源在途）。
    /// §5.8 BVA 第 3 案例（在途 WIP 應完整保留，不可因除以零而異常）。
    /// </summary>
    [Fact]
    public void ComputeEndingWip_NoCompletion_AllMaterialInProcess()
    {
        // 期初 0 + 發料 300 + 人工 50 - 完工 0 = 350（全在製）
        var endWip = WipBalanceCalculator.ComputeEndingWip(
            beginningWip:      0m,
            netMaterialIssued: 300m,
            laborAndOverhead:  50m,
            completionCost:    0m);

        Assert.Equal(350m, endWip);
    }
}
