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

/// <summary>ReworkMaterialController 生命週期測試：Index / Void / VoidAndReopen / Delete。</summary>
public class ReworkMaterialControllerTests : TestBase
{
    private readonly Mock<IUserContext>             _mockUser     = new();
    private readonly Mock<IPermissionService>       _mockPerm     = new();
    private readonly Mock<IDateTimeService>         _mockDt       = new();
    private readonly Mock<IApprovalService>         _mockApproval = new();
    private readonly Mock<IPlantParamService>      _mockSysParam = new();
    private readonly Mock<IDocNumberService>        _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService> _mockPosting  = new();
    private readonly Mock<IUomService>              _mockUom      = new();
    private readonly Mock<ICurrencyService>         _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public ReworkMaterialControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("RI2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "重工料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "重工倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "R-01", LocationName = "重工區" });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "重工料",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "R-01",
        });
        // RI and RR are pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private ReworkMaterialController BuildController()
    {
        var ctrl = new ReworkMaterialController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockSysParam.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private ReworkMaterialDoc SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr,
        string typeCode = "RI", decimal qty = 10)
    {
        var doc = new ReworkMaterialDoc
        {
            ReworkMaterialDocId = id,
            IssueDate           = new DateOnly(2026, 4, 19),
            TypeCode            = typeCode,
            CreatedBy           = UserId,
            PlantId             = PlantId,
            Status              = status,
            ApprovalStatus      = appr,
        };
        doc.Lines.Add(new ReworkMaterialDocLine
        {
            ReworkMaterialDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 0, WhCode = "WH01", LocationCode = "R-01",
        });
        _context.ReworkMaterialDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<ReworkMaterialDoc>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── CancelConfirm ──────────────────────────────────────────────────────
    // 取消確認＝把已確認、倉庫尚未動作的單退回草稿；倉庫一旦上架就不能退，
    // 否則庫存已異動、單據卻回到可改的草稿，兩邊對不起來。

    [Fact]
    public async Task CancelConfirm_Confirmed_NoPutAway_ShouldSetDraft()
    {
        SeedDoc("RI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("RI2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.ReworkMaterialDocs.FindAsync("RI2026041901");
        Assert.Equal(DocStatus.Draft, doc!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("RI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("RI2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.ReworkMaterialDocs.FindAsync("RI2026041901");
        Assert.Equal(DocStatus.Confirmed, doc!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("RI2026041901", status, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("RI2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.ReworkMaterialDocs.FindAsync("RI2026041901");
        Assert.Equal(status, doc!.Status);
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("RI2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();
        var result = await ctrl.Void("RI2026041901", "error entry");
        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("RI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("RI2026041901", null));
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("RI2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("RI2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("RI2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.ReworkMaterialDocs.IgnoreQueryFilters().Count());
        var newDoc = _context.ReworkMaterialDocs.IgnoreQueryFilters().First(d => d.ReworkMaterialDocId == "RI2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("RI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("RI2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("RI2026041901");
        Assert.False(_context.ReworkMaterialDocs.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("RI2026041901");
        Assert.IsType<JsonResult>(result);
        Assert.True(_context.ReworkMaterialDocs.Any());
    }

    // ── Confirm：庫存簡化模式（inv_simplified_mode）分支 ────────────────────

    [Fact]
    public async Task Confirm_SimplifiedMode_ShouldPostImmediatelyWithStockQty()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved, qty: 7m);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("RI2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("RI2026041901", captured!.DocNo);
        Assert.Equal(PlantId, captured.PlantId);
        var line = Assert.Single(captured.Lines);
        Assert.Equal("MAT001", line.ProductCode);
        Assert.Equal(7m, line.Qty);              // 過帳數量取庫存單位量
        Assert.Equal("WH01", line.WhCode);
        Assert.Equal("R-01", line.LocationCode);
    }

    [Fact]
    public async Task Confirm_FullMode_ShouldOnlyMarkConfirmedWithoutPosting()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("RI2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.ReworkMaterialDocs.Single().Status);
    }

    /// <summary>參數從未設定（乾淨部署，GetAsync 回 null）時視同完整模式，不得直接過帳。</summary>
    [Fact]
    public async Task Confirm_ParamNeverSet_ShouldBehaveAsFullMode()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var ctrl = BuildController();
        await ctrl.Confirm("RI2026041901");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.ReworkMaterialDocs.Single().Status);
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_PostingRejects_ShouldReturnBadRequestAndNotConfirm()
    {
        SeedDoc("RI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("庫存不足"));

        var ctrl = BuildController();
        var result = await ctrl.Confirm("RI2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(DocStatus.Draft, _context.ReworkMaterialDocs.Single().Status);
    }

    // ── Save：與工單發退料單的分工（2026-10-16）─────────────────────────────

    private static ReworkMaterialDocFormVm NewVm(string? workOrderCode) => new()
    {
        IssueDate = new DateOnly(2026, 4, 19), TypeCode = "RI", WorkCenterCode = "WC01",
        WorkOrderCode = workOrderCode,
        Lines = { new ReworkMaterialDocLineVm { LineNo = 1, ProductCode = "MAT001", Qty = 5, UomCode = "PC", WhCode = "WH01", LocationCode = "R-01" } },
    };

    /// <summary>
    /// 有開工單的重工要走工單發退料單（回寫工單已發／已退量、完工時結算原值與重工損失）；
    /// 這張單填了既有工單號，工單的已發量不會動、成本也被拆成兩半，所以擋下、不存檔。
    /// </summary>
    [Fact]
    public async Task Save_WorkOrderCodeOfExistingWorkOrder_ShouldRejectAndNotSave()
    {
        _context.WorkOrders.Add(new Modules.MFG.Models.WorkOrder
        {
            WorkOrderId = "WO-RW-01", ProductionBatchId = "PB01", ProductCode = "MAT001",
            RoutingId = "RT01", Type = Modules.MFG.Models.WorkOrderType.Rework, PlantId = PlantId,
        });
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Save(NewVm(" WO-RW-01 "));

        Assert.IsType<PartialViewResult>(result);
        Assert.True(ctrl.ModelState[nameof(ReworkMaterialDocFormVm.WorkOrderCode)]?.Errors.Count > 0);
        Assert.Empty(_context.ReworkMaterialDocs);
    }

    /// <summary>沒有開工單的現場重工（工單號留空或填的不是工單）照常可以存。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("現場重工-A線")]
    public async Task Save_NoWorkOrder_ShouldNotTriggerWorkOrderGuard(string? workOrderCode)
    {
        var ctrl = BuildController();
        await ctrl.Save(NewVm(workOrderCode));

        Assert.False(ctrl.ModelState.TryGetValue(nameof(ReworkMaterialDocFormVm.WorkOrderCode), out var e)
                     && e.Errors.Count > 0);
    }
}
