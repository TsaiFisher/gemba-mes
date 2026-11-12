using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES.Tests.RPS;

namespace SmartFactory.MES.Tests.INV;

/// <summary>
/// §6.1 前置作業二：INV 報表特徵測試（characterization test）。
///
/// 同 <c>OrdReportCharacterizationTests</c>：鎖住現況輸出，供 Phase 0-B 抽
/// <c>IReportTool</c> 時比對。這三支的測試覆蓋實測為 0（§15.3），而它們的輸出
/// 直接是金額與缺料判斷——算錯不會有任何跡象。
/// </summary>
public class InvReportCharacterizationTests : TestBase
{
    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();

    private const string PlantId = "P001";

    /// <summary>SlowMoving 以 <c>IDateTimeService.GetLocalDate()</c> 取今天，故可完全固定。</summary>
    private static readonly DateOnly Today = new(2026, 5, 14);

    public InvReportCharacterizationTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _user.Setup(u => u.UserId).Returns("user1");

        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);

        _dt.Setup(d => d.Now).Returns(new DateTime(2026, 5, 14, 9, 0, 0));
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Today);
    }

    // ────────────────────────────────────────────────────────────────
    // 共用建構與種子
    // ────────────────────────────────────────────────────────────────

    private InventoryValuationController NewValuation()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new InventoryValuationTool(_context),
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private StockShortageController NewShortage()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new StockShortageTool(_context, new Modules.INV.Services.InventoryReservationService(_context), _dt.Object),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private SlowMovingInventoryController NewSlowMoving()
        => new(_context, _user.Object, _perm.Object,
               GetMockExportService().Object, _dt.Object,
               new SlowMovingInventoryTool(_context, _dt.Object),
               GetMockLocalizer<ProjectZSharedResource>().Object,
               ReportHeaderLocalizer.Create())
           { ControllerContext = GetControllerContextWithServices() };

    private void AddProduct(string code, string name, decimal safetyStock = 0, bool isActive = true)
    {
        _context.Products.Add(new Product
        {
            ProductCode = code, ProductName = name,
            StockUom = "PC", SafetyStock = safetyStock, IsActive = isActive,
        });
        _context.SaveChanges();
    }

    private void AddInventory(string product, string location, decimal onHand,
                              decimal unitCost = 0, decimal reserved = 0, string lot = "")
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = product, LocationCode = location,
            LotNo = lot, QtyOnHand = onHand, QtyReserved = reserved, UnitCost = unitCost,
        });
        _context.SaveChanges();
    }

    private void AddMovement(string product, string location, DateOnly date, string lot = "")
    {
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, ProductCode = product, LocationCode = location, LotNo = lot,
            MovementDate = date, TypeCode = "RC", DocNo = $"M{date:yyyyMMdd}", DocLineNo = 1,
            UomCode = "PC", StockUomCode = "PC", Qty = 1, StockQty = 1, Direction = 1,
        });
        _context.SaveChanges();
    }

    private static T ModelOf<T>(IActionResult result) where T : class
        => Assert.IsType<T>(Assert.IsType<ViewResult>(result).Model);

    // ════════════════════════════════════════════════════════════════
    // InventoryValuation（存貨價值分析）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 評估金額 = 在庫量 × 均價；預設依品號升冪；小計是**全查詢**加總而非當頁加總。
    ///
    /// 兩個容易在重構時走樣的現況：
    ///  ① 過濾條件是 <c>QtyOnHand != 0</c>，**負庫存會被列入並讓小計變小**（不是 &gt; 0）。
    ///  ② 品號主檔查無時，品名以品號回填（左外接 + DefaultIfEmpty）。
    /// </summary>
    [Fact]
    public async Task InventoryValuation_金額計算_負庫存列入_品名回填()
    {
        AddProduct("PRD-A", "甲料");
        AddProduct("PRD-B", "乙料");
        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 5m);      //   50
        AddInventory("PRD-B", "L1", onHand: 3m,  unitCost: 2.5m);    //    7.5
        AddInventory("PRD-C", "L1", onHand: 0m,  unitCost: 99m);     // 排除（=0）
        AddInventory("PRD-D", "L1", onHand: -2m, unitCost: 10m);     //  -20（無主檔）

        var result = await NewValuation().Index(null, null, null, searched: true, pageNumber: 1, pageSize: 20);
        var view   = Assert.IsType<ViewResult>(result);
        var rows   = Assert.IsType<PaginatedList<InventoryValuationRow>>(view.Model);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "PRD-A", "PRD-B", "PRD-D" }, rows.Select(r => r.ProductCode));

        Assert.Equal(50m,   rows[0].TotalValue);
        Assert.Equal(7.5m,  rows[1].TotalValue);
        Assert.Equal(-20m,  rows[2].TotalValue);

        // 無主檔者以品號回填品名
        Assert.Equal("PRD-D", rows[2].ProductName);
        Assert.Equal("甲料",  rows[0].ProductName);

        // 小計 = 50 + 7.5 − 20
        Assert.Equal(37.5m, view.ViewData["GrandTotal"]);
    }

    /// <summary>小計取全查詢加總，不受分頁影響（每頁 1 筆時仍是三筆的總和）。</summary>
    [Fact]
    public async Task InventoryValuation_小計不受分頁影響()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 5m);
        AddInventory("PRD-B", "L1", onHand: 3m,  unitCost: 2.5m);
        AddInventory("PRD-D", "L1", onHand: -2m, unitCost: 10m);

        var view = Assert.IsType<ViewResult>(
            await NewValuation().Index(null, null, null, searched: true, pageNumber: 1, pageSize: 1));
        var rows = Assert.IsType<PaginatedList<InventoryValuationRow>>(view.Model);

        Assert.Single(rows);
        Assert.Equal(37.5m, view.ViewData["GrandTotal"]);
    }

    /// <summary>排序：value_desc 依金額降冪，product_desc 依品號降冪。</summary>
    [Fact]
    public async Task InventoryValuation_排序參數()
    {
        AddProduct("PRD-A", "甲料");
        AddProduct("PRD-B", "乙料");
        AddInventory("PRD-A", "L1", onHand: 1m,  unitCost: 1m);    // 1
        AddInventory("PRD-B", "L1", onHand: 10m, unitCost: 10m);   // 100

        var byValue = Assert.IsType<PaginatedList<InventoryValuationRow>>(
            Assert.IsType<ViewResult>(
                await NewValuation().Index(null, null, "value_desc", searched: true, 1, 20)).Model);
        Assert.Equal(new[] { "PRD-B", "PRD-A" }, byValue.Select(r => r.ProductCode));

        var byProduct = Assert.IsType<PaginatedList<InventoryValuationRow>>(
            Assert.IsType<ViewResult>(
                await NewValuation().Index(null, null, "product_desc", searched: true, 1, 20)).Model);
        Assert.Equal(new[] { "PRD-B", "PRD-A" }, byProduct.Select(r => r.ProductCode));
    }

    /// <summary>未查詢時 Model 為 null（不是空清單）——View 端據此區分「還沒查」與「查無資料」。</summary>
    [Fact]
    public async Task InventoryValuation_未查詢時Model為null()
    {
        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 5m);

        var view = Assert.IsType<ViewResult>(
            await NewValuation().Index(null, null, null, searched: false, 1, 20));

        Assert.Null(view.Model);
    }

    /// <summary>讀取閘門動詞為 <c>CanRead</c>（Phase 0-A 已統一；本報表含金額屬機密分級）。</summary>
    [Fact]
    public async Task InventoryValuation_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("InventoryValuation")).ReturnsAsync(false);

        var result = await NewValuation().Index(null, null, null, searched: true, 1, 20);

        Assert.IsType<ForbidResult>(result);
    }

    // ════════════════════════════════════════════════════════════════
    // StockShortage（缺貨分析）
    // ════════════════════════════════════════════════════════════════

    private void AddReservation(string product, decimal qty, ReservationCategory cat = ReservationCategory.OnHand,
                                string? supplyDocType = null)
    {
        _context.Set<InventoryReservation>().Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = product, ReservedQty = qty, SourceDocId = "SRC",
            Category = cat, Status = ReservationStatus.Active, SupplyDocType = supplyDocType,
            RequiredDate = DateTime.UtcNow,
        });
        _context.SaveChanges();
    }

    private void AddQuarantine(string location)
    {
        _context.Warehouses.Add(new Warehouse { WhCode = "WQ", WhName = "待驗", WarehouseType = WarehouseType.Quarantine, PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { LocationCode = location, WhCode = "WQ", LocationName = location });
        _context.SaveChanges();
    }

    private void AddPo(string product, decimal qty, DateOnly delivery, decimal received = 0)
    {
        var id = $"PO-{product}-{delivery:MMdd}";
        _context.Set<Modules.PUR.Models.PurchaseOrder>().Add(new Modules.PUR.Models.PurchaseOrder
        {
            PurchaseOrderId = id, PlantId = PlantId, SupplierCode = "S1",
            ApprovalStatus = DocumentApprovalStatus.Approved, CreatedBy = "u"
        });
        _context.Set<Modules.PUR.Models.PurchaseOrderLine>().Add(new Modules.PUR.Models.PurchaseOrderLine
        {
            PurchaseOrderId = id, LineNo = 1, ProductCode = product, OrderQty = qty, ReceivedQty = received,
            StockQty = qty, DeliveryDate = delivery
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// 目前缺口 = 安全存量 − 目前可用，且**只留 &gt; 0 者**。目前可用＝在手（跨儲位、不含待驗倉）− 有效在手預留，
    /// 與投產料閘 GetPlantAvailableQtyAsync 同口徑（2026-10-27：原本扣的是從沒被寫入的 Inventory.QtyReserved）。
    ///
    /// 關鍵現況：完全沒有庫存紀錄的品號，可用量以 0 計 → 缺口 = 全額安全存量，
    /// 也就是「從沒進過貨的料」會出現在缺貨清單上，這是刻意的。
    /// </summary>
    [Fact]
    public async Task StockShortage_缺口計算_跨儲位彙總_無庫存者以零計()
    {
        AddProduct("PRD-A", "甲料", safetyStock: 100m);
        AddProduct("PRD-B", "乙料", safetyStock: 50m);
        AddProduct("PRD-C", "丙料", safetyStock: 0m);    // 安全存量 0 → 不納入
        AddProduct("PRD-E", "戊料", safetyStock: 25m);   // 完全無庫存紀錄

        AddInventory("PRD-A", "L1", onHand: 20m);
        AddInventory("PRD-A", "L2", onHand: 10m);
        AddReservation("PRD-A", 10m);                              // 30 − 10 = 20 → 缺 80
        AddInventory("PRD-B", "L1", onHand: 60m);                  // 可用 60 → 不缺
        AddInventory("PRD-C", "L1", onHand: 1m);

        var rows = ModelOf<List<StockShortageRowVm>>(
            await NewShortage().Index(null, searched: true));

        Assert.Equal(2, rows.Count);

        Assert.Equal("PRD-A", rows[0].ProductCode);
        Assert.Equal(100m,    rows[0].SafetyStock);
        Assert.Equal(30m,     rows[0].QtyOnHand);
        Assert.Equal(10m,     rows[0].QtyReserved);
        Assert.Equal(20m,     rows[0].QtyAvailable);
        Assert.Equal(80m,     rows[0].ShortageQty);
        Assert.Equal(80m,     rows[0].ProjectedShortageQty);   // 無在途

        Assert.Equal("PRD-E", rows[1].ProductCode);
        Assert.Equal(0m,      rows[1].QtyAvailable);
        Assert.Equal(25m,     rows[1].ShortageQty);
    }

    [Fact]
    public async Task StockShortage_待驗倉不算目前可用()
    {
        AddProduct("PRD-Q", "待驗料", safetyStock: 50m);
        AddQuarantine("LQ");
        AddInventory("PRD-Q", "LQ", onHand: 100m);
        AddInventory("PRD-Q", "L1", onHand: 10m);

        var row = Assert.Single(ModelOf<List<StockShortageRowVm>>(await NewShortage().Index(null, searched: true)));

        Assert.Equal(10m, row.QtyOnHand);
        Assert.Equal(10m, row.QtyAvailable);
        Assert.Equal(40m, row.ShortageQty);
    }

    /// <summary>
    /// 在途只計截止日前到貨、未被其他需求認領（Supply peg）的量——與投產料閘同一個函式。
    /// 截止日留白＝今天起 30 天（Today＝2026-05-14 → 06-13）。
    /// </summary>
    [Fact]
    public async Task StockShortage_採購在途只計截止日前到貨且扣除已認領()
    {
        AddProduct("PRD-P", "在途料", safetyStock: 100m);
        AddPo("PRD-P", 50m, Today.AddDays(10));                  // 計入
        AddPo("PRD-P", 70m, Today.AddDays(45));                  // 預設截止日之後 → 不計
        AddReservation("PRD-P", 20m, ReservationCategory.Supply, "PurchaseOrder");   // 已被認領 20

        var row = Assert.Single(ModelOf<List<StockShortageRowVm>>(await NewShortage().Index(null, searched: true)));
        Assert.Equal(30m, row.QtyIncomingPo);                    // 50 − 20
        Assert.Equal(30m, row.QtyProjected);
        Assert.Equal(70m, row.ProjectedShortageQty);

        row = Assert.Single(ModelOf<List<StockShortageRowVm>>(
            await NewShortage().Index(null, searched: true, byDate: Today.AddDays(60).ToString("yyyy-MM-dd"))));
        Assert.Equal(100m, row.QtyIncomingPo);                   // 50 + 70 − 20
        Assert.Equal(0m,   row.ProjectedShortageQty);            // 在途補足：仍列出（目前缺），預計缺口 0
    }

    [Fact]
    public async Task StockShortage_依預計缺口降冪()
    {
        AddProduct("PRD-1", "有在途", safetyStock: 100m);
        AddProduct("PRD-2", "無在途", safetyStock: 60m);
        AddPo("PRD-1", 90m, Today.AddDays(5));

        var rows = ModelOf<List<StockShortageRowVm>>(await NewShortage().Index(null, searched: true));

        Assert.Equal(new[] { "PRD-2", "PRD-1" }, rows.Select(r => r.ProductCode));   // 預計缺口 60 > 10
    }

    /// <summary>
    /// ⚠️ 現況：<c>BuildQueryAsync</c> **不篩 <c>IsActive</c>**，
    /// 已停用但安全存量 &gt; 0 的品號照樣出現在缺貨清單（Index 的下拉選單才篩 IsActive）。
    /// 鎖住它，因為工具化時很容易「順手補上」而改變輸出。
    /// </summary>
    [Fact]
    public async Task StockShortage_停用品號仍會列出()
    {
        AddProduct("PRD-X", "已停用料", safetyStock: 40m, isActive: false);

        var rows = ModelOf<List<StockShortageRowVm>>(
            await NewShortage().Index(null, searched: true));

        Assert.Single(rows);
        Assert.Equal("PRD-X", rows[0].ProductCode);
        Assert.Equal(40m,     rows[0].ShortageQty);
    }

    /// <summary>品號關鍵字同時比對品號與品名，且為模糊比對。</summary>
    [Fact]
    public async Task StockShortage_關鍵字同時比對品號與品名()
    {
        AddProduct("PRD-A", "不鏽鋼管", safetyStock: 100m);
        AddProduct("PRD-B", "銅棒",     safetyStock: 100m);

        var byName = ModelOf<List<StockShortageRowVm>>(
            await NewShortage().Index("不鏽鋼", searched: true));
        Assert.Single(byName);
        Assert.Equal("PRD-A", byName[0].ProductCode);

        var byCode = ModelOf<List<StockShortageRowVm>>(
            await NewShortage().Index("PRD-B", searched: true));
        Assert.Single(byCode);
        Assert.Equal("PRD-B", byCode[0].ProductCode);
    }

    /// <summary>閘門動詞為 <c>CanRead</c>（2026-08-14 裁決；同 OrderOverdue 的說明）。</summary>
    [Fact]
    public async Task StockShortage_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("StockShortage")).ReturnsAsync(false);
        _perm.Setup(p => p.CanCreate("StockShortage")).ReturnsAsync(true);

        Assert.IsType<ForbidResult>(await NewShortage().Index(null, searched: true));

        _perm.Setup(p => p.CanRead("StockShortage")).ReturnsAsync(true);
        _perm.Setup(p => p.CanCreate("StockShortage")).ReturnsAsync(false);

        Assert.IsType<ViewResult>(await NewShortage().Index(null, searched: true));
    }

    // ════════════════════════════════════════════════════════════════
    // SlowMovingInventory（呆滯庫存）
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 呆滯天數 = 今天 − 該（品號, 儲位, 批號）的最後異動日；
    /// 門檻為 <c>&gt;=</c>（含當天）；依庫存金額（量 × 單位成本）降冪。
    ///
    /// 關鍵現況：**查無任何異動紀錄者，呆滯天數以哨兵值 9999 表示**（不是 null、
    /// 也不是排除），因此必定超過任何門檻而永遠上榜。Export 會把 9999 顯示成 "N/A"。
    /// </summary>
    [Fact]
    public async Task SlowMoving_呆滯天數_無異動以9999計_依金額降冪()
    {
        AddProduct("PRD-A", "甲料");
        AddProduct("PRD-B", "乙料");
        AddProduct("PRD-C", "丙料");

        AddInventory("PRD-A", "L1", onHand: 10m, unitCost: 3m);    // 金額  30
        AddInventory("PRD-B", "L1", onHand: 5m,  unitCost: 100m);  // 金額 500（但不呆滯）
        AddInventory("PRD-C", "L2", onHand: 2m,  unitCost: 50m);   // 金額 100（無異動）

        AddMovement("PRD-A", "L1", new DateOnly(2026, 1, 1));      // 距 5/14 = 133 天
        AddMovement("PRD-B", "L1", new DateOnly(2026, 5, 10));     // 距 5/14 =   4 天

        var rows = ModelOf<List<SlowMovingInventoryRowVm>>(
            await NewSlowMoving().Index(idleDays: 90, searchProduct: null, searched: true));

        Assert.Equal(2, rows.Count);

        Assert.Equal("PRD-C", rows[0].ProductCode);   // 金額 100 > 30
        Assert.Equal(9999,    rows[0].IdleDays);
        Assert.Null(rows[0].LastMovementDate);
        Assert.Equal(100m,    rows[0].InventoryValue);

        Assert.Equal("PRD-A", rows[1].ProductCode);
        Assert.Equal(133,     rows[1].IdleDays);
        Assert.Equal(new DateOnly(2026, 1, 1), rows[1].LastMovementDate);
        Assert.Equal(30m,     rows[1].InventoryValue);
    }

    /// <summary>門檻為含端點的 <c>&gt;=</c>：呆滯天數恰等於門檻時要列出。</summary>
    [Fact]
    public async Task SlowMoving_門檻含端點()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 1m, unitCost: 1m);
        AddMovement("PRD-A", "L1", Today.AddDays(-90));

        var atThreshold = ModelOf<List<SlowMovingInventoryRowVm>>(
            await NewSlowMoving().Index(idleDays: 90, searchProduct: null, searched: true));
        Assert.Single(atThreshold);
        Assert.Equal(90, atThreshold[0].IdleDays);

        var aboveThreshold = ModelOf<List<SlowMovingInventoryRowVm>>(
            await NewSlowMoving().Index(idleDays: 91, searchProduct: null, searched: true));
        Assert.Empty(aboveThreshold);
    }

    /// <summary>在手量 0（或負）者不進呆滯庫存——與 InventoryValuation 的 <c>!= 0</c> 不同，這裡是 <c>&gt; 0</c>。</summary>
    [Fact]
    public async Task SlowMoving_在手量非正數者排除()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 0m,  unitCost: 5m);
        AddInventory("PRD-A", "L2", onHand: -3m, unitCost: 5m);

        var rows = ModelOf<List<SlowMovingInventoryRowVm>>(
            await NewSlowMoving().Index(idleDays: 90, searchProduct: null, searched: true));

        Assert.Empty(rows);
    }

    /// <summary>
    /// 呆滯判定的鍵含批號：同品號同儲位、不同批號各自計算最後異動日。
    /// 批號一律以空字串代表「無批號」（見既有 E2E 種子慣例），不可用 null。
    /// </summary>
    [Fact]
    public async Task SlowMoving_批號各自計算呆滯天數()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 1m, unitCost: 10m, lot: "LOT-OLD");
        AddInventory("PRD-A", "L1", onHand: 1m, unitCost: 10m, lot: "LOT-NEW");
        AddMovement("PRD-A", "L1", new DateOnly(2026, 1, 1), lot: "LOT-OLD");
        AddMovement("PRD-A", "L1", new DateOnly(2026, 5, 10), lot: "LOT-NEW");

        var rows = ModelOf<List<SlowMovingInventoryRowVm>>(
            await NewSlowMoving().Index(idleDays: 90, searchProduct: null, searched: true));

        Assert.Single(rows);
        Assert.Equal("LOT-OLD", rows[0].LotNo);
        Assert.Equal(133,       rows[0].IdleDays);
    }

    /// <summary>未給門檻時採預設 90 天，且 ViewBag 帶回掃描上限供 View 顯示。</summary>
    [Fact]
    public async Task SlowMoving_預設門檻90天且未超出掃描上限()
    {
        AddProduct("PRD-A", "甲料");
        AddInventory("PRD-A", "L1", onHand: 1m, unitCost: 1m);
        AddMovement("PRD-A", "L1", Today.AddDays(-95));

        var view = Assert.IsType<ViewResult>(
            await NewSlowMoving().Index(idleDays: null, searchProduct: null, searched: true));
        var rows = Assert.IsType<List<SlowMovingInventoryRowVm>>(view.Model);

        Assert.Single(rows);
        Assert.Equal(90,    view.ViewData["IdleDays"]);
        Assert.Equal(false, view.ViewData["TooMany"]);
        Assert.Equal(20000, view.ViewData["ScanLimit"]);
    }

    /// <summary>讀取閘門動詞為 <c>CanRead</c>（Phase 0-A 已統一；本報表含庫存金額）。</summary>
    [Fact]
    public async Task SlowMoving_閘門動詞為CanRead()
    {
        _perm.Setup(p => p.CanRead("SlowMovingInventory")).ReturnsAsync(false);

        var result = await NewSlowMoving().Index(idleDays: null, searchProduct: null, searched: true);

        Assert.IsType<ForbidResult>(result);
    }
}
