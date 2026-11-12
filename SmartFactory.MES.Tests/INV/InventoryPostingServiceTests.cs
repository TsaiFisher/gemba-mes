using SmartFactory.MES.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// InventoryPostingService 單元測試：逐筆過帳、簡化模式、作廢沖銷。
/// </summary>
public class InventoryPostingServiceTests : TestBase
{
    private readonly InventoryService _invSvc;
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc;
    private readonly Mock<ISystemParamService> _mockSysParamSvc;
    private readonly Mock<IDateTimeService> _mockDt;
    private readonly InventoryPostingService _postingSvc;
    private const string PlantId = "P001";

    public InventoryPostingServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _invSvc = new InventoryService(_context);
        _mockCostSvc = new Mock<IInventoryCostLayerService>();
        _mockSysParamSvc = new Mock<ISystemParamService>();
        _mockDt = new Mock<IDateTimeService>();
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 20));
        _postingSvc = new InventoryPostingService(_context, _invSvc, _mockCostSvc.Object, _mockSysParamSvc.Object, _mockDt.Object);

        SeedCommonData();
    }

    private void SeedCommonData()
    {
        // 倉儲
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-02", LocationName = "A-02" });

        // PI, PF, IA are pre-seeded by HasData — no explicit Add needed

        // 產品類別與產品
        var cat = new ProductCategory { CategoryCode = "RAW", CategoryName = "原料", CostMethod = CostMethod.MovingAverage, IsActive = true };
        _context.ProductCategories.Add(cat);
        _context.Products.Add(new Product { ProductCode = "MAT001", CategoryCode = "RAW", IsActive = true });
        _context.Products.Add(new Product { ProductCode = "MAT002", CategoryCode = "RAW", IsActive = true });

        _context.SaveChanges();
    }

    // ── PostLineDetailAsync（完整模式入庫）──

    [Fact]
    public async Task PostLineDetail_Inbound_ShouldCreateMovementAndUpdateInventory()
    {
        var qtyPutAway = 0m;
        var docPosted = false;

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000001",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 100,
            TypeCode = "PI",
            TypeCategory = "Purchase",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = 10,
            CreatedBy = "user1",
            UpdateQtyPutAwayAsync = qty => { qtyPutAway += qty; return Task.CompletedTask; },
            CheckAllLinesCompleteAsync = () => Task.FromResult(true),
            SetDocPosted = () => docPosted = true,
        };

        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        // 驗證 Inventory
        var invQty = await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01");
        Assert.Equal(100m, invQty);

        // 驗證 StockMovement
        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.DocNo == "PE2025000001" && s.DocLineNo == 1);
        Assert.NotNull(sm);
        Assert.Equal(1, sm!.Direction);
        Assert.Equal(10m, sm.UnitCost);
        Assert.Equal(1000m, sm.TotalCost);

        // 驗證 QtyPutAway callback
        Assert.Equal(100m, qtyPutAway);

        // 驗證 DocStatus callback（所有行完成）
        Assert.True(docPosted);
    }

    [Fact]
    public async Task PostLineDetail_LotControlled_MissingLot_ShouldThrow()
    {
        // 準備 LotControlled 產品
        _context.Products.Add(new Product { ProductCode = "MAT-LOT", CategoryCode = "RAW", IsLotControlled = true, IsActive = true });
        await _context.SaveChangesAsync();

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "LOT-TEST-001",
            LineNo = 1,
            ProductCode = "MAT-LOT",
            LocationCode = "A-01",
            LotNo = "", // Missing
            Qty = 100,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted = () => { },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _postingSvc.PostLineDetailAsync(req));
        PzAssert.Message("Inv_LotRequiredFmt", ex.Message);
    }

    [Fact]
    public async Task PostLineDetail_Inbound_NegativeUnitPrice_ShouldThrow()
    {
        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "NEG-PRICE-001",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 100,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = -10m, // 負單價
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted = () => { },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _postingSvc.PostLineDetailAsync(req));
        PzAssert.Message("Inv_NegativeInboundPriceFmt", ex.Message);
        Assert.Contains("MAT001", ex.Message);
    }

    [Fact]
    public async Task PostDocSimplified_Inbound_NegativeUnitPrice_ShouldThrow()
    {
        var req = new PostDocSimplifiedRequest
        {
            PlantId = PlantId,
            DocNo = "NEG-PRICE-002",
            MovementDate = new DateOnly(2025, 4, 1),
            TypeCode = "PI",
            TypeCategory = "Purchase",
            CreatedBy = "user1",
            Lines = new List<SimplifiedPostLine>
            {
                new()
                {
                    LineNo = 1, ProductCode = "MAT001",
                    LocationCode = "A-01", WhCode = "WH01",
                    Qty = 50, UomCode = "PC", StockUomCode = "PC",
                    UnitPrice = -5m, // 負單價
                    SetQtyPutAway = _ => { }
                }
            },
            SetDocPosted = () => { }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _postingSvc.PostDocSimplifiedAsync(req));
        PzAssert.Message("Inv_NegativeInboundPriceFmt", ex.Message);
        Assert.Contains("MAT001", ex.Message);
    }

    [Fact]
    public async Task PostLineDetail_Inbound_NotAllComplete_ShouldNotSetPosted()
    {
        var docPosted = false;

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000002",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 50,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = 10,
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false), // 尚未全部完成
            SetDocPosted = () => docPosted = true,
        };

        await _postingSvc.PostLineDetailAsync(req);
        Assert.False(docPosted);
    }

    // ── PostLineDetailAsync（完整模式出庫）──

    [Fact]
    public async Task PostLineDetail_Outbound_ShouldDecrementInventory()
    {
        // 先入庫 100
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        await _context.SaveChangesAsync();

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PF2025000001",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 30,
            TypeCode = "PF",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(true),
            SetDocPosted = () => { },
        };

        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var qty = await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01");
        Assert.Equal(70m, qty);

        // 出庫 StockMovement 應取現有均價
        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.DocNo == "PF2025000001");
        Assert.Equal(10m, sm!.UnitCost);
        Assert.Equal(-1, sm.Direction);
    }

    [Fact]
    public async Task PostLineDetail_Outbound_InsufficientQty_ShouldThrow()
    {
        // 庫存只有 20，嘗試出庫 50
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 20,
        });
        await _context.SaveChangesAsync();

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PF2025000002",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 50,
            TypeCode = "PF",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted = () => { },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _postingSvc.PostLineDetailAsync(req));
    }

    // ── PostDocSimplifiedAsync（簡化模式）──

    [Fact]
    public async Task PostDocSimplified_ShouldPostAllLinesAndSetPosted()
    {
        var docPosted = false;
        var line1QtyPutAway = 0m;
        var line2QtyPutAway = 0m;

        var req = new PostDocSimplifiedRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000010",
            MovementDate = new DateOnly(2025, 4, 1),
            TypeCode = "PI",
            TypeCategory = "Purchase",
            CreatedBy = "user1",
            Lines = new List<SimplifiedPostLine>
            {
                new()
                {
                    LineNo = 1, ProductCode = "MAT001",
                    LocationCode = "A-01", WhCode = "WH01",
                    Qty = 100, UomCode = "PC", StockUomCode = "PC",
                    UnitPrice = 10,
                    SetQtyPutAway = qty => line1QtyPutAway = qty,
                },
                new()
                {
                    LineNo = 2, ProductCode = "MAT002",
                    LocationCode = "A-02", WhCode = "WH01",
                    Qty = 50, UomCode = "PC", StockUomCode = "PC",
                    UnitPrice = 20,
                    SetQtyPutAway = qty => line2QtyPutAway = qty,
                },
            },
            SetDocPosted = () => docPosted = true,
        };

        await _postingSvc.PostDocSimplifiedAsync(req);
        await _context.SaveChangesAsync();

        // Inventory
        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
        Assert.Equal(50m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT002", "A-02"));

        // StockMovements
        var count = await _context.StockMovements.IgnoreQueryFilters()
            .CountAsync(s => s.DocNo == "PE2025000010");
        Assert.Equal(2, count);

        // QtyPutAway
        Assert.Equal(100m, line1QtyPutAway);
        Assert.Equal(50m, line2QtyPutAway);

        // DocStatus
        Assert.True(docPosted);
    }

    [Fact]
    public async Task PostDocSimplified_LotControlled_MissingLot_ShouldThrow()
    {
        _context.Products.Add(new Product { ProductCode = "MAT-LOT-S", CategoryCode = "RAW", IsLotControlled = true, IsActive = true });
        await _context.SaveChangesAsync();

        var req = new PostDocSimplifiedRequest
        {
            PlantId = PlantId,
            DocNo = "SIMP-LOT",
            MovementDate = new DateOnly(2025, 4, 1),
            TypeCode = "PI",
            TypeCategory = "Purchase",
            CreatedBy = "user1",
            Lines = new List<SimplifiedPostLine>
            {
                new()
                {
                    LineNo = 1, ProductCode = "MAT-LOT-S",
                    LocationCode = "A-01", WhCode = "WH01",
                    Qty = 50, UomCode = "PC", StockUomCode = "PC",
                    LotNo = "", // Missing
                    SetQtyPutAway = _ => { }
                }
            },
            SetDocPosted = () => { }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _postingSvc.PostDocSimplifiedAsync(req));
        PzAssert.Message("Inv_LotRequiredFmt", ex.Message);
    }

    // ── VoidDocAsync ──

    [Fact]
    public async Task VoidDoc_ShouldReverseMovementsAndInventory()
    {
        // 先入庫
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, TypeCode = "PI", DocNo = "PE2025000020", DocLineNo = 1,
            MovementDate = new DateOnly(2025, 4, 1),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 100, StockUomCode = "PC", StockQty = 100,
            Direction = 1, UnitCost = 10, TotalCost = 1000,
        });
        await _context.SaveChangesAsync();

        var docVoid = false;
        var req = new VoidDocRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000020",
            Reason = "測試作廢",
            CreatedBy = "user1",
            SetDocVoid = () => docVoid = true,
        };

        await _postingSvc.VoidDocAsync(req);
        await _context.SaveChangesAsync();

        // 庫存應歸零
        Assert.Equal(0m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));

        // 應有 2 筆 StockMovement（原始 + 沖銷）
        var movements = await _context.StockMovements.IgnoreQueryFilters()
            .Where(s => s.DocNo == "PE2025000020")
            .ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, m => m.Direction == -1); // 沖銷

        Assert.True(docVoid);
    }

    [Fact]
    public async Task VoidDoc_InsufficientQty_ShouldThrow()
    {
        // 入庫 100 後又出庫 80，剩 20，嘗試作廢原入庫 100（需沖銷出庫 100，但只有 20 可用）
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 20,
        });
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, TypeCode = "PI", DocNo = "PE2025000030", DocLineNo = 1,
            MovementDate = new DateOnly(2025, 4, 1),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 100, StockUomCode = "PC", StockQty = 100,
            Direction = 1, UnitCost = 10, TotalCost = 1000,
        });
        await _context.SaveChangesAsync();

        var req = new VoidDocRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000030",
            Reason = "測試",
            SetDocVoid = () => { },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _postingSvc.VoidDocAsync(req));
    }

    [Fact]
    public async Task VoidDoc_NoMovements_MarksVoidWithoutThrow()
    {
        // 設計變更（commit f8ce747）：單據過帳時無庫存異動（如空白明細）者，
        // 無需沖銷，直接標記作廢且不丟例外。本測試對齊變更後的契約，
        // 取代舊版 VoidDoc_NoMovements_ShouldThrow（已過時，與實作相反）。
        var docVoided = false;
        var req = new VoidDocRequest
        {
            PlantId = PlantId,
            DocNo = "NONEXIST",
            Reason = "測試",
            SetDocVoid = () => docVoided = true,
        };

        await _postingSvc.VoidDocAsync(req);

        Assert.True(docVoided);
    }

    // ── MA 成本入庫過帳驗證 ──

    [Fact]
    public async Task PostLineDetail_Inbound_ShouldUpdateMACost()
    {
        // 先有 100 @ $10
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        await _context.SaveChangesAsync();

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PE2025000040",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 100,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = 20, // 新進 100 @ $20
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted = () => { },
        };

        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        var inv = await _invSvc.GetOrCreateInventoryAsync(PlantId, "MAT001", "A-01", "");
        // (100*10 + 100*20) / 200 = 15
        Assert.Equal(15m, inv.UnitCost);
    }

    [Fact]
    public async Task PostLineDetail_Inbound_ShouldUpdateMACost_PlantWide()
    {
        // 1. 儲位 A-01 有 100 @ $10
        await _invSvc.UpdateQtyAsync(PlantId, "MAT001", "A-01", "", 100);
        var inv1 = await _invSvc.GetOrCreateInventoryAsync(PlantId, "MAT001", "A-01", "");
        inv1.UnitCost = 10;
        
        // 2. 儲位 A-02 有 50 @ $10
        await _invSvc.UpdateQtyAsync(PlantId, "MAT001", "A-02", "", 50);
        var inv2 = await _invSvc.GetOrCreateInventoryAsync(PlantId, "MAT001", "A-02", "");
        inv2.UnitCost = 10;
        
        await _context.SaveChangesAsync();

        // 3. 在 A-01 入庫 50 @ $40
        // 原總值 = (100+50)*10 = 1500
        // 新增值 = 50 * 40 = 2000
        // 新總量 = 150 + 50 = 200
        // 新均價 = (1500 + 2000) / 200 = 3500 / 200 = 17.5
        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "PE-PLANT-MA-TEST",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 50,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = 40,
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(false),
            SetDocPosted = () => { },
        };

        await _postingSvc.PostLineDetailAsync(req);
        await _context.SaveChangesAsync();

        // 驗證 A-01 與 A-02 的成本是否都更新為 17.5
        var res1 = await _invSvc.GetOrCreateInventoryAsync(PlantId, "MAT001", "A-01", "");
        var res2 = await _invSvc.GetOrCreateInventoryAsync(PlantId, "MAT001", "A-02", "");
        
        Assert.Equal(150m, res1.QtyOnHand); 
        Assert.Equal(50m, res2.QtyOnHand);
        Assert.Equal(17.5m, res1.UnitCost);
        Assert.Equal(17.5m, res2.UnitCost);
    }

    [Fact]
    public async Task PostLineDetail_ShouldFallbackToGlobalCostMethod()
    {
        // 1. 設定產品類別為 SystemDefault
        var cat = await _context.ProductCategories.FirstAsync(c => c.CategoryCode == "RAW");
        cat.CostMethod = CostMethod.SystemDefault;
        await _context.SaveChangesAsync();

        // 2. Mock 全域參數為 FIFO
        _mockSysParamSvc.Setup(s => s.GetAsync("inv_default_cost_method")).ReturnsAsync("FIFO");

        var req = new PostLineDetailRequest
        {
            PlantId = PlantId,
            DocNo = "FIFO-TEST",
            LineNo = 1,
            ProductCode = "MAT001",
            LocationCode = "A-01",
            Qty = 10,
            TypeCode = "PI",
            MovementDate = new DateOnly(2025, 4, 1),
            UomCode = "PC",
            StockUomCode = "PC",
            UnitPrice = 100,
            UpdateQtyPutAwayAsync = _ => Task.CompletedTask,
            CheckAllLinesCompleteAsync = () => Task.FromResult(true),
            SetDocPosted = () => { },
        };

        await _postingSvc.PostLineDetailAsync(req);

        // 驗證是否呼叫了 CostSvc.AddLayerAsync (代表走 FIFO 分支)，
        // 且成本層的入庫日取自**本次過帳的業務日**（req.MovementDate），不是系統當天。
        _mockCostSvc.Verify(c => c.AddLayerAsync(
            PlantId, "MAT001", "A-01", "", 10m, 100m, "FIFO-TEST", req.MovementDate,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
