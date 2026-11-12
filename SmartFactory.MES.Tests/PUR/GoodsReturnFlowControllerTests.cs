using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// GoodsReturnController 流程測試：Confirm / CancelConfirm / Void（完整模式 + 簡化模式）。
/// 退貨為出庫方向（Direction = -1），需先有庫存才能過帳。
/// </summary>
public class GoodsReturnFlowControllerTests : TestBase
{
    private readonly Mock<IDocNumberService>  _mockDocNo = new();
    private readonly Mock<IDateTimeService>   _mockDt    = new();
    private readonly Mock<IUserContext>       _mockUser  = new();
    private readonly Mock<IPermissionService> _mockPerm  = new();
    private readonly Mock<IApprovalService>   _mockApproval = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    // InventoryPostingService 讀的是 inv_default_cost_method——那個參數裁決為「不做廠別化」，
    // 仍走全域的 ISystemParamService，所以這裡要兩個不同的替身，不能共用一個。
    private readonly Mock<ISystemParamService> _mockGlobalParam = new();
    private readonly Mock<IInventoryCostLayerService> _mockCostSvc = new();
    private readonly Mock<IUomService>       _mockUom = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private readonly PurchaseService         _purchaseService;
    private readonly InventoryService        _invSvc;
    private readonly InventoryPostingService _postingSvc;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public GoodsReturnFlowControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string p, string f, string t, decimal q, CancellationToken ct) => q);

        _mockCurrency = GetMockCurrencyService();
        _purchaseService = new PurchaseService(_context, _mockDocNo.Object, _mockDt.Object, _mockUom.Object, _mockCurrency.Object, new SmartFactory.MES.Modules.INV.Services.InventoryReservationService(_context));
        _invSvc = new InventoryService(_context);
        _postingSvc = new InventoryPostingService(_context, _invSvc, _mockCostSvc.Object, _mockGlobalParam.Object, _mockDt.Object);

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT1", CategoryName = "原料類",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "A-01",
        });
        _context.Products.Add(new Product
        {
            ProductCode = "MAT001", ProductName = "測試料",
            PurchaseUom = "PC", StockUom = "PC", CategoryCode = "CAT1",
        });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Companies.Add(new Company { CompanyCode = "SUP001", CompanyName = "供應商", IsSupplier = true });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01" });

        _context.SaveChanges();
    }

    private void SeedInventory(decimal qtyOnHand, decimal unitCost = 10)
    {
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = qtyOnHand, UnitCost = unitCost,
        });
        _context.SaveChanges();
    }

    private GoodsReturnController BuildController(
        SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService? apInvoice = null)
    {
        var ctrl = new GoodsReturnController(
            _context, _purchaseService, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockApproval.Object,
            _mockSysParam.Object, _postingSvc, _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object, apInvoice);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_ReversesApForSourceReceipt()
    {
        // 進貨退出簡化模式完成 → 就退貨量負向回沖來源進貨單的應付（守住「退貨→降應付」連線）。
        SeedInventory(100);
        var grt = SeedReturn("PF-REV-01", DocStatus.Draft, DocumentApprovalStatus.Approved, qty: 12);
        var line = grt.Lines.First();
        line.GoodsReceiptId = "PE-SRC-01"; line.ReceiptLineNo = 1;   // 連結來源 GR 明細
        _context.SaveChanges();
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var mockApInvoice = new Mock<SmartFactory.MES.Modules.FIN.Services.IAPInvoiceService>();
        mockApInvoice.Setup(a => a.ReverseForReturnedQtyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFactory.MES.Modules.FIN.Services.ReturnReversalResult.NoInvoice);
        var ctrl = BuildController(mockApInvoice.Object);

        var result = await ctrl.Confirm("PF-REV-01");

        Assert.IsType<OkObjectResult>(result);
        mockApInvoice.Verify(a => a.ReverseForReturnedQtyAsync("PE-SRC-01", 1, 12m, It.IsAny<CancellationToken>()), Times.Once);
    }

    private GoodsReturn SeedReturn(string id, DocStatus status, DocumentApprovalStatus appr,
        decimal qty = 30, decimal putAway = 0, string? locationCode = "A-01")
    {
        var grt = new GoodsReturn
        {
            GoodsReturnId = id,
            ReturnDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId,
            SupplierCode = "SUP001",
            Status = status,
            ApprovalStatus = appr,
            PlantId = PlantId,
        };
        grt.Lines.Add(new GoodsReturnLine
        {
            GoodsReturnId = id, LineNo = 1,
            ProductCode = "MAT001",
            ReturnQty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 10, WhCode = "WH01", LocationCode = locationCode,
            QtyPutAway = putAway,
        });
        _context.GoodsReturns.Add(grt);
        _context.SaveChanges();
        return grt;
    }

    // ── Confirm ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_CompleteMode_ShouldSetConfirmed_NoInventoryChange()
    {
        SeedInventory(100);
        SeedReturn("PF2025000001", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PF2025000001");

        Assert.IsType<OkObjectResult>(result);
        var grt = await _context.GoodsReturns.FindAsync("PF2025000001");
        Assert.Equal(DocStatus.Confirmed, grt!.Status);
        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_ShouldDecrementInventoryAndPost()
    {
        SeedInventory(100);
        SeedReturn("PF2025000002", DocStatus.Draft, DocumentApprovalStatus.Approved, qty: 30);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PF2025000002");

        Assert.IsType<OkObjectResult>(result);
        var grt = await _context.GoodsReturns
            .Include(g => g.Lines)
            .FirstAsync(g => g.GoodsReturnId == "PF2025000002");
        Assert.Equal(DocStatus.Posted, grt.Status);
        Assert.Equal(70m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
        Assert.Equal(30m, grt.Lines.First().QtyPutAway);

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.DocNo == "PF2025000002");
        Assert.Equal(-1, sm!.Direction);
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_InsufficientInventory_ShouldReject()
    {
        SeedInventory(10);
        SeedReturn("PF2025000003", DocStatus.Draft, DocumentApprovalStatus.Approved, qty: 30);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PF2025000003");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedInventory(100);
        SeedReturn("PF2025000004", DocStatus.Draft, DocumentApprovalStatus.Draft);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PF2025000004");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── CancelConfirm ────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_NoWarehouseWork_ShouldRevertToDraft()
    {
        SeedReturn("PF2025000010", DocStatus.Confirmed, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("PF2025000010");

        Assert.IsType<OkObjectResult>(result);
        var grt = await _context.GoodsReturns.FindAsync("PF2025000010");
        Assert.Equal(DocStatus.Draft, grt!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldReject()
    {
        SeedReturn("PF2025000011", DocStatus.Confirmed, DocumentApprovalStatus.Approved, putAway: 10);

        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("PF2025000011");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Void ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldRestoreInventory()
    {
        // 退貨已過帳：原始狀態出庫 30（已扣庫存），Void 會反向入庫 30
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 70, UnitCost = 10,
        });
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, TypeCode = "PF", DocNo = "PF2025000020", DocLineNo = 1,
            MovementDate = new DateOnly(2025, 4, 1),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 30, StockUomCode = "PC", StockQty = 30,
            Direction = -1, UnitCost = 10, TotalCost = 300,
        });
        SeedReturn("PF2025000020", DocStatus.Posted, DocumentApprovalStatus.Approved, qty: 30, putAway: 30);

        var ctrl = BuildController();
        var result = await ctrl.Void("PF2025000020", "測試作廢");

        Assert.IsType<OkObjectResult>(result);
        var grt = await _context.GoodsReturns.FindAsync("PF2025000020");
        Assert.Equal(DocStatus.Void, grt!.Status);
        // 庫存恢復：70 + 30 = 100
        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedReturn("PF2025000021", DocStatus.Confirmed, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.Void("PF2025000021", null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Void_Posted_ShouldRestoreGrReturnedQty()
    {
        // §3.9 修正點：退貨單作廢須回沖 GR 明細累計退貨量（否則 §3.1 有效收料被永久低估）
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = "PE2025000030", SupplierCode = "SUP001",
            Status = DocStatus.Posted, PlantId = PlantId,
            ReceiptDate = new DateOnly(2025, 3, 1), CreatedBy = UserId,
        });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "PE2025000030", LineNo = 1, ProductCode = "MAT001",
            ReceiptQty = 100, StockQty = 100, ReturnedQty = 30,
        });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 70, UnitCost = 10,
        });
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, TypeCode = "PF", DocNo = "PF2025000022", DocLineNo = 1,
            MovementDate = new DateOnly(2025, 4, 1),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 30, StockUomCode = "PC", StockQty = 30,
            Direction = -1, UnitCost = 10, TotalCost = 300,
        });
        var grt = SeedReturn("PF2025000022", DocStatus.Posted, DocumentApprovalStatus.Approved, qty: 30, putAway: 30);
        grt.Lines.First().GoodsReceiptId = "PE2025000030";
        grt.Lines.First().ReceiptLineNo = 1;
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Void("PF2025000022", "測試作廢");

        Assert.IsType<OkObjectResult>(result);
        var grLine = await _context.GoodsReceiptLines.FindAsync("PE2025000030", 1);
        Assert.Equal(0m, grLine!.ReturnedQty);
    }
}
