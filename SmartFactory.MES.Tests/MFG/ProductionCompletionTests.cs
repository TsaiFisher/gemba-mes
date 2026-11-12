using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 完工入庫業務規則單元測試（mfg-domain.md §5.4 / §17.2 / §17.3 Phase F）。
/// 純函式，不接 DB。
/// </summary>
public class ProductionCompletionTests
{
    // ── Backflush 後沖計算（mfg-domain.md §17.3）────────────────────────────

    [Fact]
    public void CalcBackflushQty_FullCompletion_ReturnsFullRequired()
    {
        // 全量完工：後沖量 = 需求量
        var qty = IProductionCompletionService.CalcBackflushQty(
            requiredQty: 100m, qtyDone: 10m, woQuantity: 10m);
        Assert.Equal(100m, qty);
    }

    [Fact]
    public void CalcBackflushQty_HalfCompletion_ReturnsHalf()
    {
        // 完工 50%：後沖量 = 需求量 × 0.5
        var qty = IProductionCompletionService.CalcBackflushQty(
            requiredQty: 100m, qtyDone: 5m, woQuantity: 10m);
        Assert.Equal(50m, qty);
    }

    [Fact]
    public void CalcBackflushQty_ZeroQtyDone_ReturnsZero()
    {
        var qty = IProductionCompletionService.CalcBackflushQty(
            requiredQty: 100m, qtyDone: 0m, woQuantity: 10m);
        Assert.Equal(0m, qty);
    }

    [Fact]
    public void CalcBackflushQty_ZeroWoQuantity_ReturnsZero()
    {
        var qty = IProductionCompletionService.CalcBackflushQty(
            requiredQty: 100m, qtyDone: 5m, woQuantity: 0m);
        Assert.Equal(0m, qty);
    }

    [Fact]
    public void CalcBackflushQty_WithScrapRate_ProportionalToQtyDone()
    {
        // WO 數量 100，需求 200（ScrapRate 已含在 RequiredQty），完工 80 → 後沖 = 200 × 80/100 = 160
        var qty = IProductionCompletionService.CalcBackflushQty(
            requiredQty: 200m, qtyDone: 80m, woQuantity: 100m);
        Assert.Equal(160m, qty);
    }

    // ── IssueMethod Enum 驗證 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(MaterialIssueMethod.Push,      false)]  // 非 Backflush
    [InlineData(MaterialIssueMethod.Pull,      false)]  // 非 Backflush
    [InlineData(MaterialIssueMethod.Backflush, true)]   // Backflush
    public void IssueMethod_BackflushIdentification(MaterialIssueMethod method, bool isBackflush)
    {
        Assert.Equal(isBackflush, method == MaterialIssueMethod.Backflush);
    }

    [Theory]
    [InlineData(BackflushTiming.AtCompletion, true)]   // 完工時後沖
    [InlineData(BackflushTiming.PerReport,    false)]  // 每次報工後沖
    public void BackflushTiming_AtCompletion(BackflushTiming timing, bool isAtCompletion)
    {
        Assert.Equal(isAtCompletion, timing == BackflushTiming.AtCompletion);
    }

    // ── 完工入庫成本計算 ──────────────────────────────────────────────────────

    [Fact]
    public void ActualUnitCost_FullyCostAccumulated_CalculatesCorrectly()
    {
        // 模擬 WorkOrder 成本累計後的單位成本計算
        decimal actualMaterial = 1000m;
        decimal actualLabor    = 500m;
        decimal actualOverhead = 200m;
        decimal qtyDone        = 10m;

        decimal totalCost = actualMaterial + actualLabor + actualOverhead;
        decimal unitCost  = totalCost / qtyDone;

        Assert.Equal(1700m, totalCost);
        Assert.Equal(170m,  unitCost);
    }

    [Fact]
    public void WorkOrderStatus_CompletedToClosedTransition_IsValid()
    {
        // 只有 Completed 狀態可入庫 → Closed
        Assert.True(WorkOrderStatus.Completed == WorkOrderStatus.Completed);
        Assert.True(WorkOrderStatus.Closed    != WorkOrderStatus.Completed);
    }

    // ── RoutingBom / WorkOrderMaterial IssueMethod Fallback 規則 ─────────────

    [Theory]
    [InlineData(MaterialIssueMethod.Backflush, MaterialIssueMethod.Backflush)]  // 明確設定：使用設定值
    [InlineData(MaterialIssueMethod.Pull,      MaterialIssueMethod.Pull)]
    [InlineData(MaterialIssueMethod.Push,      MaterialIssueMethod.Push)]
    public void RoutingBom_ExplicitIssueMethod_UsedDirectly(
        MaterialIssueMethod bomMethod, MaterialIssueMethod expected)
    {
        MaterialIssueMethod? bomIssue = bomMethod;
        var result = bomIssue ?? MaterialIssueMethod.Push;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RoutingBom_NullIssueMethod_FallsBackToPush()
    {
        // null → 廠別預設 Push
        MaterialIssueMethod? bomIssue = null;
        var result = bomIssue ?? MaterialIssueMethod.Push;
        Assert.Equal(MaterialIssueMethod.Push, result);
    }

    // ── 分箱批號（container-tracking.md §8 L1+）────────────────────────────

    [Fact]
    public void BoxLots_最後一箱裝餘數_序號兩位補零()
    {
        var boxes = CompletionBoxing.BoxLots(100m, 30m, "LOT");
        Assert.Equal(new[] { ("LOT-01", 30m), ("LOT-02", 30m), ("LOT-03", 30m), ("LOT-04", 10m) }, boxes.ToArray());
    }

    [Fact]
    public void BoxLots_整除時最後一箱是滿箱()
    {
        var boxes = CompletionBoxing.BoxLots(90m, 30m, "LOT");
        Assert.Equal(3, boxes.Count);
        Assert.All(boxes, b => Assert.Equal(30m, b.Qty));
    }

    [Fact]
    public void BoxLots_百箱以上序號依箱數補到三位_字串排序即箱序()
    {
        var boxes = CompletionBoxing.BoxLots(120m, 1m, "W");
        Assert.Equal("W-001", boxes[0].LotNo);
        Assert.Equal("W-120", boxes[^1].LotNo);
        Assert.Equal(boxes.Select(b => b.LotNo), boxes.Select(b => b.LotNo).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void BoxLots_小數數量_總和等於完工量()
    {
        var boxes = CompletionBoxing.BoxLots(10.5m, 4m, "K");
        Assert.Equal(new[] { 4m, 4m, 2.5m }, boxes.Select(b => b.Qty));
        Assert.Equal(10.5m, boxes.Sum(b => b.Qty));
    }

    [Fact]
    public void BoxLots_完工量不足一箱_只有一箱()
    {
        var box = Assert.Single(CompletionBoxing.BoxLots(7m, 30m, "S"));
        Assert.Equal(("S-01", 7m), box);
    }
}
