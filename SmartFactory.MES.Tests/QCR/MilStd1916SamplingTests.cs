using SmartFactory.MES.Services;
using static SmartFactory.MES.Services.MilStd1916Service;

namespace SmartFactory.MES.Tests.QCR;

/// <summary>
/// MIL-STD-1916 Table II 抽樣引擎單元測試（mfg-domain.md §15.3）。
/// 純查表邏輯，不接 DB。允收數恆為 0（Ac=0, Re=1）。
/// </summary>
public class MilStd1916SamplingTests
{
    // ── GetSampleSize：VL-IV 跨列邊界 ───────────────────────────────────────

    [Theory]
    [InlineData(   2, VerificationLevel.IV,  2)]   // ≤8 列（lot=1 會被截頂，改用 2 驗查表值）
    [InlineData(   8, VerificationLevel.IV,  2)]   // ≤8 列上限
    [InlineData(   9, VerificationLevel.IV,  3)]   // 跨入 ≤15 列
    [InlineData(  15, VerificationLevel.IV,  3)]   // ≤15 列上限
    [InlineData(  25, VerificationLevel.IV,  3)]   // ≤25 列（與 ≤15 列同樣本數）
    [InlineData(  26, VerificationLevel.IV,  5)]   // 跨入 ≤50 列
    [InlineData(  50, VerificationLevel.IV,  5)]   // ≤50 列上限
    [InlineData(  90, VerificationLevel.IV,  5)]   // ≤90 列（與 ≤50 列同樣本數）
    [InlineData(  91, VerificationLevel.IV,  8)]   // 跨入 ≤150 列
    [InlineData( 150, VerificationLevel.IV,  8)]   // ≤150 列上限
    [InlineData( 280, VerificationLevel.IV,  8)]   // ≤280 列（同樣本數）
    [InlineData( 500, VerificationLevel.IV,  8)]   // ≤500 列（同樣本數）
    [InlineData( 501, VerificationLevel.IV, 13)]   // 跨入 ≤1200 列
    [InlineData(1200, VerificationLevel.IV, 13)]   // ≤1200 列上限
    [InlineData(3200, VerificationLevel.IV, 13)]   // ≤3200 列（同樣本數）
    [InlineData(3201, VerificationLevel.IV, 20)]   // 跨入 ≤10000 列
    [InlineData(10000,VerificationLevel.IV, 20)]   // ≤10000 列上限
    [InlineData(35000,VerificationLevel.IV, 20)]   // ≤35000 列（同樣本數）
    [InlineData(35001,VerificationLevel.IV, 32)]   // 跨入 ≤150000 列
    [InlineData(500001,VerificationLevel.IV,50)]   // 超出 ≤500000，落入最後列
    public void GetSampleSize_VlIV_ReturnsCorrectSampleAtBoundary(
        int lotSize, VerificationLevel vl, int expected)
    {
        Assert.Equal(expected, GetSampleSize(lotSize, vl));
    }

    // ── GetSampleSize：同一批量（lot=100）各 VL 全掃 ────────────────────────

    [Theory]
    [InlineData(VerificationLevel.I,    2)]   // ≤150 列，VL-I
    [InlineData(VerificationLevel.II,   3)]
    [InlineData(VerificationLevel.III,  5)]
    [InlineData(VerificationLevel.IV,   8)]
    [InlineData(VerificationLevel.V,   13)]
    [InlineData(VerificationLevel.VI,  20)]
    [InlineData(VerificationLevel.VII, 32)]   // ≤150 列，VL-VII
    public void GetSampleSize_Lot100_AllVlVariants(VerificationLevel vl, int expected)
    {
        Assert.Equal(expected, GetSampleSize(100m, vl));
    }

    // ── GetSampleSize：批量上限（n = min(table, lot)）────────────────────────

    [Theory]
    [InlineData(  1, VerificationLevel.VII, 1)]    // 表格值 8，批量只有 1 → 全檢
    [InlineData(  3, VerificationLevel.VII, 3)]    // 表格值 8，批量只有 3 → 全檢
    [InlineData(  7, VerificationLevel.VII, 7)]    // 表格值 8，批量 7 → 全檢
    [InlineData(  8, VerificationLevel.VII, 8)]    // 表格值 8，批量 8 → 恰好等於
    [InlineData(  9, VerificationLevel.VII, 9)]    // 批量 9 落入 ≤15 列，表格值 13 > 9 → 截頂全檢
    public void GetSampleSize_SmallLot_CapsAtLotSize(
        int lotSize, VerificationLevel vl, int expected)
    {
        Assert.Equal(expected, GetSampleSize(lotSize, vl));
    }

    // ── GetSampleSize：小數批量無條件進位 ────────────────────────────────────

    [Fact]
    public void GetSampleSize_DecimalLot_CeilsBeforeLookup()
    {
        // 8.0m → ceil=8 → ≤8 列，VL-IV = 2
        Assert.Equal(2, GetSampleSize(8.0m, VerificationLevel.IV));

        // 8.1m → ceil=9 → ≤15 列，VL-IV = 3
        Assert.Equal(3, GetSampleSize(8.1m, VerificationLevel.IV));

        // 25.99m → ceil=26 → ≤50 列，VL-IV = 5
        Assert.Equal(5, GetSampleSize(25.99m, VerificationLevel.IV));
    }

    // ── IsAccepted：Ac=0 規則 ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, true)]    // 零不良 → 允收
    [InlineData(1, false)]   // 1 件不良 → 拒批（Re=1）
    [InlineData(5, false)]   // 多件不良 → 拒批
    public void IsAccepted_AcAlwaysZero(int failQty, bool expected)
    {
        Assert.Equal(expected, IsAccepted(failQty));
    }

    // ── GetVlOptions：7 個等級完整回傳 ──────────────────────────────────────

    [Fact]
    public void GetVlOptions_Returns7DistinctLevels()
    {
        var opts = GetVlOptions();

        Assert.Equal(7, opts.Count);
        // 值域 1~7 且不重複
        Assert.Equal(Enumerable.Range(1, 7), opts.Select(o => o.Value));
        // 每個 Label 都不為空
        Assert.All(opts, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }
}
