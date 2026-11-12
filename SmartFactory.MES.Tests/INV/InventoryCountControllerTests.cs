using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// InventoryCountController 測試：Index、Create（WithSheet/WithoutSheet）、
/// GenerateItems、SaveFirstCount、SaveRecount（職責分離）、Close（結帳差異過帳）、
/// Delete（有/無 counted items）。
/// </summary>
public class InventoryCountControllerTests : TestBase
{
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLocalizer = new();
    private readonly Mock<IDocNumberService>                        _mockDocNo     = new();
    private readonly Mock<IInventoryPostingService>                 _mockPosting   = new();
    private readonly Mock<IDateTimeService>                         _mockDt        = new();
    private readonly Mock<IExportService>                           _mockExport;
    private readonly Mock<IPermissionService>                       _mockPerm      = new();
    private readonly Mock<IUserContext>                             _mockUser      = new();

    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private const string User2Id = "user2";

    public InventoryCountControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IC2026060001");

        _mockLocalizer
            .Setup(l => l[It.IsAny<string>()])
            .Returns<string>(k => new LocalizedString(k, k));
        _mockLocalizer
            .Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns<string, object[]>((k, _) => new LocalizedString(k, k));

        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(
            It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockExport = GetMockExportService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId, IsActive = true });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "成品倉", PlantId = PlantId, IsActive = true });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A區1號", IsActive = true });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH02", LocationCode = "B-01", LocationName = "B區1號", IsActive = true });
        _context.Inventories.Add(new Inventory { PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "", QtyOnHand = 100, QtyReserved = 0, UnitCost = 5m });
        _context.Inventories.Add(new Inventory { PlantId = PlantId, ProductCode = "MAT002", LocationCode = "B-01", LotNo = "L01",  QtyOnHand = 50,  QtyReserved = 0, UnitCost = 8m });
        _context.SaveChanges();
    }

    private readonly Mock<SmartFactory.MES.Modules.INV.Services.IInventoryCostLayerService> _mockCostLayers = new();

    private InventoryCountController BuildController()
    {
        var ctrl = new InventoryCountController(
            _mockLocalizer.Object, _mockDocNo.Object,
            _mockPosting.Object, _mockDt.Object, _mockExport.Object,
            _mockCostLayers.Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object);
        return ctrl;
    }

    private InventoryCountSheet SeedSheet(string id, string status = "Counting")
    {
        var sheet = new InventoryCountSheet
        {
            SheetId    = id,
            PlantId    = PlantId,
            CountMode  = "WithSheet",
            SheetStatus = status,
            CreatedBy  = UserId,
            SnapshotAt = DateTime.UtcNow,
        };
        _context.InventoryCountSheets.Add(sheet);
        _context.SaveChanges();
        return sheet;
    }

    private InventoryCountItem SeedItem(string sheetId, string itemStatus = "Pending",
        string? countedBy = null, decimal bookQty = 100)
    {
        var item = new InventoryCountItem
        {
            SheetId      = sheetId,
            SeqNo        = 1,
            WhCode       = "WH01",
            LocationCode = "A-01",
            LotNo        = "",
            ProductCode  = "MAT001",
            BookQty      = bookQty,
            ItemStatus   = itemStatus,
            FirstCountedBy = countedBy,
        };
        _context.InventoryCountItems.Add(item);
        _context.SaveChanges();
        return item;
    }

    // ── Index ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl   = BuildController();
        var result = await ctrl.Index(null, null, null, null, searched: false);
        var view   = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedSheet("IC2026060001");
        var ctrl   = BuildController();
        var result = await ctrl.Index(null, null, null, null, searched: true);
        var view   = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<InventoryCountSheet>>(view.Model);
    }

    [Fact]
    public async Task Index_FilterByStatus_ShouldFilter()
    {
        SeedSheet("IC2026060001", "Counting");
        SeedSheet("IC2026060002", "Closed");
        var ctrl   = BuildController();
        var result = await ctrl.Index("Closed", null, null, null, searched: true);
        var view   = Assert.IsType<ViewResult>(result);
        var list   = Assert.IsType<PaginatedList<InventoryCountSheet>>(view.Model);
        Assert.All(list, s => Assert.Equal("Closed", s.SheetStatus));
    }

    // ── Create ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithSheet_ShouldGenerateItems()
    {
        var ctrl  = BuildController();
        var model = new InventoryCountSheet { CountMode = "WithSheet" };
        var result = await ctrl.Create(model);

        var json  = Assert.IsType<JsonResult>(result);
        var sheet = await _context.InventoryCountSheets
            .Include(s => s.Items)
            .FirstAsync();

        Assert.Equal(PlantId, sheet.PlantId);
        Assert.Equal("Counting", sheet.SheetStatus);
        Assert.Equal(2, sheet.Items.Count);  // 兩筆庫存存在且 QtyOnHand > 0
    }

    [Fact]
    public async Task Create_WithoutSheet_ShouldNotGenerateItems()
    {
        var ctrl  = BuildController();
        var model = new InventoryCountSheet { CountMode = "WithoutSheet" };
        var result = await ctrl.Create(model);

        var sheet = await _context.InventoryCountSheets
            .Include(s => s.Items)
            .FirstAsync();
        Assert.Empty(sheet.Items);
    }

    // ── GenerateItems ─────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateItems_Counting_NoPriorCounted_ShouldReplaceItems()
    {
        var sheet = SeedSheet("IC2026060001");
        // 加一筆 Pending 明細（後來會被清除）
        SeedItem(sheet.SheetId, "Pending");

        var ctrl   = BuildController();
        var result = await ctrl.GenerateItems("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? data = json.Value;
        Assert.NotNull(data);

        var items = await _context.InventoryCountItems
            .Where(i => i.SheetId == "IC2026060001")
            .ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("Pending", i.ItemStatus));
    }

    [Fact]
    public async Task GenerateItems_HasCountedItem_ShouldFail()
    {
        SeedSheet("IC2026060001");
        SeedItem("IC2026060001", "Counted");

        var ctrl   = BuildController();
        var result = await ctrl.GenerateItems("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? data = json.Value;
        Assert.NotNull(data);
        Assert.False((bool)data!.GetType().GetProperty("success")!.GetValue(data)!);
    }

    // ── SaveFirstCount ────────────────────────────────────────────────────

    [Fact]
    public async Task SaveFirstCount_Pending_ShouldSetCounted()
    {
        SeedSheet("IC2026060001");
        var item   = SeedItem("IC2026060001", "Pending");

        var ctrl   = BuildController();
        var result = await ctrl.SaveFirstCount(item.ItemId, 90m, null);
        var json   = Assert.IsType<JsonResult>(result);

        var updated = await _context.InventoryCountItems.FindAsync(item.ItemId);
        Assert.Equal(90m, updated!.FirstCountQty);
        Assert.Equal("Counted", updated.ItemStatus);
        Assert.Equal(UserId, updated.FirstCountedBy);
    }

    [Fact]
    public async Task SaveFirstCount_ClosedSheet_ShouldFail()
    {
        SeedSheet("IC2026060001", "Closed");
        var item   = SeedItem("IC2026060001", "Pending");

        var ctrl   = BuildController();
        var result = await ctrl.SaveFirstCount(item.ItemId, 90m, null);
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);
    }

    [Fact]
    public async Task SaveFirstCount_AfterRecount_ShouldFailAndKeepRecount()
    {
        // 覆盤後再改初盤會把狀態寫回 Counted——覆盤結果還在列上，卻又回到待覆盤清單
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Recounted", countedBy: User2Id);

        var ctrl   = BuildController();
        var result = await ctrl.SaveFirstCount(item.ItemId, 70m, null);
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);

        var unchanged = await _context.InventoryCountItems.FindAsync(item.ItemId);
        Assert.Equal("Recounted", unchanged!.ItemStatus);
        Assert.NotEqual(70m, unchanged.FirstCountQty);
    }

    // ── SaveRecount ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveRecount_DifferentUser_ShouldSucceed()
    {
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Counted", countedBy: User2Id); // 初盤人 ≠ UserId

        var ctrl   = BuildController();
        var result = await ctrl.SaveRecount(item.ItemId, 85m, null);
        var json   = Assert.IsType<JsonResult>(result);

        var updated = await _context.InventoryCountItems.FindAsync(item.ItemId);
        Assert.Equal(85m, updated!.RecountQty);
        Assert.Equal("Recounted", updated.ItemStatus);
    }

    [Fact]
    public async Task SaveRecount_SameUser_ShouldFail_SegregationOfDuties()
    {
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Counted", countedBy: UserId); // 初盤人 == 覆盤人

        var ctrl   = BuildController();
        var result = await ctrl.SaveRecount(item.ItemId, 85m, null);
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);
    }

    [Fact]
    public async Task SaveRecount_ItemNotCounted_ShouldFail()
    {
        SeedSheet("IC2026060001");
        var item   = SeedItem("IC2026060001", "Pending"); // 尚未初盤

        var ctrl   = BuildController();
        var result = await ctrl.SaveRecount(item.ItemId, 85m, null);
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);
    }

    // ── Close ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Close_AllItemsCounted_ShouldCloseAndCallPosting()
    {
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Counted");
        item.FirstCountQty = 80m; // Variance = 80 - 100 = -20
        _context.SaveChanges();

        var ctrl   = BuildController();
        var result = await ctrl.Close("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);

        var sheet = await _context.InventoryCountSheets.FindAsync("IC2026060001");
        Assert.Equal("Closed", sheet!.SheetStatus);
        Assert.NotNull(sheet.ClosedAt);
        // 調整單號必須回寫：這是重複結帳守門唯一的依據，沒寫進去等於守門不存在
        Assert.False(string.IsNullOrEmpty(sheet.AdjustmentDocId));

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(
            It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 已回寫調整單號的清冊，即使狀態被改回 Counting 也不得再結帳一次。
    /// 舊防線只有 SheetStatus，狀態一被改回去就完全失守（第二次會另採新單號，
    /// 過帳層以單號為鍵的冪等攔不到），同批差異因而被扣兩次庫存。
    /// </summary>
    [Fact]
    public async Task Close_WhenAdjustmentAlreadyIssued_ShouldFailEvenIfStatusReverted()
    {
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Counted");
        item.FirstCountQty = 80m;
        var seeded = _context.InventoryCountSheets.Find("IC2026060001")!;
        seeded.AdjustmentDocId = "SA2026060099";   // 已結過一次
        seeded.SheetStatus     = "Counting";       // 狀態被改回去
        _context.SaveChanges();

        var ctrl   = BuildController();
        var result = await ctrl.Close("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);

        var sheet = await _context.InventoryCountSheets.FindAsync("IC2026060001");
        Assert.Equal("SA2026060099", sheet!.AdjustmentDocId);   // 沒被新單號蓋掉
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(
            It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_HasPendingItems_ShouldFail()
    {
        SeedSheet("IC2026060001");
        SeedItem("IC2026060001", "Pending"); // 有未初盤明細

        var ctrl   = BuildController();
        var result = await ctrl.Close("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);

        var sheet = await _context.InventoryCountSheets.FindAsync("IC2026060001");
        Assert.Equal("Counting", sheet!.SheetStatus);
    }

    [Fact]
    public async Task Close_ZeroVariance_ShouldNotCallPosting()
    {
        SeedSheet("IC2026060001");
        var item = SeedItem("IC2026060001", "Counted", bookQty: 100);
        item.FirstCountQty = 100m; // Variance = 0
        _context.SaveChanges();

        var ctrl = BuildController();
        await ctrl.Close("IC2026060001");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(
            It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── DeleteConfirmed ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteConfirmed_NoPendingItems_ShouldDelete()
    {
        SeedSheet("IC2026060001");

        var ctrl   = BuildController();
        var result = await ctrl.DeleteConfirmed("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);

        Assert.False(_context.InventoryCountSheets.Any());
    }

    [Fact]
    public async Task DeleteConfirmed_HasCountedItems_ShouldFail()
    {
        SeedSheet("IC2026060001");
        SeedItem("IC2026060001", "Counted");

        var ctrl   = BuildController();
        var result = await ctrl.DeleteConfirmed("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);
        Assert.True(_context.InventoryCountSheets.Any());
    }

    [Fact]
    public async Task DeleteConfirmed_ClosedSheet_ShouldFail()
    {
        SeedSheet("IC2026060001", "Closed");

        var ctrl   = BuildController();
        var result = await ctrl.DeleteConfirmed("IC2026060001");
        var json   = Assert.IsType<JsonResult>(result);
        dynamic? val = json.Value;
        Assert.False((bool)val!.GetType().GetProperty("success")!.GetValue(val)!);
    }

    // ── GenerateItemsInternal：倉庫範圍過濾 ──────────────────────────────

    [Fact]
    public async Task Create_WithSheet_WhRange_ShouldFilterByWarehouse()
    {
        var ctrl  = BuildController();
        var model = new InventoryCountSheet
        {
            CountMode  = "WithSheet",
            WhCodeFrom = "WH01",
            WhCodeTo   = "WH01", // 僅取 WH01（A-01）
        };
        await ctrl.Create(model);

        var sheet = await _context.InventoryCountSheets
            .Include(s => s.Items)
            .FirstAsync();
        Assert.Single(sheet.Items);
        Assert.Equal("WH01", sheet.Items.First().WhCode);
    }

    [Fact]
    public async Task Create_WithSheet_ProdRange_ShouldFilterByProduct()
    {
        var ctrl  = BuildController();
        var model = new InventoryCountSheet
        {
            CountMode    = "WithSheet",
            ProdCodeFrom = "MAT002",
        };
        await ctrl.Create(model);

        var sheet = await _context.InventoryCountSheets
            .Include(s => s.Items)
            .FirstAsync();
        Assert.Single(sheet.Items);
        Assert.Equal("MAT002", sheet.Items.First().ProductCode);
    }
}
