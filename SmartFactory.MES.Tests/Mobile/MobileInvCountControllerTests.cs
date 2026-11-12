using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Api.Mobile;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Shared;
using Xunit;

namespace SmartFactory.MES.Tests.Mobile;

/// <summary>
/// MobileInvController 庫存盤點端點測試：依清冊初盤、自由盤點自動找/建 WithoutSheet 清冊、
/// 複盤職責分離（複盤人 ≠ 初盤人）。
/// </summary>
public class MobileInvCountControllerTests : TestBase
{
    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private const string User2Id = "user2";

    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<IMobilePermissionService> _mockMobilePerm = new();
    private readonly Mock<IIqcInspectionService> _mockIqc = new();
    private readonly Mock<IDocNumberService> _mockDocNo = new();

    private int _seq = 1;

    public MobileInvCountControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockUser.Setup(u => u.ActiveRoleId).Returns("role1");

        _mockMobilePerm.Setup(m => m.CanExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"IC2026070{_seq++:D3}");

        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "", QtyOnHand = 100
        });
        _context.SaveChanges();
    }

    private MobileInvController BuildController(string userId = UserId)
    {
        _mockUser.Setup(u => u.UserId).Returns(userId);
        return new MobileInvController(_context, _mockUser.Object, _mockIqc.Object, _mockMobilePerm.Object, _mockDocNo.Object);
    }

    private InventoryCountSheet SeedWithSheetSheet(string id, params (string product, string loc, decimal book)[] items)
    {
        var sheet = new InventoryCountSheet
        {
            SheetId = id, PlantId = PlantId, CountMode = "WithSheet", SheetStatus = "Counting",
            CreatedBy = UserId, SnapshotAt = DateTime.UtcNow,
        };
        int seq = 0;
        foreach (var (product, loc, book) in items)
        {
            seq++;
            sheet.Items.Add(new InventoryCountItem
            {
                SheetId = id, SeqNo = seq, WhCode = "WH01", LocationCode = loc,
                LotNo = "", ProductCode = product, BookQty = book, ItemStatus = "Pending",
            });
        }
        _context.InventoryCountSheets.Add(sheet);
        _context.SaveChanges();
        return sheet;
    }

    // ── 依清冊：first-count 帶 ItemId ────────────────────────────────────────

    [Fact]
    public async Task FirstCount_WithItemId_UpdatesExistingItem()
    {
        var sheet = SeedWithSheetSheet("IC2026070001", ("MAT001", "A-01", 100m));
        var itemId = sheet.Items.First().ItemId;

        var ctrl = BuildController();
        var result = await ctrl.SubmitFirstCount(new CountSubmitRequest(
            new List<CountLineInput> { new(itemId, null, null, null, null, 95m) }));

        Assert.IsType<OkObjectResult>(result);
        var item = await _context.InventoryCountItems.FirstAsync(i => i.ItemId == itemId);
        Assert.Equal(95m, item.FirstCountQty);
        Assert.Equal("Counted", item.ItemStatus);
        Assert.Equal(UserId, item.FirstCountedBy);
    }

    [Fact]
    public async Task FirstCount_WithItemId_AfterRecount_RejectedAndRecountKept()
    {
        // 離線佇列晚到的初盤（或重送）不得把已覆盤的明細寫回 Counted
        var sheet = SeedWithSheetSheet("IC2026070009", ("MAT001", "A-01", 100m));
        var item = sheet.Items.First();
        item.FirstCountQty = 95m; item.FirstCountedBy = UserId;
        item.RecountQty = 96m; item.RecountedBy = User2Id; item.ItemStatus = "Recounted";
        await _context.SaveChangesAsync();

        var ctrl = BuildController();
        var result = await ctrl.SubmitFirstCount(new CountSubmitRequest(
            new List<CountLineInput> { new(item.ItemId, null, null, null, null, 80m) }));

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<MobileOkResult>(ok.Value);
        var lines = Assert.IsAssignableFrom<System.Collections.Generic.List<CountLineResult>>(body.Data);
        Assert.False(lines.Single().Success);

        var unchanged = await _context.InventoryCountItems.FirstAsync(i => i.ItemId == item.ItemId);
        Assert.Equal("Recounted", unchanged.ItemStatus);
        Assert.Equal(95m, unchanged.FirstCountQty);
        Assert.Equal(96m, unchanged.RecountQty);
    }

    // ── 自由盤點：不帶 ItemId，自動建立 WithoutSheet 清冊 ────────────────────

    [Fact]
    public async Task FirstCount_WithoutItemId_AutoCreatesWithoutSheetSheet()
    {
        var ctrl = BuildController();
        var result = await ctrl.SubmitFirstCount(new CountSubmitRequest(
            new List<CountLineInput> { new(null, "MAT001", "A-01", "WH01", null, 90m) }));

        Assert.IsType<OkObjectResult>(result);
        var sheet = await _context.InventoryCountSheets
            .Include(s => s.Items)
            .SingleAsync(s => s.CountMode == "WithoutSheet");

        Assert.Equal(PlantId, sheet.PlantId);
        Assert.Equal(UserId, sheet.CreatedBy);
        Assert.Equal("Counting", sheet.SheetStatus);
        var item = Assert.Single(sheet.Items);
        Assert.Equal("MAT001", item.ProductCode);
        Assert.Equal(90m, item.FirstCountQty);
        Assert.Equal(100m, item.BookQty); // 依目前庫存快照
    }

    [Fact]
    public async Task FirstCount_WithoutItemId_ReusesOpenWithoutSheetForSameUser()
    {
        var ctrl = BuildController();

        await ctrl.SubmitFirstCount(new CountSubmitRequest(
            new List<CountLineInput> { new(null, "MAT001", "A-01", "WH01", null, 90m) }));
        await ctrl.SubmitFirstCount(new CountSubmitRequest(
            new List<CountLineInput> { new(null, "MAT001", "B-02", "WH01", null, 10m) }));

        var sheets = await _context.InventoryCountSheets
            .Where(s => s.CountMode == "WithoutSheet").ToListAsync();
        Assert.Single(sheets); // 同一使用者、同一廠別 → 併入同一張清冊，而非各建一張

        var items = await _context.InventoryCountItems
            .Where(i => i.SheetId == sheets[0].SheetId).ToListAsync();
        Assert.Equal(2, items.Count);
    }

    // ── 離線重送去重（clientId）───────────────────────────────────────────────
    // 盤點原本不能離線排隊：自由盤點每送一次就多建一筆明細。每次呼叫前 Clear 追蹤器
    // ＝模擬新的請求（正式環境每個請求一個 DbContext）。

    [Fact]
    public async Task FirstCount_FreeMode_SameClientIdTwice_CreatesOneItem()
    {
        CountSubmitRequest Req() => new(
            new List<CountLineInput> { new(null, "MAT001", "A-01", "WH01", null, 90m) }, "c-count-1", DateTime.UtcNow);

        await BuildController().SubmitFirstCount(Req());
        _context.ChangeTracker.Clear();
        var replay = await BuildController().SubmitFirstCount(Req());

        Assert.IsType<OkObjectResult>(replay);
        _context.ChangeTracker.Clear();
        Assert.Single(await _context.InventoryCountItems.ToListAsync());
    }

    [Fact]
    public async Task FirstCount_Replay_ReturnsFirstAttemptFailedLines_InCamelCase()
    {
        // 前端佇列靠 data[].success === false 判斷部分失敗、顯示黃字。
        // 快照若用預設序列化（PascalCase），重放出來是 "Success"，前端就看不到失敗。
        var sheet = SeedWithSheetSheet("IC2026070010", ("MAT001", "A-01", 100m));
        var item = sheet.Items.First();
        item.FirstCountQty = 95m; item.FirstCountedBy = UserId;
        item.RecountQty = 96m; item.RecountedBy = User2Id; item.ItemStatus = "Recounted";
        await _context.SaveChangesAsync();
        CountSubmitRequest Req() => new(
            new List<CountLineInput> { new(item.ItemId, null, null, null, null, 80m) }, "c-count-2");

        await BuildController().SubmitFirstCount(Req());
        _context.ChangeTracker.Clear();
        var replay = Assert.IsType<OkObjectResult>(await BuildController().SubmitFirstCount(Req()));

        var json = System.Text.Json.JsonSerializer.Serialize(replay.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"success\":false", json);
        PzAssert.ContainsMessage("Mob_LineRecountedLocked", System.Text.RegularExpressions.Regex.Unescape(json));
    }

    [Fact]
    public async Task FirstCount_ManyFailures_SnapshotFitsColumnAndBatchStillSaves()
    {
        // SyncLog.ResponseSnapshot 上限 2000。整批結果全存的話 30 行就爆欄寬，
        // 在 PostgreSQL 上連業務資料一起存不進去（InMemory 不檢查長度，故這裡直接量快照）。
        var lines = Enumerable.Range(1, 80)
            .Select(i => new CountLineInput(900000 + i, null, null, null, null, 1m)).ToList();
        lines.Add(new CountLineInput(null, "MAT001", "A-01", "WH01", null, 90m));

        var result = await BuildController().SubmitFirstCount(new CountSubmitRequest(lines, "c-count-3"));

        Assert.IsType<OkObjectResult>(result);
        var log = await _context.SyncLogs.SingleAsync(l => l.ClientId == "c-count-3");
        Assert.NotNull(log.ResponseSnapshot);
        Assert.True(log.ResponseSnapshot!.Length <= 2000, $"快照 {log.ResponseSnapshot.Length} 字元，超過欄寬 2000");
        Assert.Single(await _context.InventoryCountItems.ToListAsync());   // 成功那一行照常存下
    }

    [Fact]
    public async Task Recount_SameClientIdTwice_ReplaysInsteadOfRejecting()
    {
        // 沒有去重時，第二次到達會因狀態已是 Recounted 回「此明細目前無法複盤」——
        // 佇列會把一筆其實成功的複盤顯示成部分失敗。
        var sheet = SeedWithSheetSheet("IC2026070011", ("MAT001", "A-01", 100m));
        var item = sheet.Items.First();
        item.FirstCountQty = 95m; item.FirstCountedBy = UserId; item.ItemStatus = "Counted";
        await _context.SaveChangesAsync();
        CountSubmitRequest Req() => new(
            new List<CountLineInput> { new(item.ItemId, null, null, null, null, 96m) }, "c-recount-1");

        await BuildController(User2Id).SubmitRecount(Req());
        _context.ChangeTracker.Clear();
        var replay = Assert.IsType<OkObjectResult>(await BuildController(User2Id).SubmitRecount(Req()));

        var body = Assert.IsType<MobileOkResult>(replay.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(body.Data);
        Assert.DoesNotContain("false", json);   // 快照只存失敗明細，這批沒有
    }

    // ── 複盤：職責分離 ───────────────────────────────────────────────────────

    [Fact]
    public async Task Recount_DifferentUser_Succeeds()
    {
        var sheet = SeedWithSheetSheet("IC2026070002", ("MAT001", "A-01", 100m));
        var item = sheet.Items.First();
        item.FirstCountQty = 95m; item.FirstCountedBy = UserId; item.ItemStatus = "Counted";
        await _context.SaveChangesAsync();

        var ctrl = BuildController(User2Id);
        var result = await ctrl.SubmitRecount(new CountSubmitRequest(
            new List<CountLineInput> { new(item.ItemId, null, null, null, null, 96m) }));

        Assert.IsType<OkObjectResult>(result);
        var updated = await _context.InventoryCountItems.FirstAsync(i => i.ItemId == item.ItemId);
        Assert.Equal(96m, updated.RecountQty);
        Assert.Equal("Recounted", updated.ItemStatus);
        Assert.Equal(User2Id, updated.RecountedBy);
    }

    [Fact]
    public async Task Recount_SameUserAsFirstCount_Rejected()
    {
        var sheet = SeedWithSheetSheet("IC2026070003", ("MAT001", "A-01", 100m));
        var item = sheet.Items.First();
        item.FirstCountQty = 95m; item.FirstCountedBy = UserId; item.ItemStatus = "Counted";
        await _context.SaveChangesAsync();

        var ctrl = BuildController(UserId); // 同一人
        var result = await ctrl.SubmitRecount(new CountSubmitRequest(
            new List<CountLineInput> { new(item.ItemId, null, null, null, null, 96m) }));

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<MobileOkResult>(ok.Value);
        var lines = Assert.IsAssignableFrom<System.Collections.Generic.List<CountLineResult>>(body.Data);
        Assert.False(lines.Single().Success);

        var unchanged = await _context.InventoryCountItems.FirstAsync(i => i.ItemId == item.ItemId);
        Assert.Null(unchanged.RecountQty);
        Assert.Equal("Counted", unchanged.ItemStatus);
    }

    [Fact]
    public async Task GetCountSheets_ReturnsOnlyWithSheetCounting()
    {
        SeedWithSheetSheet("IC2026070004", ("MAT001", "A-01", 100m));
        var closed = new InventoryCountSheet
        {
            SheetId = "IC2026070005", PlantId = PlantId, CountMode = "WithSheet",
            SheetStatus = "Closed", CreatedBy = UserId, SnapshotAt = DateTime.UtcNow,
        };
        _context.InventoryCountSheets.Add(closed);
        await _context.SaveChangesAsync();

        var ctrl = BuildController();
        var result = await ctrl.GetCountSheets();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<MobileOkResult>(ok.Value);
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(body.Data).Cast<object>().ToList();
        Assert.Single(list);
    }

    [Fact]
    public async Task GetCountSheets_全部盤完的清冊不列出()
    {
        // 2026-10-05 手機實測：已盤完的清冊仍出現在「選擇盤點清冊」，選了只會看到空清單。
        SeedWithSheetSheet("IC2026070006", ("MAT001", "A-01", 100m));
        var done = SeedWithSheetSheet("IC2026070007", ("MAT002", "A-02", 50m));
        foreach (var i in _context.InventoryCountItems.Where(i => i.SheetId == done.SheetId))
            i.ItemStatus = "Counted";
        await _context.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await BuildController().GetCountSheets());
        var body = Assert.IsType<MobileOkResult>(ok.Value);
        var ids = Assert.IsAssignableFrom<System.Collections.IEnumerable>(body.Data).Cast<object>()
            .Select(o => (string)o.GetType().GetProperty("SheetId")!.GetValue(o)!).ToList();
        Assert.Equal(new[] { "IC2026070006" }, ids);
    }
}
