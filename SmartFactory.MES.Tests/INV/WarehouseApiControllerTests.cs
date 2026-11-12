using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.INV;

/// <summary>WarehouseApiController：待處理明細查詢、庫存查詢、入庫上架、出庫下架。</summary>
public class WarehouseApiControllerTests : TestBase
{
    private readonly Mock<IUserContext> _mockUser = new();
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc = new();
    private readonly Mock<ISystemParamService> _mockSysParam = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly InventoryService _invSvc;
    private readonly InventoryPostingService _postingSvc;
    private readonly Mock<IUomService> _mockUom = new();
    private readonly Mock<IAPInvoiceService> _mockAp = new();
    private readonly Mock<IARInvoiceService> _mockAr = new();
    private readonly Mock<SmartFactory.MES.Modules.ACT.Services.IJournalEntryService> _mockJournal = new();

    private const string PlantId = "P001";

    public WarehouseApiControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns("user1");

        _invSvc = new InventoryService(_context);
        var mockDt = new Mock<IDateTimeService>();
        mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 20));
        _postingSvc = new InventoryPostingService(_context, _invSvc, _mockCostSvc.Object, _mockSysParam.Object, mockDt.Object);

        // 預設允許所有操作（測試關注業務邏輯，不測試 ACL）
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);

        // Pass-through: quantity rounding returns the input unchanged
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal q, CancellationToken __) => q);

        _mockAp.Setup(a => a.AccrueForReceivedQtyAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new APInvoice());
        _mockAr.Setup(a => a.ReverseForReturnedQtyAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReturnReversalResult.NoInvoice);

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "測試料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Products.Add(new Product { ProductCode = "MAT002", ProductName = "測試料 B", StockUom = "PC", CategoryCode = "CAT01" });
        // 免驗直放品號：IqcExempt=true → 免 IQC 即可直接上架至正常倉並就上架量認列應付
        _context.Products.Add(new Product { ProductCode = "MATEXEMPT", ProductName = "免驗料", StockUom = "PC", CategoryCode = "CAT01", IqcExempt = true });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01 位" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-02", LocationName = "A-02 位" });
        // 客供倉：CI／CR 只能落在這裡（InventoryPostingService ③-b 守門）。
        // 客供料屬客戶財產，落在一般倉會被算進移動平均的分母而壓低自有料單價。
        _context.Warehouses.Add(new Warehouse
        {
            WhCode = "WHCS", WhName = "客供倉", PlantId = PlantId,
            WarehouseType = WarehouseType.CustomerSupplied,
        });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WHCS", LocationCode = "CS-01", LocationName = "CS-01 位" });
        // PI and PF are pre-seeded by HasData — no explicit Add needed
        _context.CodeEntries.Add(new CodeEntry { Category = "cost_method", Code = "MA", NameZh = "移動平均" });
        _context.CodeEntries.Add(new CodeEntry { Category = "cost_method", Code = "FIFO", NameZh = "先進先出" });
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "CAT01", CategoryName = "一般品", CostMethod = CostMethod.MovingAverage });
        _context.SaveChanges();
    }

    private WarehouseApiController BuildController()
    {
        return new WarehouseApiController(_context, _mockUser.Object, _postingSvc, _invSvc, _mockPerm.Object, _mockUom.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object, _mockAp.Object, _mockAr.Object, _mockJournal.Object);
    }

    private GoodsReceipt SeedConfirmedReceipt(string id, decimal qty = 100, decimal putAway = 0,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Approved)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = id,
            ReceiptDate = new DateOnly(2026, 4, 10),
            SupplierCode = "SUP001",
            Status = DocStatus.Confirmed,
            ApprovalStatus = appr,
            PlantId = PlantId,
            TypeCode = "PI",
            TypeCategory = "Receipt",
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = id, LineNo = 1,
            ProductCode = "MAT001",
            ReceiptQty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 10, WhCode = "WH01", LocationCode = "A-01",
            QtyPutAway = putAway,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
        return gr;
    }

    /// <summary>品質隔離閘門：登記一筆 IQC Pass 記錄，讓收貨明細可直接上架至一般倉（略過待驗倉）。</summary>
    private void SeedIqcPass(string goodsReceiptId, int lineNo, string productCode = "MAT001")
    {
        _context.MobileIqcRecords.Add(new MobileIqcRecord
        {
            GoodsReceiptId = goodsReceiptId, LineNo = lineNo, ProductCode = productCode,
            PlantId = PlantId, SampleQty = 1, PassQty = 1, FailQty = 0, Result = "Pass",
            InspectorId = "user1",
        });
        _context.SaveChanges();
    }

    /// <summary>指定品號與（可不等的）進貨/庫存量的進貨單，供免驗直放與 UOM 非 1:1 認列驗證。</summary>
    private GoodsReceipt SeedReceiptFor(string id, string productCode, decimal receiptQty, decimal stockQty,
        decimal unitPrice = 10)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = id, ReceiptDate = new DateOnly(2026, 4, 10),
            SupplierCode = "SUP001", Status = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = PlantId,
            TypeCode = "PI", TypeCategory = "Receipt",
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = id, LineNo = 1, ProductCode = productCode,
            ReceiptQty = receiptQty, UomCode = "BOX", StockQty = stockQty, StockUomCode = "PC",
            UnitPrice = unitPrice, WhCode = "WH01", LocationCode = "A-01",
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
        return gr;
    }

    /// <summary>已確認的銷貨退回單（連結來源出貨單），供 AR 折讓回沖驗證。</summary>
    private SalesReturn SeedConfirmedSalesReturn(string id, string shipmentId, decimal returnQty, decimal stockQty)
    {
        var doc = new SalesReturn
        {
            SalesReturnId = id, ReturnDate = new DateOnly(2026, 4, 10),
            TypeCode = "GR", CompanyCode = "CUS001", GoodsShipmentId = shipmentId,
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId, CreatedBy = "user1",
        };
        doc.Lines.Add(new SalesReturnLine
        {
            SalesReturnId = id, LineNo = 1, GoodsShipmentId = shipmentId, ShipmentLineNo = 1,
            ProductCode = "MAT001", ReturnQty = returnQty, UomCode = "PC",
            StockQty = stockQty, StockUomCode = "PC", UnitPrice = 20,
        });
        _context.SalesReturns.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private GoodsReturn SeedConfirmedReturn(string id, decimal qty = 30, decimal putAway = 0)
    {
        var gr = new GoodsReturn
        {
            GoodsReturnId = id,
            ReturnDate = new DateOnly(2026, 4, 10),
            SupplierCode = "SUP001",
            Status = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
            TypeCode = "PF",
            TypeCategory = "Return",
        };
        gr.Lines.Add(new GoodsReturnLine
        {
            GoodsReturnId = id, LineNo = 1,
            ProductCode = "MAT001",
            ReturnQty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 10, WhCode = "WH01", LocationCode = "A-01",
            QtyPutAway = putAway,
        });
        _context.GoodsReturns.Add(gr);
        _context.SaveChanges();
        return gr;
    }

    // ───────────────────── Query ─────────────────────

    [Fact]
    public async Task PendingInbound_ShouldReturnConfirmedWithRemaining()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100, putAway: 0);
        SeedConfirmedReceipt("PE2026041002", qty: 50,  putAway: 50); // already complete
        var ctrl = BuildController();

        var result = await ctrl.PendingInbound();
        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(list);
    }

    [Fact]
    public async Task PendingInbound_SearchByProduct_ShouldMatch()
    {
        SeedConfirmedReceipt("PE2026041001");
        var ctrl = BuildController();

        var result = await ctrl.PendingInbound(search: "MAT001");
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single((IEnumerable<object>)ok.Value!);
    }

    [Fact]
    public async Task PendingOutbound_ShouldReturnConfirmedReturns()
    {
        SeedConfirmedReturn("PF2026041001");
        var ctrl = BuildController();

        var result = await ctrl.PendingOutbound();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single((IEnumerable<object>)ok.Value!);
    }

    [Fact]
    public async Task Stock_ShouldReturnAvailableByLocation()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 50, QtyReserved = 10,
        });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-02", LotNo = "",
            QtyOnHand = 0, QtyReserved = 0,
        });
        _context.SaveChanges();
        var ctrl = BuildController();

        var result = await ctrl.Stock("MAT001");
        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(rows); // zero-qty should be excluded
    }

    [Fact]
    public async Task GetLocation_Exists_ShouldReturnWhInfo()
    {
        var ctrl = BuildController();
        var result = await ctrl.GetLocation("A-01");
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetLocation_NotFound_ShouldReturn404()
    {
        var ctrl = BuildController();
        var result = await ctrl.GetLocation("ZZZ-99");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetProduct_Exists_ShouldReturnInfo()
    {
        var ctrl = BuildController();
        var result = await ctrl.GetProduct("MAT001");
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetProduct_NotFound_ShouldReturn404()
    {
        var ctrl = BuildController();
        var result = await ctrl.GetProduct("ZZZ");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ───────────────────── Put-away ─────────────────────

    [Fact]
    public async Task PutAway_Partial_ShouldUpdateQtyPutAway_AndInventory()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        SeedIqcPass("PE2026041001", 1);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts.FindAsync("PE2026041001");
        Assert.Equal(DocStatus.Confirmed, gr!.Status);
        Assert.Equal(40m, gr.Lines.First().QtyPutAway);

        var inv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01");
        Assert.NotNull(inv);
        Assert.Equal(40m, inv!.QtyOnHand);

        Assert.Single(_context.StockMovements);
    }

    [Fact]
    public async Task PutAway_FullAmount_ShouldAutoPosted()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        SeedIqcPass("PE2026041001", 1);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 100, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts.FindAsync("PE2026041001");
        Assert.Equal(DocStatus.Posted, gr!.Status);
    }

    // ───────────────────── 離線佇列重送去重（clientId）─────────────────────
    // 佇列在「伺服端已處理、回應在路上斷線」時保留 pending，恢復連線後再送一次。
    // 沒有去重的話，剩餘量夠就上架／下架兩次。每次呼叫前 Clear 追蹤器＝模擬新的請求
    // （正式環境每個請求一個 DbContext；共用追蹤器會讓「沒存到的 SyncLog」看起來還在）。

    [Fact]
    public async Task PutAway_SameClientIdTwice_ShouldPutAwayOnce()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        SeedIqcPass("PE2026041001", 1);
        WarehouseApiController.PutAwayRequest Req() => new()
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = "",
            ClientId = "c-put-1", ClientTimestamp = DateTime.UtcNow,
        };

        Assert.IsType<OkObjectResult>(await BuildController().PutAway(Req()));
        _context.ChangeTracker.Clear();
        var replay = await BuildController().PutAway(Req());

        var ok = Assert.IsType<OkObjectResult>(replay);
        Assert.Contains("duplicate = True", ok.Value!.ToString());
        _context.ChangeTracker.Clear();
        var gr = await _context.GoodsReceipts.Include(g => g.Lines).FirstAsync(g => g.GoodsReceiptId == "PE2026041001");
        Assert.Equal(40m, gr.Lines.First().QtyPutAway);
        Assert.Equal(40m, _context.Inventories.First(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01").QtyOnHand);
        Assert.Single(_context.StockMovements);
    }

    [Fact]
    public async Task PutAway_RejectedAttempt_ShouldNotConsumeClientId()
    {
        // 驗證失敗（超量）沒有走到 SaveChanges——SyncLog 不得被存下來，
        // 否則修正後用同一個 clientId 重試會被當成「已處理」而永遠送不出去。
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        SeedIqcPass("PE2026041001", 1);
        var rejected = await BuildController().PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 500, LotNo = "",
            ClientId = "c-put-2",
        });
        Assert.IsType<BadRequestObjectResult>(rejected);
        _context.ChangeTracker.Clear();

        Assert.False(await _context.SyncLogs.AnyAsync(l => l.ClientId == "c-put-2"));
        var retried = await BuildController().PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = "",
            ClientId = "c-put-2",
        });
        Assert.IsType<OkObjectResult>(retried);
        Assert.True(await _context.SyncLogs.AnyAsync(l => l.ClientId == "c-put-2"));
    }

    [Fact]
    public async Task Pick_SameClientIdTwice_ShouldPickOnce()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 50, QtyReserved = 0, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedReturn("PF2026041001", qty: 30);
        WarehouseApiController.PickRequest Req() => new()
        {
            DocNo = "PF2026041001", LineNo = 1, ClientId = "c-pick-1",
            Picks = new List<WarehouseApiController.PickItem> { new() { LocationCode = "A-01", Qty = 10 } },
        };

        Assert.IsType<OkObjectResult>(await BuildController().Pick(Req()));
        _context.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await BuildController().Pick(Req()));

        _context.ChangeTracker.Clear();
        Assert.Equal(40m, _context.Inventories.First(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01").QtyOnHand);
        Assert.Single(_context.StockMovements);
    }

    [Fact]
    public async Task PutAway_WithoutClientId_ShouldNotWriteSyncLog()
    {
        // 線上直接送出不帶 clientId：不去重、不留 SyncLog（行為與改動前相同）
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        SeedIqcPass("PE2026041001", 1);
        await BuildController().PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = "",
        });
        Assert.Empty(_context.SyncLogs);
    }

    // ───────────────────── Put-away — 品質隔離閘門 ─────────────────────

    [Fact]
    public async Task PutAway_WithoutIqc_ToNormalWarehouse_ShouldReject()
    {
        // 尚未提交 IQC：不可直接上架至一般倉（WH01 為 WarehouseType.Raw）
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = ""
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(_context.StockMovements);
    }

    [Fact]
    public async Task PutAway_WithoutIqc_ToQuarantineWarehouse_ShouldSucceed()
    {
        // 尚未提交 IQC：允許先上架至待驗倉（WarehouseType.Quarantine）
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        await _context.SaveChangesAsync();
        SeedConfirmedReceipt("PE2026041001", qty: 100);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "QC01", LocationCode = "Q-01", Qty = 40, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        var inv = _context.Inventories.FirstOrDefault(i => i.ProductCode == "MAT001" && i.LocationCode == "Q-01");
        Assert.NotNull(inv);
        Assert.Equal(40m, inv!.QtyOnHand);
    }

    // ─────────── Put-away — 免驗直放認列 / 銷退折讓回沖（接線守門）───────────
    // 認列/回沖的金額與 GL 由 APAccrualByReceivedQtyTests / ARReversalByReturnedQtyTests
    // 以真 JournalEntryService 驗證；此處只釘 controller 是否在「貨真的進正常倉」時
    // 以正確的認列基礎量呼叫服務（mock 協作者 + Verify 互動）。

    [Fact]
    public async Task PutAway_ExemptProduct_ToNormalWarehouse_Succeeds_AndAccrues()
    {
        // 免驗品（IqcExempt）無 IQC Pass 也可直上正常倉，且就上架量認列應付。
        SeedReceiptFor("PE2026041001", "MATEXEMPT", receiptQty: 100, stockQty: 100);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 40, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);   // 閘門旁路：免驗品不被擋到待驗倉
        _mockAp.Verify(a => a.AccrueForReceivedQtyAsync("PE2026041001", 1, 40m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PutAway_ExemptProduct_NonUnitUom_AccruesInReceiptUnits()
    {
        // 進貨單位 BOX≠庫存單位 PC：ReceiptQty=10、StockQty=100（1 BOX=10 PC）。
        // 上架 50 PC → 認列基礎＝50×(10/100)=5 進貨單位（BOX）。
        SeedReceiptFor("PE2026041002", "MATEXEMPT", receiptQty: 10, stockQty: 100);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041002", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 50, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        _mockAp.Verify(a => a.AccrueForReceivedQtyAsync("PE2026041002", 1, 5m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PutAway_NonExempt_ToQuarantine_DoesNotAccrue()
    {
        // 非免驗品未 IQC → 只能進待驗倉；貨未進正常倉故不認列（待 IQC 放行時才認列）。
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        _context.SaveChanges();
        SeedConfirmedReceipt("PE2026041003", qty: 100);   // MAT001，非免驗
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041003", LineNo = 1, WhCode = "QC01", LocationCode = "Q-01", Qty = 40, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        _mockAp.Verify(a => a.AccrueForReceivedQtyAsync(It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PutAwaySalesReturn_ToNormalWarehouse_ReversesArForSourceShipment()
    {
        // 銷退貨入正常倉 → 就上架量負向回沖來源出貨單應收（依來源出貨行號對應）。
        SeedConfirmedSalesReturn("GR2026041001", shipmentId: "GS-SRC-01", returnQty: 12, stockQty: 12);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = "GR", DocNo = "GR2026041001", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 12, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        _mockAr.Verify(a => a.ReverseForReturnedQtyAsync("GS-SRC-01", 1, 12m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PutAwaySalesReturn_NonUnitUom_ReversesInSalesUnits()
    {
        // 退回銷售單位 6、庫存單位 60（1 銷售單位=10 庫存單位）；上架 60 庫存單位
        // → 回沖基礎＝60×(6/60)=6 銷售單位。
        SeedConfirmedSalesReturn("GR2026041002", shipmentId: "GS-SRC-02", returnQty: 6, stockQty: 60);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = "GR", DocNo = "GR2026041002", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 60, LotNo = ""
        });

        Assert.IsType<OkObjectResult>(result);
        _mockAr.Verify(a => a.ReverseForReturnedQtyAsync("GS-SRC-02", 1, 6m, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PutAway_ExceedRemaining_ShouldReject()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100, putAway: 80);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, WhCode = "WH01", LocationCode = "A-01", Qty = 30, LotNo = ""
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutAway_NonConfirmedStatus_ShouldReject()
    {
        var gr = SeedConfirmedReceipt("PE2026041001", qty: 100);
        gr.Status = DocStatus.Draft;
        await _context.SaveChangesAsync();
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, LocationCode = "A-01", Qty = 10,
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutAway_UnapprovedDoc_ShouldReject()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 100, appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, LocationCode = "A-01", Qty = 10,
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutAway_UnknownLocation_ShouldReject()
    {
        SeedConfirmedReceipt("PE2026041001");
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "PE2026041001", LineNo = 1, LocationCode = "ZZZ", Qty = 10,
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PutAway_DocNotFound_ShouldReturn404()
    {
        var ctrl = BuildController();
        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocNo = "NOPE", LineNo = 1, LocationCode = "A-01", Qty = 10,
        });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ───────────────────── Pick ─────────────────────

    [Fact]
    public async Task Pick_SingleLocation_ShouldUpdateAndPost()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 50, QtyReserved = 0, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedReturn("PF2026041001", qty: 30);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            DocNo = "PF2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 30 },
            },
        });

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReturns.FindAsync("PF2026041001");
        Assert.Equal(DocStatus.Posted, gr!.Status);

        var inv = _context.Inventories.First(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01");
        Assert.Equal(20m, inv.QtyOnHand);
    }

    [Fact]
    public async Task Pick_MultipleLocations_ShouldSplitPost()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 20, QtyReserved = 0, UnitCost = 10,
        });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-02", LotNo = "",
            QtyOnHand = 20, QtyReserved = 0, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedReturn("PF2026041001", qty: 30);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            DocNo = "PF2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 20 },
                new() { LocationCode = "A-02", Qty = 10 },
            },
        });

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReturns.FindAsync("PF2026041001");
        Assert.Equal(DocStatus.Posted, gr!.Status);
        Assert.Equal(2, _context.StockMovements.Count());
    }

    [Fact]
    public async Task Pick_InsufficientStock_ShouldReject()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 5, QtyReserved = 0,
        });
        _context.SaveChanges();
        SeedConfirmedReturn("PF2026041001", qty: 30);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            DocNo = "PF2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 30 },
            },
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Pick_ExceedLineRemaining_ShouldReject()
    {
        SeedConfirmedReturn("PF2026041001", qty: 30);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            DocNo = "PF2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 50 },
            },
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Pick_EmptyPicks_ShouldReject()
    {
        SeedConfirmedReturn("PF2026041001");
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            DocNo = "PF2026041001", LineNo = 1, Picks = new(),
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ───────────────────── GetOutstandingSourceLines ─────────────────────

    [Fact]
    public async Task GetOutstandingSourceLines_ConfirmedLoanDoc_ShouldBeReturned()
    {
        // IB is pre-seeded by HasData with TypeCategory="Loan"
        // Source doc must be Posted (warehouse put-away completed) to appear in clearance picker
        var doc = new LoanDoc
        {
            LoanDocId = "IB2026041901", LoanDate = new DateOnly(2026, 4, 19),
            TypeCode = "IB", EntityType = "Customer", EntityCode = "CUS001",
            CreatedBy = "user1", PlantId = PlantId,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = "IB2026041901", LineNo = 1,
            ProductCode = "MAT001", Qty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            WhCode = "WH01", LocationCode = "A-01", QtySourceCleared = 0,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.GetOutstandingSourceLines("IB");
        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Single(rows);
    }

    [Fact]
    public async Task GetOutstandingSourceLines_DraftLoanDoc_ShouldNotBeReturned()
    {
        // IB is pre-seeded by HasData with TypeCategory="Loan"
        var doc = new LoanDoc
        {
            LoanDocId = "IB2026041901", LoanDate = new DateOnly(2026, 4, 19),
            TypeCode = "IB", EntityType = "Customer", EntityCode = "CUS001",
            CreatedBy = "user1", PlantId = PlantId,
            Status = DocStatus.Draft, ApprovalStatus = DocumentApprovalStatus.Draft,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = "IB2026041901", LineNo = 1,
            ProductCode = "MAT001", Qty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            WhCode = "WH01", LocationCode = "A-01", QtySourceCleared = 0,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.GetOutstandingSourceLines("IB");
        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetOutstandingSourceLines_FullyCleared_ShouldBeExcluded()
    {
        // IB is pre-seeded by HasData with TypeCategory="Loan"
        var doc = new LoanDoc
        {
            LoanDocId = "IB2026041901", LoanDate = new DateOnly(2026, 4, 19),
            TypeCode = "IB", EntityType = "Customer", EntityCode = "CUS001",
            CreatedBy = "user1", PlantId = PlantId,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = "IB2026041901", LineNo = 1,
            ProductCode = "MAT001", Qty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            WhCode = "WH01", LocationCode = "A-01", QtySourceCleared = 10,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.GetOutstandingSourceLines("IB");
        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetOutstandingSourceLines_UnknownTypeCode_ShouldReject()
    {
        var ctrl = BuildController();
        var result = await ctrl.GetOutstandingSourceLines("ZZZ");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ───────────────────── TypeCode routing — PutAway ─────────────────────

    private LoanDoc SeedConfirmedLoanDoc(string id, string typeCode, decimal qty = 10, decimal putAway = 0)
    {
        var doc = new LoanDoc
        {
            LoanDocId    = id,
            LoanDate     = new DateOnly(2026, 4, 10),
            TypeCode     = typeCode,
            TypeCategory = "Loan",
            EntityType   = "Customer",
            EntityCode   = "CUS001",
            CreatedBy    = "user1",
            PlantId      = PlantId,
            Status       = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = id, LineNo = 1,
            ProductCode = "MAT001",
            Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            WhCode = null, QtyPutAway = putAway,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private SampleDoc SeedConfirmedSampleDoc(string id, string typeCode, decimal qty = 10, decimal putAway = 0)
    {
        var doc = new SampleDoc
        {
            SampleDocId  = id,
            SampleDate   = new DateOnly(2026, 4, 10),
            TypeCode     = typeCode,
            TypeCategory = "Sample",
            EntityType   = "Customer",
            EntityCode   = "CUS001",
            CreatedBy    = "user1",
            PlantId      = PlantId,
            Status       = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        doc.Lines.Add(new SampleDocLine
        {
            SampleDocId = id, LineNo = 1,
            ProductCode = "MAT001",
            Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            WhCode = null, QtyPutAway = putAway,
        });
        _context.SampleDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    [Theory]
    [InlineData("IB")]  // 借入 Direction=+1 → 上架增加庫存
    [InlineData("LR")]  // 借出退回 Direction=+1 → 上架增加庫存
    public async Task PutAway_LoanInTypeCode_ShouldRouteToPutAwayLoanDoc(string typeCode)
    {
        // IB/LR are Direction=+1 (fixed ITT data); no existing inventory needed
        SeedConfirmedLoanDoc("IB2026041001", typeCode, qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = typeCode, DocNo = "IB2026041001", LineNo = 1,
            LocationCode = "A-01", Qty = 5, LotNo = "",
        });

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.LoanDocs.FindAsync("IB2026041001");
        Assert.Equal(5m, doc!.Lines.First().QtyPutAway);
    }

    [Theory]
    // 儲位刻意各給各的：CI 是**客戶的料**，只能落在客供倉（③-b 守門）；
    // SR 是我們自己送出去的樣品退回來，是自有料，進一般倉。
    // 同一個 TypeCategory（Sample）裝著所有權相反的兩種東西，這裡看得最清楚。
    [InlineData("CI", "CS-01")]  // 客供進貨 Direction=+1 → 上架增加庫存
    [InlineData("SR", "A-01")]   // 樣品退回 Direction=+1 → 上架增加庫存
    public async Task PutAway_SampleInTypeCode_ShouldRouteToPutAwaySampleDoc(string typeCode, string location)
    {
        // CI/SR are Direction=+1 (fixed ITT data); no existing inventory needed
        SeedConfirmedSampleDoc("CI2026041001", typeCode, qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = typeCode, DocNo = "CI2026041001", LineNo = 1,
            LocationCode = location, Qty = 10, LotNo = "",
        });

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.SampleDocs.FindAsync("CI2026041001");
        Assert.Equal(DocStatus.Posted, doc!.Status);
    }

    [Fact]
    public async Task PutAway_LoanTypeCode_WithGoodsReceiptDocNo_ShouldReturn404()
    {
        // Regression guard: IB TypeCode routes to LoanDocs, not GoodsReceipts
        SeedConfirmedReceipt("PE2026041001", qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = "IB", DocNo = "PE2026041001", LineNo = 1,
            LocationCode = "A-01", Qty = 5, LotNo = "",
        });

        Assert.IsType<NotFoundObjectResult>(result); // not found in LoanDocs
    }

    [Fact]
    public async Task PutAway_EmptyTypeCode_ShouldFallbackToGoodsReceipt()
    {
        SeedConfirmedReceipt("PE2026041001", qty: 10);
        SeedIqcPass("PE2026041001", 1);
        var ctrl = BuildController();

        var result = await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = "", DocNo = "PE2026041001", LineNo = 1,
            LocationCode = "A-01", Qty = 5, LotNo = "",
        });

        Assert.IsType<OkObjectResult>(result);
    }

    // ───────────────────── TypeCode routing — Pick ─────────────────────

    [Theory]
    [InlineData("BR")]  // 借入退回 Direction=-1 → 下架減少庫存
    [InlineData("IL")]  // 借出 Direction=-1 → 下架減少庫存
    public async Task Pick_LoanOutTypeCode_ShouldRouteToPickLoanDoc(string typeCode)
    {
        // BR/IL are Direction=-1 (fixed ITT data); need existing inventory
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 20, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedLoanDoc("BR2026041001", typeCode, qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = typeCode, DocNo = "BR2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 10 },
            },
        });

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.LoanDocs.FindAsync("BR2026041001");
        Assert.Equal(DocStatus.Posted, doc!.Status);
    }

    [Theory]
    // 與 PutAway 同一個道理：CR 退的是**客戶的料**，從客供倉出；
    // SO 送出去的是我們自己的樣品，從一般倉出（且要認 5230 樣品費）。
    [InlineData("CR", "CS-01")]  // 客供退回 Direction=-1 → 下架減少庫存
    [InlineData("SO", "A-01")]   // 樣品出貨 Direction=-1 → 下架減少庫存
    public async Task Pick_SampleOutTypeCode_ShouldRouteToPickSampleDoc(string typeCode, string location)
    {
        // CR/SO are Direction=-1 (fixed ITT data); need existing inventory
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = location, LotNo = "",
            QtyOnHand = 20, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedSampleDoc("CR2026041001", typeCode, qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = typeCode, DocNo = "CR2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = location, Qty = 10 },
            },
        });

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.SampleDocs.FindAsync("CR2026041001");
        Assert.Equal(DocStatus.Posted, doc!.Status);
    }

    [Fact]
    public async Task Pick_LoanOutTypeCode_WithGoodsReturnDocNo_ShouldReturn404()
    {
        // Regression guard: BR TypeCode routes to LoanDocs, not GoodsReturns
        SeedConfirmedReturn("PF2026041001", qty: 10);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = "BR", DocNo = "PF2026041001", LineNo = 1, // not in LoanDocs
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 5 },
            },
        });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Pick_EmptyTypeCode_ShouldFallbackToGoodsReturn()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 50, UnitCost = 10,
        });
        _context.SaveChanges();
        SeedConfirmedReturn("PF2026041001", qty: 30);
        var ctrl = BuildController();

        var result = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = "", DocNo = "PF2026041001", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem>
            {
                new() { LocationCode = "A-01", Qty = 30 },
            },
        });

        Assert.IsType<OkObjectResult>(result);
    }

    // ── 委外收成品上架（完整模式）────────────────────────────────────────────

    /// <summary>
    /// 完整模式下確認的委外收貨要出現在待上架清單裡。
    ///
    /// 簡化模式在確認當下就過帳；完整模式的確認只把單據轉 Confirmed 並提示「等待倉庫作業」，
    /// 但倉庫作業畫面原本根本查不到它——成品於是永遠進不了庫，而委外發料早就把原料扣掉了
    /// （金額留在存貨科目），總帳與庫存明細帳從此永久相差那批料錢。
    /// </summary>
    [Fact]
    public async Task PendingInbound_IncludesConfirmedOutsourceReceipt()
    {
        SeedOutsourceReceipt("OR-1", qty: 10m, unitCost: 25m, carriedIn: 1_260m);

        var ok = Assert.IsType<OkObjectResult>(await BuildController().PendingInbound());
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value!).ToList();

        var row = Assert.Single(rows, r => Prop<string>(r, "docNo") == "OR-1");
        Assert.Equal(WarehouseApiController.OutsourceReceiptDocType, Prop<string>(row, "docType"));
        Assert.Equal(10m, Prop<decimal>(row, "pending"));
    }

    /// <summary>
    /// 上架時要把「在外原料」併入成品單位成本，且**不再產生分錄**。
    ///
    /// 成品入庫價值 = 加工費 + 在外原料（後者早就在存貨科目裡，只是換了形態）。
    /// 只認加工費的話，庫存明細帳會永久少於總帳，差額正好是被加工掉的原料，
    /// 而成品帶著低估的單位成本出貨，COGS 跟著低估。
    /// </summary>
    [Fact]
    public async Task PutAway_OutsourceReceipt_MergesCarriedInMaterialCostIntoUnitCost()
    {
        SeedOutsourceReceipt("OR-2", qty: 10m, unitCost: 25m, carriedIn: 1_260m);

        var result = await BuildController().PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocType = WarehouseApiController.OutsourceReceiptDocType,
            TypeCode = "OI", DocNo = "OR-2", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 10m,
        });

        Assert.IsType<OkObjectResult>(result);
        var mv = Assert.Single(_context.StockMovements.Where(m => m.DocNo == "OR-2").ToList());
        Assert.Equal(1, mv.Direction);
        // 加工費 25 ＋ 在外原料 1,260/10 = 126 → 151
        Assert.Equal(151m, mv.UnitCost);
        Assert.Equal(1_510m, mv.TotalCost);
    }

    /// <summary>分批上架時併入的料錢要按量攤分——一次全給會讓第一批單價暴衝、後面幾批不含料錢。</summary>
    [Fact]
    public async Task PutAway_OutsourceReceipt_ProratesCarriedInCostAcrossPartialPutAways()
    {
        SeedOutsourceReceipt("OR-3", qty: 10m, unitCost: 25m, carriedIn: 1_260m);
        var ctrl = BuildController();

        foreach (var _ in new[] { 1, 2 })
            Assert.IsType<OkObjectResult>(await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
            {
                DocType = WarehouseApiController.OutsourceReceiptDocType,
                TypeCode = "OI", DocNo = "OR-3", LineNo = 1,
                WhCode = "WH01", LocationCode = "A-01", Qty = 5m,
            }));

        var mvs = _context.StockMovements.Where(m => m.DocNo == "OR-3").ToList();
        Assert.Equal(2, mvs.Count);
        Assert.All(mvs, m => Assert.Equal(151m, m.UnitCost));   // 兩批單價相同，不是第一批吃光
    }

    /// <summary>
    /// 超耗扣款分錄（借 2152／貸 1401）在整張單上架完成時提一次。
    ///
    /// 逐行提會在分批上架時重複入帳；不提則扣款讓應付低於收貨時掛的暫估，
    /// 差額永久留在 2152，而且沒有任何一張傳票會失衡。
    /// </summary>
    [Fact]
    public async Task PutAway_OutsourceReceipt_PostsScrapPenaltyOnceWhenFullyPutAway()
    {
        SeedOutsourceReceipt("OR-4", qty: 10m, unitCost: 25m, carriedIn: 1_260m, penalty: 135m);
        var ctrl = BuildController();

        await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocType = WarehouseApiController.OutsourceReceiptDocType,
            TypeCode = "OI", DocNo = "OR-4", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 4m,
        });
        // 還沒上架完 → 不得提列
        _mockJournal.Verify(j => j.CreateAutoEntryAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateOnly>(), It.IsAny<string>(),
            It.IsAny<List<SmartFactory.MES.Modules.ACT.Services.JournalLineRequest>>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        await ctrl.PutAway(new WarehouseApiController.PutAwayRequest
        {
            DocType = WarehouseApiController.OutsourceReceiptDocType,
            TypeCode = "OI", DocNo = "OR-4", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 6m,
        });

        _mockJournal.Verify(j => j.CreateAutoEntryAsync("INV", "OR-4",
            It.IsAny<DateOnly>(), PlantId,
            It.Is<List<SmartFactory.MES.Modules.ACT.Services.JournalLineRequest>>(l =>
                l.Count == 2 && l[0].Debit == 135m && l[1].Credit == 135m),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 委外收回品也要先品檢（2026-10-12，使用者流程：收貨 → 品檢 → 入庫 → 領出）：
    /// 非免驗品號尚未 IQC 合格前不得上架到一般倉，只能先進待驗倉；比照一般進貨的品質隔離閘門。
    /// </summary>
    [Fact]
    public async Task PutAway_OutsourceReceipt_NotInspected_MustGoToQuarantineFirst()
    {
        SeedOutsourceReceipt("OR-6", qty: 10m, unitCost: 25m, carriedIn: 0m, product: "MAT001");
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        _context.SaveChanges();
        var ctrl = BuildController();
        WarehouseApiController.PutAwayRequest Req(string wh, string loc) => new()
        {
            DocType = WarehouseApiController.OutsourceReceiptDocType,
            TypeCode = "OI", DocNo = "OR-6", LineNo = 1, WhCode = wh, LocationCode = loc, Qty = 10m,
        };

        Assert.IsType<BadRequestObjectResult>(await ctrl.PutAway(Req("WH01", "A-01")));
        Assert.Empty(_context.StockMovements.Where(m => m.DocNo == "OR-6").ToList());

        Assert.IsType<OkObjectResult>(await ctrl.PutAway(Req("QC01", "Q-01")));
    }

    /// <summary>
    /// 上架進一般倉＝合格入庫：回寫委外派工完工量（IOutsourceProgressService）；進待驗倉不算（還沒驗）。
    /// </summary>
    [Fact]
    public async Task PutAway_OutsourceReceipt_NormalWarehouse_UpdatesOutsourceProgress_QuarantineDoesNot()
    {
        SeedOutsourceReceipt("OR-7", qty: 10m, unitCost: 25m, carriedIn: 0m);   // 免驗品號，可直放一般倉
        _context.Warehouses.Add(new Warehouse { WhCode = "QC01", WhName = "待驗倉", PlantId = PlantId, WarehouseType = WarehouseType.Quarantine });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "QC01", LocationCode = "Q-01", LocationName = "Q-01 位" });
        _context.SaveChanges();
        var progress = new Mock<SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService>();
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(SmartFactory.MES.Modules.MFG.Services.IOutsourceProgressService))).Returns(progress.Object);
        var ctrl = BuildController();
        ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = sp.Object } };
        WarehouseApiController.PutAwayRequest Req(string wh, string loc, decimal qty) => new()
        {
            DocType = WarehouseApiController.OutsourceReceiptDocType,
            TypeCode = "OI", DocNo = "OR-7", LineNo = 1, WhCode = wh, LocationCode = loc, Qty = qty,
        };

        Assert.IsType<OkObjectResult>(await ctrl.PutAway(Req("QC01", "Q-01", 4m)));
        progress.Verify(p => p.ApplyGoodQtyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.IsType<OkObjectResult>(await ctrl.PutAway(Req("WH01", "A-01", 6m)));
        progress.Verify(p => p.ApplyGoodQtyAsync("OR-7", 1, 6m, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 同一個 TypeCode "OI" 同時屬於一般進貨單與委外收貨，沒有 docType 就會導到錯的單別。
    /// </summary>
    [Fact]
    public async Task PutAway_WithoutDocType_RoutesOiToGoodsReceipt_NotOutsourceReceipt()
    {
        SeedOutsourceReceipt("OR-5", qty: 10m, unitCost: 25m, carriedIn: 0m);

        var result = await BuildController().PutAway(new WarehouseApiController.PutAwayRequest
        {
            TypeCode = "OI", DocNo = "OR-5", LineNo = 1,
            WhCode = "WH01", LocationCode = "A-01", Qty = 10m,
        });

        // 走進貨單 handler → 查無此單（而不是誤把委外收貨當成進貨單過帳）
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(_context.StockMovements.Where(m => m.DocNo == "OR-5").ToList());
    }

    /// <summary>匿名型別的欄位取值（PendingInbound 回的是投影，沒有型別可以繫結）。</summary>
    private static T Prop<T>(object row, string name)
        => (T)row.GetType().GetProperty(name)!.GetValue(row)!;

    // 預設用免驗品號：這幾支測的是成本攤分與分錄，不是品檢閘門（閘門另有專測，見下）
    private void SeedOutsourceReceipt(string id, decimal qty, decimal unitCost,
                                      decimal carriedIn, decimal penalty = 0m, string product = "MATEXEMPT")
    {
        _context.Set<SmartFactory.MES.Modules.MFG.Models.OutsourcePurchaseOrder>().Add(
            new SmartFactory.MES.Modules.MFG.Models.OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = id + "-OPO", SupplierCode = "SUP001",
                OrderDate = new DateOnly(2026, 5, 1), PlantId = PlantId,
            });
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = id, OutsourcePurchaseOrderId = id + "-OPO",
            SupplierCode = "SUP001", ReceiptDate = new DateOnly(2026, 5, 20),
            CreatedBy = "user1", ExchangeRate = 1m, PlantId = PlantId,
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            TypeCode = "OI", TypeCategory = "Outsource",
            Lines =
            {
                new OutsourceReceiptLine
                {
                    OutsourceReceiptId = id, LineNo = 1, OpoLineNo = 1, ProductCode = product,
                    ReceiptQty = qty, StockQty = qty, UomCode = "PC", StockUomCode = "PC",
                    UnitCost = unitCost, ExchangeRate = 1m,
                    CarriedInMaterialCost = carriedIn, ExcessScrapPenalty = penalty,
                },
            },
        });
        _context.SaveChanges();
    }

    // ── §24.2a 出貨揀貨不得拿走保留給其他需求的成品（2026-10-14 驗收補） ────────────

    [Fact]
    public async Task Pick_GoodsShipment_StockReservedForAnotherOrder_RejectedBeyondFreeQty()
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            QtyOnHand = 50, QtyReserved = 0, UnitCost = 10,
        });
        var gs = new GoodsShipment
        {
            GoodsShipmentId = "GS-RSV", ShipDate = new DateOnly(2026, 5, 20), TypeCode = "GS", CompanyCode = "CUS001",
            SalesOrderId = "SO-MINE", Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = PlantId,
        };
        gs.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = "GS-RSV", LineNo = 1, SalesOrderId = "SO-MINE", SoLineNo = 1, ProductCode = "MAT001",
            ShipQty = 30, StockQty = 30, UomCode = "PC", StockUomCode = "PC",
        });
        _context.GoodsShipments.Add(gs);
        _context.MrpRunLines.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
        {
            MrpRunId = "MRP-RSV", LineSeq = 1, SalesOrderId = "SO-OTHER", SalesOrderLineSeq = 1,
            ProductCode = "MAT001", RequiredQty = 30, RequiredDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.SaveChanges();
        var reservations = new InventoryReservationService(_context);
        await reservations.ReserveAsync(PlantId, "MAT001", 30m, ReservationSourceType.MrpRunLine, "MRP-RSV", 1,
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var ctrl = new WarehouseApiController(_context, _mockUser.Object, _postingSvc, _invSvc, _mockPerm.Object, _mockUom.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object, _mockAp.Object, _mockAr.Object, _mockJournal.Object,
            reservations: reservations);

        // 在手 50、其中 30 保留給別張訂單 → 最多出 20
        var rejected = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = "GS", DocNo = "GS-RSV", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem> { new() { LocationCode = "A-01", Qty = 21 } },
        });
        Assert.IsType<BadRequestObjectResult>(rejected);
        Assert.Equal(50m, _context.Inventories.First(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01").QtyOnHand);

        var ok = await ctrl.Pick(new WarehouseApiController.PickRequest
        {
            TypeCode = "GS", DocNo = "GS-RSV", LineNo = 1,
            Picks = new List<WarehouseApiController.PickItem> { new() { LocationCode = "A-01", Qty = 20 } },
        });
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal(30m, _context.Inventories.First(i => i.ProductCode == "MAT001" && i.LocationCode == "A-01").QtyOnHand);
    }

    // ── 選擇庫存視窗的廠級保留（驗收 X-8）──────────────────────────────────────

    private void SeedReservation(ReservationSourceType type, string docId, int? seq, decimal qty,
        string product = "MAT001", ReservationStatus status = ReservationStatus.Active,
        ReservationCategory category = ReservationCategory.OnHand)
    {
        _context.Set<InventoryReservation>().Add(new InventoryReservation
        {
            PlantId = PlantId, ProductCode = product, SourceType = type, SourceDocId = docId,
            SourceLineSeq = seq, ReservedQty = qty, Status = status, Category = category,
            RequiredDate = new DateTime(2026, 6, 1),
        });
    }

    /// <summary>
    /// 只算本品號、在手、Active、量 &gt; 0 的保留（口徑同出貨檢查）；MRP 需求列換成使用者認得的銷貨訂單號，
    /// 同一張訂單的多列合併，依數量由大到小。
    /// </summary>
    [Fact]
    public async Task StockReservations_CountsActiveOnHandOnly_AndMapsMrpLineToSalesOrder()
    {
        _context.MrpRunLines.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
            { MrpRunId = "RUN1", LineSeq = 1, SalesOrderId = "SO-001", ProductCode = "MAT001" });
        _context.MrpRunLines.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
            { MrpRunId = "RUN1", LineSeq = 2, SalesOrderId = "SO-001", ProductCode = "MAT001" });
        SeedReservation(ReservationSourceType.MrpRunLine, "RUN1", 1, 5m);
        SeedReservation(ReservationSourceType.MrpRunLine, "RUN1", 2, 3m);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-1", 1, 2m);
        // 以下都不該算進去
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-2", 1, 50m, status: ReservationStatus.Released);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-3", 1, 50m, status: ReservationStatus.Consumed);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-4", 1, 50m, category: ReservationCategory.Supply);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-5", 1, 0m);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-6", 1, 50m, product: "MAT002");
        _context.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await BuildController().StockReservations("MAT001"));
        Assert.Equal(10m, Prop<decimal>(ok.Value!, "total"));
        var sources = ((System.Collections.IEnumerable)Prop<object>(ok.Value!, "sources")).Cast<object>().ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal(("SalesOrder", "SO-001", 8m),
            (Prop<string>(sources[0], "type"), Prop<string>(sources[0], "docId"), Prop<decimal>(sources[0], "qty")));
        Assert.Equal(("WorkOrderMaterial", "WO-1", 2m),
            (Prop<string>(sources[1], "type"), Prop<string>(sources[1], "docId"), Prop<decimal>(sources[1], "qty")));
    }

    /// <summary>MRP 需求列找不到訂單號（例如預測需求）時保留原來源，不丟掉這筆保留量。</summary>
    [Fact]
    public async Task StockReservations_MrpLineWithoutSalesOrder_KeepsRunId()
    {
        _context.MrpRunLines.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
            { MrpRunId = "RUN9", LineSeq = 1, SalesOrderId = null, ProductCode = "MAT001" });
        SeedReservation(ReservationSourceType.MrpRunLine, "RUN9", 1, 4m);
        SeedReservation(ReservationSourceType.MrpRunLine, "RUN-GONE", 7, 1m);   // 需求列已不存在
        _context.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await BuildController().StockReservations("MAT001"));
        Assert.Equal(5m, Prop<decimal>(ok.Value!, "total"));
        var docIds = ((System.Collections.IEnumerable)Prop<object>(ok.Value!, "sources")).Cast<object>()
            .Select(x => (Prop<string>(x, "type"), Prop<string>(x, "docId"))).ToList();
        Assert.Equal(new[] { ("MrpRunLine", "RUN9"), ("MrpRunLine", "RUN-GONE") }, docIds);
    }

    [Fact]
    public async Task StockReservations_NoReservation_ReturnsZero()
    {
        var ok = Assert.IsType<OkObjectResult>(await BuildController().StockReservations("MAT001"));
        Assert.Equal(0m, Prop<decimal>(ok.Value!, "total"));
    }

    [Fact]
    public async Task StockReservations_NoStockLookupPermission_Forbid()
    {
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(false);
        SeedReservation(ReservationSourceType.WorkOrderMaterial, "WO-1", 1, 2m);
        _context.SaveChanges();

        Assert.IsType<ForbidResult>(await BuildController().StockReservations("MAT001"));
    }
}
