using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// WorkOrderCostCalculator 單元測試（mfg-domain.md §16.2 / §12.2「工單釋出後標準成本」）。
/// 純函式，不接 DB。
/// </summary>
public class WorkOrderCostCalculatorTests
{
    private static Dictionary<string, (decimal Labor, decimal Machine)> Rates(
        params (string code, decimal labor, decimal machine)[] r)
        => r.ToDictionary(x => x.code, x => (x.labor, x.machine));

    [Fact]
    public void ComputeStandard_HourUnit_SumsMaterialLaborOverhead()
    {
        var materials = new[]
        {
            new WorkOrderMaterial { ProductCode = "M1", RequiredQty = 20m, UnitCost = 5m },
            new WorkOrderMaterial { ProductCode = "M2", RequiredQty = 3m,  UnitCost = 10m },
        };
        var ops = new[]
        {
            new WorkOrderOperation { WorkCenterId = "WC1", SetupTime = 1m, UnitTime = 0.5m, TimeUnit = OperationTimeUnit.Hour, SetupTimeUnit = OperationTimeUnit.Hour },
        };

        var (mat, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 10m, materials, ops, Rates(("WC1", 100m, 200m)));

        Assert.Equal(130m, mat);              // 20×5 + 3×10
        // 工時 = 1 + 0.5×10 = 6 小時
        Assert.Equal(600m, lab);              // 6 × 100
        Assert.Equal(1200m, ovh);             // 6 × 200
    }

    [Fact]
    public void ComputeStandard_MinuteUnit_ConvertsToHours()
    {
        var ops = new[]
        {
            new WorkOrderOperation { WorkCenterId = "WC1", SetupTime = 0m, UnitTime = 6m, TimeUnit = OperationTimeUnit.Minute },
        };

        var (_, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 10m, Array.Empty<WorkOrderMaterial>(), ops, Rates(("WC1", 100m, 300m)));

        // 工時 = (0 + 6×10) 分 = 60 分 = 1 小時
        Assert.Equal(100m, lab);
        Assert.Equal(300m, ovh);
    }

    [Fact]
    public void ComputeStandard_UnknownWorkCenter_ContributesZeroLaborOverhead()
    {
        var ops = new[]
        {
            new WorkOrderOperation { WorkCenterId = "GHOST", SetupTime = 1m, UnitTime = 1m, TimeUnit = OperationTimeUnit.Hour },
        };

        var (mat, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(
            quantity: 5m, Array.Empty<WorkOrderMaterial>(), ops, Rates(("WC1", 100m, 200m)));

        Assert.Equal(0m, mat);
        Assert.Equal(0m, lab);     // 工作中心查無費率 → 0
        Assert.Equal(0m, ovh);
    }
}
