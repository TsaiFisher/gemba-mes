using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// InventoryService 單元測試：餘額查詢、數量更新、MA 成本計算。
/// </summary>
public class InventoryServiceTests : TestBase
{
    private readonly InventoryService _service;
    private const string PlantId = "P001";
    private const string ProductCode = "MAT001";
    private const string LocationCode = "A-01";

    public InventoryServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _service = new InventoryService(_context);

        // 建立基礎儲位
        _context.Warehouses.Add(new Warehouse
        {
            WhCode = "WH01", WhName = "原料倉", PlantId = PlantId,
        });
        _context.StorageLocations.Add(new StorageLocation
        {
            WhCode = "WH01", LocationCode = LocationCode, LocationName = "A 排 01 層",
        });
        _context.SaveChanges();
    }

    // ── GetQtyOnHandAsync ──

    [Fact]
    public async Task GetQtyOnHand_NoRecord_ShouldReturnZero()
    {
        var qty = await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(0m, qty);
    }

    [Fact]
    public async Task GetQtyOnHand_WithRecord_ShouldReturnQty()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100,
        });
        await _context.SaveChangesAsync();

        var qty = await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(100m, qty);
    }

    // ── GetAvailableQtyAsync ──

    [Fact]
    public async Task GetAvailableQty_WithReserved_ShouldSubtract()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100, QtyReserved = 30,
        });
        await _context.SaveChangesAsync();

        var qty = await _service.GetAvailableQtyAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(70m, qty);
    }

    // ── UpdateQtyAsync ──

    [Fact]
    public async Task UpdateQty_NewRecord_ShouldCreateAndSet()
    {
        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "", 50);
        await _context.SaveChangesAsync();

        var qty = await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(50m, qty);
    }

    [Fact]
    public async Task UpdateQty_Existing_ShouldIncrement()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100,
        });
        await _context.SaveChangesAsync();

        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "", 25);
        await _context.SaveChangesAsync();

        var qty = await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(125m, qty);
    }

    [Fact]
    public async Task UpdateQty_Negative_ShouldDecrement()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100,
        });
        await _context.SaveChangesAsync();

        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "", -40);
        await _context.SaveChangesAsync();

        var qty = await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode);
        Assert.Equal(60m, qty);
    }

    // ── UpdateMovingAverageCostAsync ──

    [Fact]
    public async Task UpdateMA_FirstEntry_ShouldSetCost()
    {
        // 新建（QtyOnHand=0），加入 100 個 @ $10
        await _service.UpdateMovingAverageCostAsync(PlantId, ProductCode, LocationCode, "", 100, 10);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(10m, inv.UnitCost);
    }

    [Fact]
    public async Task UpdateMA_SecondEntry_ShouldRecalculate()
    {
        // 先有 100 個 @ $10
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        await _context.SaveChangesAsync();

        // 加入 100 個 @ $20 → 新均價 = (100*10 + 100*20) / 200 = 15
        await _service.UpdateMovingAverageCostAsync(PlantId, ProductCode, LocationCode, "", 100, 20);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(15m, inv.UnitCost);
    }

    [Fact]
    public async Task UpdateMA_ZeroResultQty_ShouldPreserveCost()
    {
        // 有 100 個 @ $10，加入 -100 個（結果為 0）→ 保留原均價
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        await _context.SaveChangesAsync();

        await _service.UpdateMovingAverageCostAsync(PlantId, ProductCode, LocationCode, "", -100, 0);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(10m, inv.UnitCost); // 保留
    }

    // ── GetLocationAsync ──

    [Fact]
    public async Task GetLocation_Existing_ShouldReturn()
    {
        var loc = await _service.GetLocationAsync(LocationCode);
        Assert.NotNull(loc);
        Assert.Equal("WH01", loc!.WhCode);
    }

    [Fact]
    public async Task GetLocation_NotFound_ShouldReturnNull()
    {
        var loc = await _service.GetLocationAsync("NOPE");
        Assert.Null(loc);
    }

    // ── GetOrCreateInventoryAsync ──

    [Fact]
    public async Task GetOrCreate_New_ShouldCreateRecord()
    {
        var inv = await _service.GetOrCreateInventoryAsync(PlantId, "NEW001", LocationCode, "");
        Assert.NotNull(inv);
        Assert.Equal(0m, inv.QtyOnHand);
    }

    [Fact]
    public async Task GetOrCreate_Existing_ShouldReturnSame()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 50,
        });
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(50m, inv.QtyOnHand);
    }

    // ── LotNo 隔離 ──

    [Fact]
    public async Task DifferentLotNo_ShouldBeIndependent()
    {
        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "LOT-A", 100);
        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "LOT-B", 50);
        await _context.SaveChangesAsync();

        Assert.Equal(100m, await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode, "LOT-A"));
        Assert.Equal(50m, await _service.GetQtyOnHandAsync(PlantId, ProductCode, LocationCode, "LOT-B"));
    }

    // ── §5.3 AVCO BVA 邊界案例 ──────────────────────────────────────────────

    /// <summary>
    /// §5.3 BVA 庫存歸零臨界點：出庫使 QtyOnHand 精確歸零後，
    /// UnitCost 必須保留最後一次的均價，不可被清空為 0（亦不引發除以零例外）。
    /// 對應規格書 §5.3 BVA「庫存歸零臨界點」。
    /// </summary>
    [Fact]
    public async Task Avco_OutboundToExactZero_UnitCostPreservedNotCleared()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 50m, UnitCost = 100m,
        });
        await _context.SaveChangesAsync();

        // 出庫剛好清空（UpdateQtyAsync 不更新 UnitCost）
        await _service.UpdateQtyAsync(PlantId, ProductCode, LocationCode, "", -50m);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(0m, inv.QtyOnHand);
        Assert.Equal(100m, inv.UnitCost);   // 均價必須保留，不可歸零
    }

    /// <summary>
    /// §5.3 BVA 庫存歸零後下筆入庫：QtyOnHand=0 時再入庫，
    /// 新 AVCO 應等於本次單價（不會因 currentMa=0 造成加權扭曲）。
    /// </summary>
    [Fact]
    public async Task Avco_AfterZeroInventory_NextInbound_ResetsToCostPrice()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 0m, UnitCost = 100m,   // 已歸零，保留舊均價
        });
        await _context.SaveChangesAsync();

        // 新入庫 10 @ $80 → totalQty=0, newMa = (0×100 + 10×80) / 10 = 80
        await _service.UpdateMovingAverageCostAsync(PlantId, ProductCode, LocationCode, "", 10m, 80m);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(80m, inv.UnitCost);    // 新均價應為本次入庫單價
    }

    /// <summary>
    /// §5.3 EP Invalid：入庫（addQty &gt; 0）時 inUnitCost 為負數，
    /// InventoryService 應拋出 InvalidOperationException，不污染均價。
    /// </summary>
    [Fact]
    public async Task UpdateMA_Inbound_NegativeUnitCost_ShouldThrow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.UpdateMovingAverageCostAsync(
                PlantId, ProductCode, LocationCode, "",
                addQty: 10m, inUnitCost: -5m));
    }

    /// <summary>
    /// §5.3 EP Valid：出庫（addQty &lt; 0）時 inUnitCost 可以為 0，
    /// 不應觸發 guard（出庫不影響 AVCO 計算）。
    /// </summary>
    [Fact]
    public async Task UpdateMA_Outbound_ZeroUnitCost_ShouldNotThrow()
    {
        _context.Inventories.Add(new SmartFactory.MES.Modules.INV.Models.Inventory
        {
            PlantId = PlantId, ProductCode = ProductCode,
            LocationCode = LocationCode, LotNo = "",
            QtyOnHand = 50m, UnitCost = 100m,
        });
        await _context.SaveChangesAsync();

        // 出庫：addQty < 0，inUnitCost 傳 0 不應拋出
        await _service.UpdateMovingAverageCostAsync(
            PlantId, ProductCode, LocationCode, "",
            addQty: -10m, inUnitCost: 0m);
        // 均價保持不變
        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(100m, inv.UnitCost);
    }

    /// <summary>
    /// §5.3 BVA 極端微小值：入庫數量 0.0001，單價 0.000001。
    /// 驗證 C# decimal 乘除不截斷為 0，AVCO 精確保留到小數點後 6 位。
    /// 對應規格書 §5.3 BVA「極端微小值」。
    /// </summary>
    [Fact]
    public async Task Avco_ExtremeSmallQtyAndPrice_DecimalPrecisionNotTruncated()
    {
        // 期初空庫存：totalQty = 0
        // newMa = (0×0 + 0.0001×0.000001) / 0.0001 = 0.000001
        await _service.UpdateMovingAverageCostAsync(
            PlantId, ProductCode, LocationCode, "",
            addQty: 0.0001m, inUnitCost: 0.000001m);
        await _context.SaveChangesAsync();

        var inv = await _service.GetOrCreateInventoryAsync(PlantId, ProductCode, LocationCode, "");
        Assert.Equal(0.000001m, inv.UnitCost);   // 不可截斷為 0
    }
}
