using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Controllers;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.INV;

public class CrossPlantTransferControllerTests : TestBase
{
    private readonly Mock<IUserContext>              _mockUser     = new();
    private readonly Mock<IPermissionService>        _mockPerm     = new();
    private readonly Mock<IDateTimeService>          _mockDt       = new();
    private readonly Mock<IApprovalService>          _mockApproval = new();
    private readonly Mock<IDocNumberService>         _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService>  _mockPosting  = new();
    private readonly Mock<IUomService>               _mockUom      = new();
    private readonly Mock<ICurrencyService>          _mockCurrency;

    private const string PlantId   = "P001";
    private const string ToPlantId = "P002";
    private const string UserId    = "user1";

    public CrossPlantTransferControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("XF2026051401");

        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal qty, CancellationToken __) => qty);

        _mockPosting.Setup(p => p.PostCrossPlantShipAsync(
                It.IsAny<PostCrossPlantShipRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostCrossPlantShipRequest, CancellationToken>((req, _) =>
            {
                foreach (var l in req.Lines) l.SetQtyShipped(l.StockQty);
                req.SetDocInTransit();
            })
            .Returns(Task.CompletedTask);

        _mockPosting.Setup(p => p.PostCrossPlantReceiveAsync(
                It.IsAny<PostCrossPlantReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostCrossPlantReceiveRequest, CancellationToken>((req, _) =>
            {
                foreach (var l in req.Lines) l.SetQtyReceived(l.StockQty);
                req.SetDocPosted();
            })
            .Returns(Task.CompletedTask);

        _mockPosting.Setup(p => p.VoidByMovementsAsync(
                It.IsAny<IReadOnlyList<StockMovement>>(), It.IsAny<VoidByMovementsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<StockMovement>, VoidByMovementsRequest, CancellationToken>((_, req, _) =>
                req.SetDocVoid?.Invoke())
            .Returns(Task.CompletedTask);

        _mockCurrency = GetMockCurrencyService();
    }

    private CrossPlantTransferController BuildController()
    {
        var ctrl = new CrossPlantTransferController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        // Details 走 CanReadDocumentAsync（ModuleControllerBase → RequestServices），需含服務的 context
        ctrl.ControllerContext = GetControllerContextWithServices(userId: UserId);
        return ctrl;
    }

    private void SetAjax(CrossPlantTransferController ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private CrossPlantTransfer SeedDoc(string id, DocStatus status = DocStatus.Draft,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var doc = new CrossPlantTransfer
        {
            CrossPlantTransferId = id,
            TransferDate         = new DateOnly(2026, 5, 14),
            CreatedBy            = UserId,
            PlantId              = PlantId,
            ToPlantId            = ToPlantId,
            FromWhCode           = "WH01",
            ToWhCode             = "WH02",
            Status               = status,
            ApprovalStatus       = appr,
        };
        doc.Lines.Add(new CrossPlantTransferLine
        {
            CrossPlantTransferId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = 10, UomCode = "PC",
            StockQty = 10, StockUomCode = "PC",
            FromWhCode = "WH01", ToWhCode = "WH02",
        });
        _context.CrossPlantTransfers.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        SeedDoc("XF2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<CrossPlantTransfer>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Create GET ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_ReturnsPartialView()
    {
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Create();
        Assert.IsType<PartialViewResult>(result);
    }

    // ── Edit GET ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsPartialView()
    {
        SeedDoc("XF2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("XF2026051401");
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Details GET ───────────────────────────────────────────────────────

    [Fact]
    public async Task Details_GET_SetsIsReadOnlyTrue()
    {
        SeedDoc("XF2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("XF2026051401");
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.True((bool)view.ViewData["IsReadOnly"]!);
    }

    [Fact]
    public async Task Details_GET_NotFound_Returns404()
    {
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Save POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_POST_NewRecord_ReturnsContent()
    {
        var ctrl = BuildController();
        var vm = new CrossPlantTransferFormVm
        {
            TransferDate = new DateOnly(2026, 5, 14),
            ToPlantId    = ToPlantId,
            Lines        = new List<CrossPlantTransferLineVm>
            {
                new() { LineNo = 1, ProductCode = "MAT001", Qty = 5, UomCode = "PC" }
            }
        };
        var result = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_POST_SameSourceAndDest_ReturnsFormWithError()
    {
        var ctrl = BuildController();
        var vm = new CrossPlantTransferFormVm
        {
            TransferDate = new DateOnly(2026, 5, 14),
            ToPlantId    = PlantId, // same as source
            Lines        = new List<CrossPlantTransferLineVm>()
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    [Fact]
    public async Task Save_POST_PendingApproval_ReturnsFormWithError()
    {
        var ctrl = BuildController();
        var vm = new CrossPlantTransferFormVm
        {
            CrossPlantTransferId = "XF2026051401",
            ApprovalStatus       = DocumentApprovalStatus.PendingApproval,
            TransferDate         = new DateOnly(2026, 5, 14),
            ToPlantId            = ToPlantId,
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedDoc("XF2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Submit("XF2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.CrossPlantTransfers.IgnoreQueryFilters().First(d => d.CrossPlantTransferId == "XF2026051401");
        Assert.Equal(DocumentApprovalStatus.Approved, doc.ApprovalStatus);
    }

    [Fact]
    public async Task Submit_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Submit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Submit_POST_AlreadyPending_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("XF2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Withdraw POST ─────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Withdraw_POST_NotPending_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("XF2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Confirm POST ──────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Confirm("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_NotDraft_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", status: DocStatus.InTransit, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("XF2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_NotApproved_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("XF2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_ApprovedDraft_SetsInTransit()
    {
        SeedDoc("XF2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("XF2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.CrossPlantTransfers.IgnoreQueryFilters().First(d => d.CrossPlantTransferId == "XF2026051401");
        Assert.Equal(DocStatus.InTransit, doc.Status);
    }

    // ── Receive POST ──────────────────────────────────────────────────────

    [Fact]
    public async Task Receive_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Receive("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Receive_POST_NotInTransit_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        _mockUser.Setup(u => u.PlantId).Returns(ToPlantId);
        var ctrl = BuildController();
        var result = await ctrl.Receive("XF2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Void POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_POST_NotPosted_ReturnsBadRequest()
    {
        SeedDoc("XF2026051401", status: DocStatus.InTransit, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Void("XF2026051401", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Void_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Void("NONEXISTENT", null);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_DraftRecord_ReturnsJsonSuccess()
    {
        SeedDoc("XF2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Delete("XF2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.CrossPlantTransfers.IgnoreQueryFilters().Any());
    }

    [Fact]
    public async Task Delete_POST_PendingApproval_ReturnsJsonError()
    {
        SeedDoc("XF2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("XF2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Delete_POST_NotDraft_ReturnsJsonError()
    {
        SeedDoc("XF2026051401", status: DocStatus.InTransit, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Delete("XF2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }
}
