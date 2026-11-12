using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.INV;

/// <summary>StockAdjustmentController 生命週期測試：Index / Void / VoidAndReopen / Delete。</summary>
public class StockAdjustmentControllerTests : TestBase
{
    private readonly Mock<IUserContext>             _mockUser     = new();
    private readonly Mock<IPermissionService>       _mockPerm     = new();
    private readonly Mock<IDateTimeService>         _mockDt       = new();
    private readonly Mock<IApprovalService>         _mockApproval = new();
    private readonly Mock<ISystemParamService>      _mockSysParam = new();
    private readonly Mock<IDocNumberService>        _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService> _mockPosting  = new();
    private readonly Mock<IUomService>              _mockUom      = new();
    private readonly Mock<ICurrencyService>         _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public StockAdjustmentControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IA2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "調整料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "調整倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "調整區" });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "調整料",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "A-01",
        });
        // IA is pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private StockAdjustmentController BuildController()
    {
        var ctrl = new StockAdjustmentController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private StockAdjustment SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr, decimal qty = 10)
    {
        var doc = new StockAdjustment
        {
            StockAdjustmentId = id,
            AdjustDate        = new DateOnly(2026, 4, 19),
            TypeCode          = "IA",
            CreatedBy         = UserId,
            PlantId           = PlantId,
            Status            = status,
            ApprovalStatus    = appr,
        };
        doc.Lines.Add(new StockAdjustmentLine
        {
            StockAdjustmentId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 0, WhCode = "WH01", LocationCode = "A-01",
        });
        _context.StockAdjustments.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("IA2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<StockAdjustment>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── CancelConfirm ──────────────────────────────────────────────────────
    // 取消確認＝把已確認、倉庫尚未動作的單退回草稿；倉庫一旦上架就不能退，
    // 否則庫存已異動、單據卻回到可改的草稿，兩邊對不起來。

    [Fact]
    public async Task CancelConfirm_Confirmed_NoPutAway_ShouldSetDraft()
    {
        SeedDoc("IA2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IA2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.StockAdjustments.FindAsync("IA2026041901");
        Assert.Equal(DocStatus.Draft, doc!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("IA2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IA2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.StockAdjustments.FindAsync("IA2026041901");
        Assert.Equal(DocStatus.Confirmed, doc!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("IA2026041901", status, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IA2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.StockAdjustments.FindAsync("IA2026041901");
        Assert.Equal(status, doc!.Status);
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("IA2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();
        var result = await ctrl.Void("IA2026041901", "error entry");
        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("IA2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("IA2026041901", null));
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("IA2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IA2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IA2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.StockAdjustments.IgnoreQueryFilters().Count());
        var newDoc = _context.StockAdjustments.IgnoreQueryFilters().First(d => d.StockAdjustmentId == "IA2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("IA2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IA2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("IA2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("IA2026041901");
        Assert.False(_context.StockAdjustments.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("IA2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IA2026041901");
        Assert.IsType<JsonResult>(result);
        Assert.True(_context.StockAdjustments.Any());
    }
}
