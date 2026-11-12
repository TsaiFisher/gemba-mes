using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 多階展開服務測試：階層展開/累計量（含損耗率）、來源判斷、
/// 途程生效區間、循環引用防護、深度上限。
/// </summary>
public class MultiLevelBomServiceTests : TestBase
{
    private const string PlantId = "P001";
    private static readonly DateOnly Today = new(2026, 7, 20);

    public MultiLevelBomServiceTests() => _context.CurrentPlantId = PlantId;

    private MultiLevelBomService MakeService() => new(_context);

    // ── 測試資料 helpers ─────────────────────────────────────────────────────

    private void AddProduct(string code, string? routingId = null, string uom = "PC")
        => _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = $"{code} 品名", StockUom = uom,
            RoutingId = routingId, IsActive = true,
        });

    private Routing AddRouting(string routingId, bool isActive = true,
        DateOnly? effFrom = null, DateOnly? effTo = null)
    {
        var r = new Routing
        {
            RoutingId = routingId, RoutingName = $"{routingId} 途程", IsActive = isActive,
            EffectiveFrom = effFrom, EffectiveTo = effTo, PlantId = PlantId,
        };
        _context.Routings.Add(r);
        return r;
    }

    private void AddOperation(string routingId, int seq, params (string ProductCode, decimal Qty, decimal Scrap)[] boms)
        => AddOperationWithOffcut(routingId, seq, boms.Select(b => (b.ProductCode, b.Qty, b.Scrap, 0m)).ToArray());

    private void AddOperationWithOffcut(string routingId, int seq,
        params (string ProductCode, decimal Qty, decimal Scrap, decimal Offcut)[] boms)
    {
        var op = new RoutingOperation
        {
            RoutingId = routingId, OperationSeq = seq, OperationName = $"OP{seq}",
            WorkCenterId = "WC1", UomCode = "PC",
            TM_SETUP = 10, TM_UNIT = 1, TimeUnit = OperationTimeUnit.Minute, YieldRate = 100,
        };
        int bomSeq = 0;
        foreach (var (code, qty, scrap, offcut) in boms)
            op.Boms.Add(new RoutingBom
            {
                RoutingId = routingId, OperationSeq = seq, BomSeq = ++bomSeq,
                ProductCode = code, Quantity = qty, ScrapRate = scrap, OffcutRate = offcut, UomCode = "PC",
            });
        _context.Set<RoutingOperation>().Add(op);
    }

    // ── 兩階展開：階層/累計量/來源 ───────────────────────────────────────────

    [Fact]
    public async Task Explode_TwoLevels_BuildsRowsWithCumulativeQty()
    {
        AddProduct("FG",  "R-FG");
        AddProduct("SFG", "R-SFG");
        AddProduct("MAT-A");
        AddProduct("MAT-B");
        AddRouting("R-FG");
        AddRouting("R-SFG");
        AddOperation("R-FG", 10, ("MAT-A", 2m, 10m), ("SFG", 1m, 0m));
        AddOperation("R-SFG", 10, ("MAT-B", 3m, 0m));
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("FG", Today);

        Assert.Equal("FG", result.RootProductCode);
        Assert.Empty(result.Warnings);

        Assert.Equal(3, result.BomRows.Count);
        var matA = result.BomRows.Single(r => r.ProductCode == "MAT-A");
        Assert.Equal(1, matA.Level);
        Assert.Equal(2.2m, matA.RequiredQty);          // 2 × (1 + 10%)
        Assert.Equal("Buy", matA.SourceType);

        var sfg = result.BomRows.Single(r => r.ProductCode == "SFG");
        Assert.Equal(1, sfg.Level);
        Assert.Equal("Make", sfg.SourceType);

        var matB = result.BomRows.Single(r => r.ProductCode == "MAT-B");
        Assert.Equal(2, matB.Level);
        Assert.Equal("SFG", matB.ParentProductCode);
        Assert.Equal(3m, matB.RequiredQty);            // 1 × 3

        // 途程節點：FG(0) 與 SFG(1)
        Assert.Equal(2, result.RoutingNodes.Count);
        Assert.Equal(0, result.RoutingNodes[0].Level);
        Assert.Equal("FG", result.RoutingNodes[0].ProductCode);
        Assert.Equal(1, result.RoutingNodes[1].Level);
        Assert.Equal("SFG", result.RoutingNodes[1].ProductCode);
        Assert.Single(result.RoutingNodes[1].Operations);
        Assert.Single(result.RoutingNodes[1].Operations[0].Boms);
    }

    // ── 損耗率連乘：父階損耗率會傳遞到子階累計量 ─────────────────────────────

    [Fact]
    public async Task Explode_ScrapRate_CompoundsAcrossLevels()
    {
        AddProduct("FG",  "R-FG");
        AddProduct("SFG", "R-SFG");
        AddProduct("MAT");
        AddRouting("R-FG");
        AddRouting("R-SFG");
        AddOperation("R-FG", 10, ("SFG", 2m, 10m));    // 2 × 1.1 = 2.2
        AddOperation("R-SFG", 10, ("MAT", 5m, 20m));   // 2.2 × 5 × 1.2 = 13.2
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("FG", Today);

        Assert.Equal(13.2m, result.BomRows.Single(r => r.ProductCode == "MAT").RequiredQty);
    }

    // ── 下角率：全外加法，與損耗率同層加總（2026-07-20 新增）─────────────────

    [Fact]
    public async Task Explode_OffcutRate_AddsAlongsideScrapRate()
    {
        AddProduct("FG", "R-FG");
        AddProduct("MAT");
        AddRouting("R-FG");
        // 2 × (1 + 20%下角 + 10%損耗) = 2.6
        AddOperationWithOffcut("R-FG", 10, ("MAT", 2m, 10m, 20m));
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("FG", Today);

        Assert.Equal(2.6m, result.BomRows.Single(r => r.ProductCode == "MAT").RequiredQty);
    }

    // ── 途程生效區間：停用/過期 → 視為採購件葉節點 ───────────────────────────

    [Fact]
    public async Task Explode_ExpiredOrInactiveRouting_TreatedAsBuyLeaf()
    {
        AddProduct("FG", "R-FG");
        AddProduct("SFG-EXPIRED", "R-EXPIRED");
        AddProduct("SFG-INACTIVE", "R-INACTIVE");
        AddRouting("R-FG");
        AddRouting("R-EXPIRED", effTo: Today.AddDays(-1));    // 昨天到期
        AddRouting("R-INACTIVE", isActive: false);
        AddOperation("R-FG", 10, ("SFG-EXPIRED", 1m, 0m), ("SFG-INACTIVE", 1m, 0m));
        AddOperation("R-EXPIRED", 10, ("MAT-X", 1m, 0m));
        AddOperation("R-INACTIVE", 10, ("MAT-Y", 1m, 0m));
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("FG", Today);

        Assert.All(result.BomRows, r => Assert.Equal("Buy", r.SourceType));
        Assert.Equal(2, result.BomRows.Count);              // 不展開 MAT-X / MAT-Y
        Assert.Single(result.RoutingNodes);                 // 只有 FG 節點
    }

    [Fact]
    public async Task Explode_RootWithoutActiveRouting_ReturnsEmpty()
    {
        AddProduct("BUY-ONLY");
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("BUY-ONLY", Today);

        Assert.Empty(result.BomRows);
        Assert.Empty(result.RoutingNodes);
        Assert.Equal("BUY-ONLY", result.RootProductCode);
    }

    // ── 循環引用：同路徑再現 → 警告 + 不往下展 ───────────────────────────────

    [Fact]
    public async Task Explode_CyclicBom_WarnsAndStops()
    {
        AddProduct("A", "R-A");
        AddProduct("B", "R-B");
        AddRouting("R-A");
        AddRouting("R-B");
        AddOperation("R-A", 10, ("B", 1m, 0m));
        AddOperation("R-B", 10, ("A", 1m, 0m));    // B 又用回 A → 循環
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("A", Today);

        Assert.Contains(result.Warnings, w => PzAssert.IsFrom("Mfg_BomBranchCycleFmt", w));
        // A(root) → B(1) → A(2, 標 Buy 不再展開)
        Assert.Equal(2, result.BomRows.Count);
        var cyclic = result.BomRows.Single(r => r.ProductCode == "A");
        Assert.Equal(2, cyclic.Level);
        Assert.Equal("Buy", cyclic.SourceType);
    }

    // ── 同品號多分支：不同路徑允許重複出現（非循環）──────────────────────────

    [Fact]
    public async Task Explode_SharedComponentInTwoBranches_AppearsInBoth()
    {
        AddProduct("FG", "R-FG");
        AddProduct("SFG1", "R-S1");
        AddProduct("SFG2", "R-S2");
        AddProduct("COMMON");
        AddRouting("R-FG");
        AddRouting("R-S1");
        AddRouting("R-S2");
        AddOperation("R-FG", 10, ("SFG1", 1m, 0m), ("SFG2", 1m, 0m));
        AddOperation("R-S1", 10, ("COMMON", 2m, 0m));
        AddOperation("R-S2", 10, ("COMMON", 3m, 0m));
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("FG", Today);

        Assert.Empty(result.Warnings);
        var commons = result.BomRows.Where(r => r.ProductCode == "COMMON").ToList();
        Assert.Equal(2, commons.Count);
        Assert.Equal(new[] { 2m, 3m }, commons.Select(c => c.RequiredQty).OrderBy(x => x).ToArray());
    }

    // ── 深度上限：超過 MaxDepth 截斷並警告 ──────────────────────────────────

    [Fact]
    public async Task Explode_ExceedsMaxDepth_TruncatesWithWarning()
    {
        int chain = MultiLevelBomService.MaxDepth + 2;
        for (int i = 0; i <= chain; i++)
        {
            var hasChild = i < chain;
            AddProduct($"P{i}", hasChild ? $"R{i}" : null);
            if (hasChild)
            {
                AddRouting($"R{i}");
                AddOperation($"R{i}", 10, ($"P{i + 1}", 1m, 0m));
            }
        }
        await _context.SaveChangesAsync();

        var result = await MakeService().ExplodeAsync("P0", Today);

        Assert.Contains(result.Warnings, w => PzAssert.IsFrom("Mfg_BomDepthTruncatedFmt", w));
        Assert.True(result.RoutingNodes.Count <= MultiLevelBomService.MaxDepth);
    }
}
