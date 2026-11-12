using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// 庫存模式資料流測試：對比 inv_simplified_mode=true（簡易）與 false（完整）兩種模式下
/// StockMovement、Inventory、callback 的行為差異。
/// 測試直接呼叫 InventoryPostingService，不經 Controller 層。
/// </summary>
public class InvModeFlowTests : TestBase
{
    private readonly InventoryService _invSvc;
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc;
    private readonly Mock<ISystemParamService> _mockSysParamSvc;
    private readonly Mock<IDateTimeService> _mockDt;
    private readonly InventoryPostingService _postingSvc;
    private const string PlantId = "P001";
    private static readonly DateOnly PostDate = new DateOnly(2026, 1, 15);

    public InvModeFlowTests()
    {
        _context.CurrentPlantId = PlantId;
        _invSvc = new InventoryService(_context);
        _mockCostSvc = new Mock<IInventoryCostLayerService>();
        _mockSysParamSvc = new Mock<ISystemParamService>();
        _mockDt = new Mock<IDateTimeService>();
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(PostDate);
        _postingSvc = new InventoryPostingService(
            _context, _invSvc, _mockCostSvc.Object, _mockSysParamSvc.Object, _mockDt.Object);

        SeedCommonData();
    }

    private void SeedCommonData()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC-A", LocationName = "A 區" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC-B", LocationName = "B 區" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "LOC-C", LocationName = "C 區" });

        var cat = new ProductCategory
        {
            CategoryCode = "RAW", CategoryName = "原料",
            CostMethod = CostMethod.MovingAverage, IsActive = true
        };
        _context.ProductCategories.Add(cat);
        _context.Products.Add(new Product { ProductCode = "P-ALPHA", CategoryCode = "RAW", IsActive = true });
        _context.Products.Add(new Product { ProductCode = "P-BETA",  CategoryCode = "RAW", IsActive = true });
        _context.Products.Add(new Product { ProductCode = "P-GAMMA", CategoryCode = "RAW", IsActive = true });

        _context.SaveChanges();
    }

    // ══════════════════════════════════════════════════════════════
    // 簡易模式（inv_simplified_mode = true）→ PostDocSimplifiedAsync
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// S1：簡易模式單行 → StockMovement.StockQty 與過帳數量吻合。
    /// </summary>
    [Fact]
    public async Task S1_SimplifiedMode_SingleLine_StockMovementCreated_QtyCorrect()
    {
        var req = BuildSimplifiedReq("GR-S1-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 120m, unitPrice: 10m),
        });

        await _postingSvc.PostDocSimplifiedAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .SingleAsync(s => s.DocNo == "GR-S1-001" && s.DocLineNo == 1);
        Assert.Equal(120m, sm.StockQty);
        Assert.Equal(1, sm.Direction);
    }

    /// <summary>
    /// S2：簡易模式單行 → Inventory.QtyOnHand 在呼叫後立即更新。
    /// </summary>
    [Fact]
    public async Task S2_SimplifiedMode_SingleLine_QtyOnHandUpdated_Immediately()
    {
        var req = BuildSimplifiedReq("GR-S2-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 80m, unitPrice: 15m),
        });

        await _postingSvc.PostDocSimplifiedAsync(req);
        await _context.SaveChangesAsync();

        var qty = await _invSvc.GetQtyOnHandAsync(PlantId, "P-ALPHA", "LOC-A");
        Assert.Equal(80m, qty);
    }

    /// <summary>
    /// S3：簡易模式單行 → SetQtyPutAway callback 以全量呼叫。
    /// </summary>
    [Fact]
    public async Task S3_SimplifiedMode_SingleLine_QtyPutAwaySetToFullQty()
    {
        var capturedQtyPutAway = 0m;
        var req = BuildSimplifiedReq("GR-S3-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 60m, unitPrice: 5m,
                 setQtyPutAway: q => capturedQtyPutAway = q),
        });

        await _postingSvc.PostDocSimplifiedAsync(req);

        Assert.Equal(60m, capturedQtyPutAway);
    }

    /// <summary>
    /// S4：簡易模式 → SetDocPosted 永遠在過帳結束後呼叫（不需任何條件）。
    /// </summary>
    [Fact]
    public async Task S4_SimplifiedMode_AllLines_SetDocPostedInvokedAtEnd()
    {
        var docPosted = false;
        var req = BuildSimplifiedReq("GR-S4-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 10m, unitPrice: 10m),
        }, onDocPosted: () => docPosted = true);

        await _postingSvc.PostDocSimplifiedAsync(req);

        Assert.True(docPosted);
    }

    /// <summary>
    /// S5：簡易模式多行 → 每行產生獨立 StockMovement，彼此不干擾。
    /// </summary>
    [Fact]
    public async Task S5_SimplifiedMode_MultiLine_EachLineHasIndependentMovement()
    {
        var req = BuildSimplifiedReq("GR-S5-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 100m, unitPrice: 10m),
            Line(2, "P-BETA",  "LOC-B", qty:  50m, unitPrice: 20m),
            Line(3, "P-GAMMA", "LOC-C", qty:  30m, unitPrice: 30m),
        });

        await _postingSvc.PostDocSimplifiedAsync(req);
        await _context.SaveChangesAsync();

        var movements = await _context.StockMovements.IgnoreQueryFilters()
            .Where(s => s.DocNo == "GR-S5-001")
            .OrderBy(s => s.DocLineNo)
            .ToListAsync();

        Assert.Equal(3, movements.Count);
        Assert.Equal(100m, movements[0].StockQty);
        Assert.Equal(50m,  movements[1].StockQty);
        Assert.Equal(30m,  movements[2].StockQty);

        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "P-ALPHA", "LOC-A"));
        Assert.Equal(50m,  await _invSvc.GetQtyOnHandAsync(PlantId, "P-BETA",  "LOC-B"));
        Assert.Equal(30m,  await _invSvc.GetQtyOnHandAsync(PlantId, "P-GAMMA", "LOC-C"));
    }

    /// <summary>
    /// S6：簡易模式 BVA — 最小數量（0.0001）不截斷，QtyOnHand 精確吻合。
    /// </summary>
    [Fact]
    public async Task S6_SimplifiedMode_BVA_MinimalQty_QtyOnHandExact()
    {
        var req = BuildSimplifiedReq("GR-S6-001", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 0.0001m, unitPrice: 100m),
        });

        await _postingSvc.PostDocSimplifiedAsync(req);
        await _context.SaveChangesAsync();

        var qty = await _invSvc.GetQtyOnHandAsync(PlantId, "P-ALPHA", "LOC-A");
        Assert.Equal(0.0001m, qty);

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .SingleAsync(s => s.DocNo == "GR-S6-001");
        Assert.Equal(0.0100m, sm.TotalCost); // 0.0001 × 100
    }

    // ══════════════════════════════════════════════════════════════
    // 完整模式（inv_simplified_mode = false）→ PostLineDetailAsync
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F1：完整模式 — 部分行完成時 SetDocPosted 不觸發。
    /// </summary>
    [Fact]
    public async Task F1_FullMode_PartialPutAway_SetDocPostedNotCalled()
    {
        var docPosted = false;
        var req = BuildDetailReq("GR-F1-001", lineNo: 1,
            qty: 100m, unitPrice: 10m,
            allLinesComplete: false,
            onDocPosted: () => docPosted = true);

        await _postingSvc.PostLineDetailAsync(req);

        Assert.False(docPosted);
    }

    /// <summary>
    /// F2：完整模式 — 最後一行完成時 SetDocPosted 觸發。
    /// </summary>
    [Fact]
    public async Task F2_FullMode_AllLinesPutAway_SetDocPostedCalled()
    {
        var docPosted = false;
        var req = BuildDetailReq("GR-F2-001", lineNo: 1,
            qty: 100m, unitPrice: 10m,
            allLinesComplete: true,
            onDocPosted: () => docPosted = true);

        await _postingSvc.PostLineDetailAsync(req);

        Assert.True(docPosted);
    }

    /// <summary>
    /// F3：完整模式 — 同品號分兩次過帳，QtyOnHand 累積。
    /// 第一次入庫 100，第二次入庫 50 → 期末 150。
    /// </summary>
    [Fact]
    public async Task F3_FullMode_SplitPutAway_QtyOnHandAccumulates()
    {
        var req1 = BuildDetailReq("GR-F3-001", lineNo: 1,
            qty: 100m, unitPrice: 10m, allLinesComplete: false);
        var req2 = BuildDetailReq("GR-F3-001", lineNo: 2,
            qty: 50m, unitPrice: 10m, allLinesComplete: true);

        await _postingSvc.PostLineDetailAsync(req1);
        await _context.SaveChangesAsync();
        await _postingSvc.PostLineDetailAsync(req2);
        await _context.SaveChangesAsync();

        var total = await _invSvc.GetQtyOnHandAsync(PlantId, "P-ALPHA", "LOC-A");
        Assert.Equal(150m, total);

        var movCount = await _context.StockMovements.IgnoreQueryFilters()
            .CountAsync(s => s.DocNo == "GR-F3-001");
        Assert.Equal(2, movCount);
    }

    // ══════════════════════════════════════════════════════════════
    // 模式比較
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// M1：模式比較 — 簡易模式恆觸發 SetDocPosted；完整模式依賴 CheckAllLinesCompleteAsync。
    /// </summary>
    [Fact]
    public async Task M1_ModeComparison_Simplified_AlwaysPosted_Full_RequiresCallback()
    {
        // 簡易模式：無條件觸發
        var simplifiedPosted = false;
        var simplifiedReq = BuildSimplifiedReq("GR-M1-S", new[]
        {
            Line(1, "P-ALPHA", "LOC-A", qty: 10m, unitPrice: 5m),
        }, onDocPosted: () => simplifiedPosted = true);

        await _postingSvc.PostDocSimplifiedAsync(simplifiedReq);
        Assert.True(simplifiedPosted, "簡易模式應在呼叫後立即觸發 SetDocPosted");

        // 完整模式（未全完成）：不觸發
        var fullNotPosted = false;
        var fullReqIncomplete = BuildDetailReq("GR-M1-F-INC", lineNo: 1,
            qty: 10m, unitPrice: 5m, allLinesComplete: false,
            onDocPosted: () => fullNotPosted = true);

        await _postingSvc.PostLineDetailAsync(fullReqIncomplete);
        Assert.False(fullNotPosted, "完整模式在部分完成時不應觸發 SetDocPosted");

        // 完整模式（全部完成）：觸發
        var fullPostedWhenDone = false;
        var fullReqComplete = BuildDetailReq("GR-M1-F-DONE", lineNo: 1,
            qty: 10m, unitPrice: 5m, allLinesComplete: true,
            onDocPosted: () => fullPostedWhenDone = true);

        await _postingSvc.PostLineDetailAsync(fullReqComplete);
        Assert.True(fullPostedWhenDone, "完整模式在全部完成時應觸發 SetDocPosted");
    }

    // ══════════════════════════════════════════════════════════════
    // 建構輔助
    // ══════════════════════════════════════════════════════════════

    private PostDocSimplifiedRequest BuildSimplifiedReq(
        string docNo,
        SimplifiedPostLine[] lines,
        Action? onDocPosted = null)
    {
        return new PostDocSimplifiedRequest
        {
            PlantId      = PlantId,
            DocNo        = docNo,
            MovementDate = PostDate,
            TypeCode     = "PI",
            TypeCategory = "Purchase",
            CreatedBy    = "test-user",
            Lines        = lines.ToList(),
            SetDocPosted = onDocPosted ?? (() => { }),
        };
    }

    private SimplifiedPostLine Line(
        int lineNo, string productCode, string locationCode,
        decimal qty, decimal? unitPrice = null,
        Action<decimal>? setQtyPutAway = null)
    {
        return new SimplifiedPostLine
        {
            LineNo       = lineNo,
            ProductCode  = productCode,
            LocationCode = locationCode,
            WhCode       = "WH01",
            Qty          = qty,
            UomCode      = "PC",
            StockUomCode = "PC",
            UnitPrice    = unitPrice,
            SetQtyPutAway = setQtyPutAway ?? (_ => { }),
        };
    }

    private PostLineDetailRequest BuildDetailReq(
        string docNo, int lineNo,
        decimal qty, decimal? unitPrice = null,
        bool allLinesComplete = true,
        Action? onDocPosted = null)
    {
        return new PostLineDetailRequest
        {
            PlantId      = PlantId,
            DocNo        = docNo,
            LineNo       = lineNo,
            ProductCode  = "P-ALPHA",
            LocationCode = "LOC-A",
            Qty          = qty,
            TypeCode     = "PI",
            TypeCategory = "Purchase",
            MovementDate = PostDate,
            UomCode      = "PC",
            StockUomCode = "PC",
            UnitPrice    = unitPrice,
            CreatedBy    = "test-user",
            UpdateQtyPutAwayAsync    = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(allLinesComplete),
            SetDocPosted = onDocPosted ?? (() => { }),
        };
    }
}
