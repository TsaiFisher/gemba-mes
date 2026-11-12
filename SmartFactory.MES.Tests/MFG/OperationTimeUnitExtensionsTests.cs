using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 工序時間單位換算守門測試。
/// 曾發生 Second 誤用 ×60（應 ÷60）造成 3600 倍誤差、以及背景服務寫回未反算單位，
/// 這裡鎖定 ToMinutes / FromMinutes 的方向與往返一致性。
/// </summary>
public class OperationTimeUnitExtensionsTests
{
    [Theory]
    [InlineData(OperationTimeUnit.Second, 120, 2)]   // 120 秒 = 2 分
    [InlineData(OperationTimeUnit.Minute, 5, 5)]     // 5 分 = 5 分
    [InlineData(OperationTimeUnit.Hour, 2, 120)]     // 2 時 = 120 分
    public void ToMinutes_ConvertsCorrectly(OperationTimeUnit unit, decimal value, decimal expectedMinutes)
    {
        Assert.Equal(expectedMinutes, OperationTimeUnitExtensions.ToMinutes(value, unit));
    }

    [Theory]
    [InlineData(OperationTimeUnit.Second, 2, 120)]   // 2 分 = 120 秒
    [InlineData(OperationTimeUnit.Minute, 5, 5)]     // 5 分 = 5 分
    [InlineData(OperationTimeUnit.Hour, 120, 2)]     // 120 分 = 2 時
    public void FromMinutes_ConvertsCorrectly(OperationTimeUnit unit, decimal minutes, decimal expectedInUnit)
    {
        Assert.Equal(expectedInUnit, OperationTimeUnitExtensions.FromMinutes(minutes, unit));
    }

    [Theory]
    [InlineData(OperationTimeUnit.Second, 90)]
    [InlineData(OperationTimeUnit.Minute, 7.5)]
    [InlineData(OperationTimeUnit.Hour, 3.25)]
    public void ToMinutes_FromMinutes_RoundTrips(OperationTimeUnit unit, decimal original)
    {
        var minutes = OperationTimeUnitExtensions.ToMinutes(original, unit);
        var back = OperationTimeUnitExtensions.FromMinutes(minutes, unit);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Second_UsesDivideNotMultiply_RegressionGuard()
    {
        // 明確鎖定方向：秒制絕不能等同乘 60（那是先前的 bug）。
        Assert.Equal(1m, OperationTimeUnitExtensions.ToMinutes(60m, OperationTimeUnit.Second));
        Assert.NotEqual(3600m, OperationTimeUnitExtensions.ToMinutes(60m, OperationTimeUnit.Second));
    }
}
