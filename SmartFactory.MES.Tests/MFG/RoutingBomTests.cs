using SmartFactory.MES.Modules.MFG.Models;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// RoutingBom.ApplyLossRates 公式測試（2026-07-20 新增下角率）：全外加法
/// 投入量 = 用量 × (1 + OffcutRate/100 + ScrapRate/100)。與既有 ScrapRate 單獨使用時的
/// 公式（× (1+ScrapRate/100)）完全相容——這是決定採「全外加法」而非「全還原法」的關鍵：
/// dev DB 現有 27 筆用料已設損耗率，全還原法會靜默改變其投入量計算結果。
/// </summary>
public class RoutingBomTests
{
    [Fact]
    public void ApplyLossRates_BothZero_ReturnsBaseQty()
        => Assert.Equal(10m, RoutingBom.ApplyLossRates(10m, 0m, 0m));

    [Fact]
    public void ApplyLossRates_ScrapOnly_MatchesLegacyFormula()
        // 既有公式：需求量 × (1 + ScrapRate/100)——OffcutRate=0 時必須與此完全一致
        => Assert.Equal(11m, RoutingBom.ApplyLossRates(10m, 0m, 10m));

    [Fact]
    public void ApplyLossRates_OffcutOnly_AddsFlatPercentage()
        => Assert.Equal(12m, RoutingBom.ApplyLossRates(10m, 20m, 0m));

    [Fact]
    public void ApplyLossRates_Combined_SumsBothRates()
        // 10 × (1 + 20% + 10%) = 13
        => Assert.Equal(13m, RoutingBom.ApplyLossRates(10m, 20m, 10m));
}
