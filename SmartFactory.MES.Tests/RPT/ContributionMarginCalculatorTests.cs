using SmartFactory.MES.Modules.RPT.Services;
using Xunit;
using VP = SmartFactory.MES.Modules.RPT.Services.ContributionMarginCalculator.VariablePolicy;

namespace SmartFactory.MES.Tests.RPT;

/// <summary>ContributionMarginCalculator（變動占比／邊際貢獻／貢獻率）純函式驗證。</summary>
public class ContributionMarginCalculatorTests
{
    [Fact]
    public void VariableRatio_material_and_labor_variable()
    {
        // 材料60+人工20 變動、製費20 固定 → 80/100 = 0.8
        var r = ContributionMarginCalculator.VariableRatio(60m, 20m, 20m, new VP(true, true, false));
        Assert.Equal(0.8m, r);
    }

    [Fact]
    public void VariableRatio_material_only()
    {
        var r = ContributionMarginCalculator.VariableRatio(60m, 20m, 20m, new VP(true, false, false));
        Assert.Equal(0.6m, r);
    }

    [Fact]
    public void VariableRatio_all_elements_variable_is_one()
    {
        var r = ContributionMarginCalculator.VariableRatio(60m, 20m, 20m, new VP(true, true, true));
        Assert.Equal(1m, r);
    }

    [Fact]
    public void VariableRatio_zero_total_returns_null()
    {
        Assert.Null(ContributionMarginCalculator.VariableRatio(0m, 0m, 0m, new VP(true, true, false)));
    }

    [Fact]
    public void Contribution_subtracts_variable_cost()
    {
        // 營收1000 − COGS600×0.8(=480) = 520
        Assert.Equal(520m, ContributionMarginCalculator.Contribution(1000m, 600m, 0.8m));
    }

    [Fact]
    public void ContributionRate_percentage()
    {
        Assert.Equal(52.00m, ContributionMarginCalculator.ContributionRate(1000m, 520m));
    }

    [Fact]
    public void ContributionRate_zero_revenue_is_zero()
    {
        Assert.Equal(0m, ContributionMarginCalculator.ContributionRate(0m, 0m));
    }
}
