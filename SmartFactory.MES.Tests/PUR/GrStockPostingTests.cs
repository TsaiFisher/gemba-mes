using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// GR 確認→StockMovement 過帳數量與成本一致性（§5.10 Gap B）。
///
/// 稽核原則：每筆 GR Line 呼叫 PostLineDetailAsync 後，
///   StockMovement.StockQty  = GRLine.StockQty
///   StockMovement.TotalCost = UnitCost × StockQty
///   StockMovement.DocNo     = GoodsReceiptId
///   StockMovement.SourceDocNo = PurchaseOrderId（有 PO 來源時）
/// </summary>
public class GrStockPostingTests : TestBase
{
    private const string PlantId     = "P001";
    private const string LocationCode = "A-01";
    private const string ProductCode  = "MAT001";

    private readonly InventoryService        _invSvc;
    private readonly InventoryPostingService _postingSvc;
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc = new();
    private readonly Mock<ISystemParamService>        _mockSysParamSvc = new();
    private readonly Mock<IDateTimeService>           _mockDt = new();

    public GrStockPostingTests()
    {
        _context.CurrentPlantId = PlantId;
        _invSvc     = new InventoryService(_context);
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2025, 1, 5));
        _postingSvc = new InventoryPostingService(_context, _invSvc, _mockCostSvc.Object,
                                                   _mockSysParamSvc.Object, _mockDt.Object);
        SeedCommonData();
    }

    private void SeedCommonData()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = LocationCode, LocationName = "A-01" });
        var cat = new ProductCategory { CategoryCode = "RAW", CategoryName = "原料", CostMethod = CostMethod.MovingAverage, IsActive = true };
        _context.ProductCategories.Add(cat);
        _context.Products.Add(new Product { ProductCode = ProductCode, CategoryCode = "RAW", IsActive = true });
        _context.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // B1：單行 GR 過帳 → StockMovement qty 吻合（§5.10 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.10 EP Valid: 單行 GR 過帳，StockMovement.StockQty 精確等於 GRLine.StockQty</summary>
    [Fact]
    public async Task GapB_SingleLinePost_StockMovementQtyMatchesGrLine()
    {
        const string grId = "GR-B001";
        var movDate = new DateOnly(2025, 1, 5);

        var req = BuildPostReq(grId, lineNo: 1, qty: 150m, unitPrice: 80m, movDate);
        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(m => m.DocNo == grId && m.DocLineNo == 1);
        Assert.Equal(150m, sm.StockQty);
        Assert.Equal(1,    sm.Direction);  // 入庫
    }

    // ════════════════════════════════════════════════════════════════════════
    // B2：TotalCost = UnitCost × StockQty（§5.10 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.10 EP Valid: StockMovement.TotalCost = UnitPrice × StockQty，無浮點精度損失</summary>
    [Fact]
    public async Task GapB_SingleLinePost_TotalCostEqualsUnitPriceTimesQty()
    {
        const string grId = "GR-B002";
        var movDate = new DateOnly(2025, 1, 5);

        var req = BuildPostReq(grId, lineNo: 1, qty: 200m, unitPrice: 35.5m, movDate);
        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(m => m.DocNo == grId);
        var expectedCost = 200m * 35.5m;  // = 7100.0
        Assert.Equal(expectedCost, sm.TotalCost!.Value, precision: 4);
        Assert.Equal(35.5m,        sm.UnitCost!.Value,  precision: 6);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B3：來源追蹤（DocNo = GR id，SourceDocNo = PO id）（§5.10 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.10 EP Valid: StockMovement.DocNo = GoodsReceiptId，SourceDocNo = PurchaseOrderId</summary>
    [Fact]
    public async Task GapB_PostWithPoSource_SourceDocNoTracked()
    {
        const string grId = "GR-B003";
        const string poId = "PO-B003";
        var movDate = new DateOnly(2025, 1, 5);

        var req = BuildPostReq(grId, lineNo: 1, qty: 50m, unitPrice: 100m, movDate,
                               sourceDocId: poId, sourceLineNo: 1);
        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(m => m.DocNo == grId);
        Assert.Equal(grId, sm.DocNo);
        Assert.Equal(poId, sm.SourceDocNo);
        Assert.Equal(1,    sm.SourceDocLineNo);
    }

    // ════════════════════════════════════════════════════════════════════════
    // B4：多行 GR → 逐行獨立 StockMovement（§5.10 EP Valid）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.10 EP Valid: 兩行不同品號/數量的 GR，各自對應一筆 StockMovement，數量獨立正確</summary>
    [Fact]
    public async Task GapB_MultiLinePost_EachLineHasIndependentMovement()
    {
        // 先補充第二個品號
        var cat2 = new ProductCategory { CategoryCode = "SEMI", CategoryName = "半成品", CostMethod = CostMethod.MovingAverage, IsActive = true };
        _context.ProductCategories.Add(cat2);
        _context.Products.Add(new Product { ProductCode = "MAT002", CategoryCode = "SEMI", IsActive = true });
        _context.SaveChanges();

        const string grId = "GR-B004";
        var movDate = new DateOnly(2025, 1, 5);

        // 行 1：MAT001, qty=100
        var req1 = BuildPostReq(grId, lineNo: 1, qty: 100m, unitPrice: 10m, movDate, productCode: ProductCode);
        await _postingSvc.PostLineDetailAsync(req1);

        // 行 2：MAT002, qty=30
        var req2 = BuildPostReq(grId, lineNo: 2, qty: 30m, unitPrice: 25m, movDate, productCode: "MAT002");
        await _postingSvc.PostLineDetailAsync(req2);
        await _context.SaveChangesAsync();

        var movements = await _context.StockMovements.IgnoreQueryFilters()
            .Where(m => m.DocNo == grId).ToListAsync();
        Assert.Equal(2, movements.Count);

        var sm1 = movements.Single(m => m.DocLineNo == 1);
        var sm2 = movements.Single(m => m.DocLineNo == 2);

        Assert.Equal(100m, sm1.StockQty);
        Assert.Equal(30m,  sm2.StockQty);
        Assert.Equal(1000m, sm1.TotalCost!.Value, precision: 4);   // 100 × 10
        Assert.Equal(750m,  sm2.TotalCost!.Value, precision: 4);   // 30 × 25
    }

    // ════════════════════════════════════════════════════════════════════════
    // B5：BVA 微量精度（§5.10 BVA）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>§5.10 BVA: StockQty = 0.0001，TotalCost = UnitCost × 0.0001，精確至 4 位小數不截斷</summary>
    [Fact]
    public async Task GapB_MinimalQty_TotalCostNotTruncated()
    {
        const string grId = "GR-B005";
        var movDate = new DateOnly(2025, 1, 5);

        var req = BuildPostReq(grId, lineNo: 1, qty: 0.0001m, unitPrice: 100m, movDate);
        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstAsync(m => m.DocNo == grId);
        Assert.Equal(0.0001m,  sm.StockQty,           precision: 4);
        Assert.Equal(0.01m,    sm.TotalCost!.Value,   precision: 4);  // 0.0001 × 100 = 0.0100
    }

    // ════════════════════════════════════════════════════════════════════════
    // B6：稽核查詢—StockMovement vs GR Line 不一致可偵測
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>稽核 LINQ：植入不一致資料後，查詢能偵測 StockMovement.StockQty ≠ GRLine.StockQty</summary>
    [Fact]
    public void GapB_AuditQuery_StockMovementQtyMismatch_Detectable()
    {
        const string grId = "GR-AUDIT01";

        // 植入 GR Line（StockQty=100）
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId, ReceiptDate = new DateOnly(2025, 2, 1),
            CreatedBy = "user1", SupplierCode = "SUP001",
            Status = DocStatus.Posted, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId, LineNo = 1, ProductCode = ProductCode,
            ReceiptQty = 100m, StockQty = 100m, UomCode = "PC", StockUomCode = "PC",
            UnitPrice = 50m,
        });
        _context.GoodsReceipts.Add(gr);

        // 植入不一致的 StockMovement（StockQty=90，故意少）
        _context.StockMovements.Add(new StockMovement
        {
            TypeCode = "PI", DocNo = grId, DocLineNo = 1,
            MovementDate = new DateOnly(2025, 2, 1),
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, UomCode = "PC", StockUomCode = "PC",
            Qty = 90m, StockQty = 90m,   // 故意不一致
            Direction = 1, UnitCost = 50m, TotalCost = 4500m,
        });
        _context.SaveChanges();

        // 稽核查詢：GR LineStockQty vs StockMovement StockQty 不吻合
        var mismatch = (
            from grl in _context.GoodsReceiptLines
            join sm  in _context.StockMovements
                on new { grl.GoodsReceiptId, LineNo = grl.LineNo }
                equals new { GoodsReceiptId = sm.DocNo, LineNo = sm.DocLineNo }
            where grl.GoodsReceiptId == grId
               && sm.Direction == 1
               && grl.StockQty != sm.StockQty
            select new { grl.GoodsReceiptId, grl.LineNo, GrQty = grl.StockQty, SmQty = sm.StockQty }
        ).ToList();

        Assert.Single(mismatch);
        Assert.Equal(10m, mismatch[0].GrQty - mismatch[0].SmQty, precision: 4);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 輔助方法
    // ════════════════════════════════════════════════════════════════════════

    private PostLineDetailRequest BuildPostReq(
        string docNo, int lineNo, decimal qty, decimal unitPrice, DateOnly movDate,
        string? productCode = null, string? sourceDocId = null, int? sourceLineNo = null)
    {
        var prod = productCode ?? ProductCode;
        var qtyPutAway = 0m;

        return new PostLineDetailRequest
        {
            PlantId      = PlantId,
            DocNo        = docNo,
            LineNo       = lineNo,
            ProductCode  = prod,
            LocationCode = LocationCode,
            Qty          = qty,
            TypeCode     = "PI",
            TypeCategory = "Purchase",
            MovementDate = movDate,
            UomCode      = "PC",
            StockUomCode = "PC",
            UnitPrice    = unitPrice,
            CreatedBy    = "user1",
            SourceDocId  = sourceDocId,
            SourceLineNo = sourceLineNo,
            UpdateQtyPutAwayAsync    = q => { qtyPutAway += q; return Task.CompletedTask; },
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted             = () => { },
        };
    }
}
