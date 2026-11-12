using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// GoodsReceiptController 流程測試：Confirm / CancelConfirm / Void（完整模式 + 簡化模式）。
/// </summary>
public class GoodsReceiptFlowControllerTests : TestBase
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
    private readonly Mock<IUomService>        _mockUom      = new();
    private readonly Mock<ICurrencyService>   _mockCurrency;
    private readonly Mock<IAPInvoiceService>  _mockApInvoice = new();
    private readonly PurchaseService         _purchaseService;
    private readonly InventoryService        _invSvc;
    private readonly InventoryPostingService _postingSvc;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public GoodsReceiptFlowControllerTests()
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
        // 未設 RoundQtyAsync 會回 0，使 CreateGoodsReceiptAsync 存下的 ReceiptQty=0；原樣回傳。
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal q, CancellationToken __) => q);

        // 認列改逐次累加後，簡化模式完成會呼叫 AccrueForReceivedQtyAsync；設定回傳避免 NRE。
        _mockApInvoice.Setup(a => a.AccrueForReceivedQtyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartFactory.MES.Modules.FIN.Models.APInvoice());

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
            PurchaseUom = "PC", StockUom = "PC",
            CategoryCode = "CAT1",
        });
        _context.Products.Add(new Product
        {
            ProductCode = "MAT002", ProductName = "無類別料",
            PurchaseUom = "PC", StockUom = "PC",
        });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Companies.Add(new Company { CompanyCode = "SUP001", CompanyName = "供應商", IsSupplier = true });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "原料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "A-01" });

        _context.SaveChanges();
    }

    private GoodsReceiptController BuildController()
    {
        var ctrl = new GoodsReceiptController(
            _context, _purchaseService, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockApproval.Object,
            _mockSysParam.Object, _postingSvc, _mockUom.Object, _mockCurrency.Object,
            _mockApInvoice.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private GoodsReceipt SeedReceipt(string id, DocStatus status, DocumentApprovalStatus appr,
        string productCode = "MAT001", string? locationCode = "A-01", decimal qty = 100, decimal putAway = 0)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = id,
            ReceiptDate = new DateOnly(2025, 4, 1),
            CreatedBy = UserId,
            SupplierCode = "SUP001",
            Status = status,
            ApprovalStatus = appr,
            PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = id, LineNo = 1,
            ProductCode = productCode,
            ReceiptQty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 10, WhCode = "WH01", LocationCode = locationCode,
            QtyPutAway = putAway,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
        return gr;
    }

    // ── Confirm ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_CompleteMode_ShouldSetConfirmed()
    {
        SeedReceipt("PE2025000001", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000001");

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts.FindAsync("PE2025000001");
        Assert.Equal(DocStatus.Confirmed, gr!.Status);
        Assert.Equal(0m, gr.Lines.First().QtyPutAway);
        // AP 認列時點重構後：進貨單「完成」不再整批產生應付發票（GenerateFromGoodsReceiptAsync）。
        // 完整模式完成後貨僅為 Confirmed、尚待上架 → 完成當下不得認列，待進正常倉才逐次累加。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_Draft_SimplifiedMode_ShouldPostAndUpdateInventory()
    {
        SeedReceipt("PE2025000002", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000002");

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts
            .Include(g => g.Lines)
            .FirstAsync(g => g.GoodsReceiptId == "PE2025000002");
        Assert.Equal(DocStatus.Posted, gr.Status);
        Assert.Equal(100m, gr.Lines.First().QtyPutAway);
        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));

        var sm = await _context.StockMovements.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.DocNo == "PE2025000002");
        Assert.NotNull(sm);
        Assert.Equal(1, sm!.Direction);
        // 簡化模式：完成即過帳進正常倉 → 就地按明細收貨量認列（一明細一次）。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync("PE2025000002", 1, 100m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_FallbackToCategoryDefaultLocation()
    {
        // Line 未指定 LocationCode，應 fallback 取 ProductCategory.DefaultLocationCode
        SeedReceipt("PE2025000003", DocStatus.Draft, DocumentApprovalStatus.Approved,
            productCode: "MAT001", locationCode: null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000003");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(100m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_NoLocationAndNoCategoryDefault_ShouldFail()
    {
        SeedReceipt("PE2025000004", DocStatus.Draft, DocumentApprovalStatus.Approved,
            productCode: "MAT002", locationCode: null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000004");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        PzAssert.ContainsMessage("Pur_LineNoLocationFmt", bad.Value!.ToString()!);
    }

    [Fact]
    public async Task Confirm_NotDraft_ShouldReject()
    {
        SeedReceipt("PE2025000005", DocStatus.Confirmed, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000005");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedReceipt("PE2025000006", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("PE2025000006");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Save 自動核准（無簽核流程）也必須產生應付發票 ─────────────────────
    // 回歸：不需審核的進貨單存檔即完成（Confirmed/Posted），過去只有手動 Confirm
    // 會產生 AP 發票，導致這類單永遠不出現在應付帳款。

    private GoodsReceiptFormVm BuildSaveVm(decimal qty = 5m) => new()
    {
        SupplierCode = "SUP001",
        ReceiptDate  = new DateOnly(2025, 4, 1),
        CreatedBy    = UserId,
        Lines =
        {
            new GoodsReceiptLineVm
            {
                LineNo = 1, ProductCode = "MAT001",
                ReceiptQty = qty, UomCode = "PC",
                StockQty = qty, StockUomCode = "PC",
                UnitPrice = 10, WhCode = "WH01", LocationCode = "A-01",
            },
        },
    };

    [Fact]
    public async Task Save_NoWorkflow_CompleteMode_ShouldGenerateApInvoice()
    {
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");
        _mockDocNo.Setup(d => d.NextAsync("GoodsReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PE-SAVE-0001");

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildSaveVm());

        Assert.IsType<ContentResult>(result);
        var gr = await _context.GoodsReceipts.FirstAsync(g => g.SupplierCode == "SUP001");
        Assert.Equal(DocStatus.Confirmed, gr.Status);
        Assert.Equal(DocumentApprovalStatus.Approved, gr.ApprovalStatus);
        // 完整模式：存檔完成後仍為 Confirmed、貨待上架 → 完成當下不認列。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_NoWorkflow_SimplifiedMode_ShouldGenerateApInvoice()
    {
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDocNo.Setup(d => d.NextAsync("GoodsReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PE-SAVE-0002");

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildSaveVm());

        Assert.IsType<ContentResult>(result);
        var gr = await _context.GoodsReceipts.Include(g => g.Lines)
            .FirstAsync(g => g.SupplierCode == "SUP001");
        Assert.Equal(DocStatus.Posted, gr.Status);
        // 簡化模式：存檔即過帳進正常倉 → 就地按明細收貨量認列（BuildSaveVm 一明細、量 5）。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(gr.GoodsReceiptId, 1, 5m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_RequiresApproval_ShouldStayDraft_NoApInvoice()
    {
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(DocumentApprovalStatus.Draft);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");
        _mockDocNo.Setup(d => d.NextAsync("GoodsReceipt", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PE-SAVE-0003");

        var ctrl = BuildController();
        await ctrl.Save(BuildSaveVm());

        var gr = await _context.GoodsReceipts.FirstAsync(g => g.SupplierCode == "SUP001");
        Assert.Equal(DocStatus.Draft, gr.Status);
        // 尚需送審核准，存檔階段不得產生應付發票
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 簽核核准（第三條完成路徑）也必須產生應付發票 ────────────────────────
    // 回歸：走簽核流程的進貨單，外部核准後由 Edit/Details 的 sync-on-read
    // （SyncApprovalStatusAsync → OnGrApprovedAsync → RunGrCompletionHooksAsync）補完成，
    // 這條路徑同樣得產 AP 發票，否則核准即完成的單不會出現在應付帳款。

    private void SeedApprovedRequestFor(string grId, string reqId)
    {
        _context.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = reqId,
            DocumentId = grId,
            DocumentType = "GoodsReceipt",
            Status = ApprovalStatus.Approved,
            PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Edit_ApprovalApprovedSync_CompleteMode_ShouldGenerateApInvoice()
    {
        var gr = SeedReceipt("PE-APPR-0001", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        gr.ApprovalRequestId = "REQ-APPR-1";
        _context.SaveChanges();
        SeedApprovedRequestFor("PE-APPR-0001", "REQ-APPR-1");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        // sync-on-read 走 ModuleControllerBase.Db（經 RequestServices 解析同一個 _context）；
        // 以 AJAX 標頭讓 Edit 回 PartialView（避開非 AJAX 分支的 IUrlHelperFactory 依賴）。
        ctrl.ControllerContext = GetControllerContextWithServices(userId: UserId);
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        await ctrl.Edit("PE-APPR-0001");

        var reloaded = await _context.GoodsReceipts.FindAsync("PE-APPR-0001");
        Assert.Equal(DocumentApprovalStatus.Approved, reloaded!.ApprovalStatus);
        Assert.Equal(DocStatus.Confirmed, reloaded.Status);
        // 簽核核准同步走完整模式（Confirmed）→ 完成當下不認列，待進正常倉才逐次累加。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Edit_ApprovalApprovedSync_SimplifiedMode_ShouldGenerateApInvoice()
    {
        var gr = SeedReceipt("PE-APPR-0002", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        gr.ApprovalRequestId = "REQ-APPR-2";
        _context.SaveChanges();
        SeedApprovedRequestFor("PE-APPR-0002", "REQ-APPR-2");
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        ctrl.ControllerContext = GetControllerContextWithServices(userId: UserId);
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        await ctrl.Edit("PE-APPR-0002");

        var reloaded = await _context.GoodsReceipts.FindAsync("PE-APPR-0002");
        Assert.Equal(DocumentApprovalStatus.Approved, reloaded!.ApprovalStatus);
        Assert.Equal(DocStatus.Posted, reloaded.Status);
        // 簽核核准同步走簡化模式（Posted）→ 就地按明細收貨量認列（SeedReceipt 一明細、量 100）。
        _mockApInvoice.Verify(a => a.GenerateFromGoodsReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApInvoice.Verify(a => a.AccrueForReceivedQtyAsync("PE-APPR-0002", 1, 100m, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CancelConfirm ────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_NoWarehouseWork_ShouldRevertToDraft()
    {
        SeedReceipt("PE2025000010", DocStatus.Confirmed, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("PE2025000010");

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts.FindAsync("PE2025000010");
        Assert.Equal(DocStatus.Draft, gr!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldReject()
    {
        SeedReceipt("PE2025000011", DocStatus.Confirmed, DocumentApprovalStatus.Approved, putAway: 30);

        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("PE2025000011");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CancelConfirm_NotConfirmed_ShouldReject()
    {
        SeedReceipt("PE2025000012", DocStatus.Draft, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("PE2025000012");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Void ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldReverseMovements()
    {
        // 模擬已過帳：庫存 + StockMovement
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
        SeedReceipt("PE2025000020", DocStatus.Posted, DocumentApprovalStatus.Approved, putAway: 100);

        var ctrl = BuildController();
        var result = await ctrl.Void("PE2025000020", "測試作廢");

        Assert.IsType<OkObjectResult>(result);
        var gr = await _context.GoodsReceipts.FindAsync("PE2025000020");
        Assert.Equal(DocStatus.Void, gr!.Status);
        Assert.Equal(0m, await _invSvc.GetQtyOnHandAsync(PlantId, "MAT001", "A-01"));
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedReceipt("PE2025000021", DocStatus.Confirmed, DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var result = await ctrl.Void("PE2025000021", null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Void_Posted_ShouldRestorePoReceivedQty()
    {
        // §3.9 修正點：作廢須回沖 PO 明細累計進貨量（否則額度永久占用、稽核判漂移）
        _context.PurchaseOrders.Add(new PurchaseOrder
        { PurchaseOrderId = "PO001", SupplierCode = "SUP001", PlantId = PlantId });
        _context.PurchaseOrderLines.Add(new PurchaseOrderLine
        { PurchaseOrderId = "PO001", LineNo = 1, ProductCode = "MAT001", OrderQty = 100, ReceivedQty = 100 });
        _context.Inventories.Add(new Inventory
        {
            PlantId = PlantId, ProductCode = "MAT001",
            LocationCode = "A-01", LotNo = "",
            QtyOnHand = 100, UnitCost = 10,
        });
        _context.StockMovements.Add(new StockMovement
        {
            PlantId = PlantId, TypeCode = "PI", DocNo = "PE2025000022", DocLineNo = 1,
            MovementDate = new DateOnly(2025, 4, 1),
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            UomCode = "PC", Qty = 100, StockUomCode = "PC", StockQty = 100,
            Direction = 1, UnitCost = 10, TotalCost = 1000,
        });
        var gr = SeedReceipt("PE2025000022", DocStatus.Posted, DocumentApprovalStatus.Approved, putAway: 100);
        gr.Lines.First().PurchaseOrderId = "PO001";
        gr.Lines.First().PoLineNo = 1;
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Void("PE2025000022", "測試作廢");

        Assert.IsType<OkObjectResult>(result);
        var poLine = await _context.PurchaseOrderLines.FindAsync("PO001", 1);
        Assert.Equal(0m, poLine!.ReceivedQty);
    }
}
