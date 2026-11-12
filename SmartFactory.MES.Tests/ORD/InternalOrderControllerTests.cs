using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.ORD;

public class InternalOrderControllerTests : TestBase
{
    private readonly Mock<IUserContext>                  _mockUser        = new();
    private readonly Mock<IPermissionService>            _mockPerm        = new();
    private readonly Mock<IDateTimeService>              _mockDt          = new();
    private readonly Mock<IApprovalService>              _mockApproval    = new();
    private readonly Mock<IDocNumberService>             _mockDocNo       = new();
    private readonly Mock<IInventoryPostingService>      _mockPosting     = new();
    private readonly Mock<IInventoryReservationService>  _mockReservations = new();
    private readonly Mock<IUomService>                   _mockUom         = new();

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public InternalOrderControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2026, 5, 14));

        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IO2026051401");

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal qty, CancellationToken __) => qty);

        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VoidDocRequest, CancellationToken>((r, _) => r.SetDocVoid())
            .Returns(Task.CompletedTask);
    }

    private InternalOrderController BuildController()
    {
        var ctrl = new InternalOrderController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockReservations.Object, _mockUom.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object,
            // §25.5 自動展開：預設不觸發（＝簡化模式關閉），使既有斷言維持原本行為。
            // 必須明確 Setup——Moq 對 Task<record> 的預設值是 null，直接取 .Triggered 會 NRE。
            NoAutoExpand());
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        ctrl.ControllerContext.HttpContext.RequestServices = BuildServiceProvider(
            (typeof(SmartFactory.MES.Services.IPermissionService), _mockPerm.Object),
            (typeof(SmartFactory.MES.Services.IUserContext), _mockUser.Object),
            (typeof(SmartFactory.MES.Data.ApplicationDbContext), _context),
            (typeof(SmartFactory.MES.Services.IApprovalService), _mockApproval.Object),
            (typeof(Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>), GetMockLocalizer<ProjectZSharedResource>().Object));
        return ctrl;
    }

    private void SetAjax(InternalOrderController ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private InternalOrder SeedDoc(string id, DocStatus status = DocStatus.Draft,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var doc = new InternalOrder
        {
            InternalOrderId = id,
            OrderDate       = new DateOnly(2026, 5, 14),
            CreatedBy       = UserId,
            PlantId         = PlantId,
            Status          = status,
            ApprovalStatus  = appr,
        };
        doc.Lines.Add(new InternalOrderLine
        {
            InternalOrderId = id, LineNo = 1,
            ProductCode = "PRD001", OrderQty = 10, UomCode = "PC",
            WhCode = "WH01", LocationCode = "A-01",
        });
        _context.InternalOrders.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<InternalOrder>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, null, searched: false);
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
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("IO2026051401");
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Edit_GET_LoadsLines()
    {
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("IO2026051401");
        var view = Assert.IsType<PartialViewResult>(result);
        var vm   = Assert.IsType<InternalOrderFormVm>(view.Model);
        Assert.Single(vm.Lines);
        Assert.Equal("PRD001", vm.Lines[0].ProductCode);
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
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("IO2026051401");
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
        var vm = new InternalOrderFormVm
        {
            OrderDate = new DateOnly(2026, 5, 14),
            Lines     = new List<InternalOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", OrderQty = 5, UomCode = "PC",
                        WhCode = "WH01", LocationCode = "A-01" }
            }
        };
        var result = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(result);
    }

    // ── §21.7 縮量反向流：釋放殘留預留 ──────────────────────────────────────

    [Fact]
    public async Task Save_POST_ReducedQty_WithMrpLines_ReleasesReservations()
    {
        SeedDoc("IO2026051401");                       // 原 OrderQty = 10
        SeedMrpLine("MRP1", 1, "IO2026051401");
        var ctrl = BuildController();

        var vm = new InternalOrderFormVm
        {
            InternalOrderId = "IO2026051401",
            OrderDate = new DateOnly(2026, 5, 14),
            Lines = new List<InternalOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", OrderQty = 5, UomCode = "PC",
                        WhCode = "WH01", LocationCode = "A-01" }   // 縮量 10 → 5
            }
        };
        await ctrl.Save(vm);

        _mockReservations.Verify(r => r.ReleaseBySourceLineAsync(
            ReservationSourceType.MrpRunLine, "MRP1", 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_POST_IncreasedQty_WithMrpLines_DoesNotRelease()
    {
        SeedDoc("IO2026051401");                       // 原 OrderQty = 10
        SeedMrpLine("MRP1", 1, "IO2026051401");
        var ctrl = BuildController();

        var vm = new InternalOrderFormVm
        {
            InternalOrderId = "IO2026051401",
            OrderDate = new DateOnly(2026, 5, 14),
            Lines = new List<InternalOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", OrderQty = 20, UomCode = "PC",
                        WhCode = "WH01", LocationCode = "A-01" }   // 增量 10 → 20
            }
        };
        await ctrl.Save(vm);

        _mockReservations.Verify(r => r.ReleaseBySourceLineAsync(
            It.IsAny<ReservationSourceType>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SeedMrpLine(string mrpRunId, int lineSeq, string sourceOrderId)
    {
        _context.MrpRunLines.Add(new MrpRunLine
        {
            MrpRunId     = mrpRunId,
            LineSeq      = lineSeq,
            SalesOrderId = sourceOrderId,
            ProductCode  = "PRD001",
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Save_POST_PendingApproval_ReturnsFormWithError()
    {
        var ctrl = BuildController();
        var vm = new InternalOrderFormVm
        {
            InternalOrderId = "IO2026051401",
            ApprovalStatus  = DocumentApprovalStatus.PendingApproval,
            OrderDate       = new DateOnly(2026, 5, 14),
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Submit("IO2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.InternalOrders.Find("IO2026051401")!;
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
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("IO2026051401");
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
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("IO2026051401");
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
        SeedDoc("IO2026051401", status: DocStatus.Confirmed, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("IO2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_NotApproved_ReturnsBadRequest()
    {
        SeedDoc("IO2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("IO2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_ApprovedDraft_TransitionsToConfirmed_NoPosting()
    {
        // 全模式：核准後 Confirm 僅轉 Confirmed（待倉庫作業領料），不在訂單過帳發料。
        // 實際發料統一於庫存管理 → 倉庫作業（WarehouseApi pick）完成（inventory.md / mfg-domain.md）。
        SeedDoc("IO2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("IO2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.InternalOrders.Find("IO2026051401")!;
        Assert.Equal(DocStatus.Confirmed, doc.Status);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CancelConfirm POST ────────────────────────────────────────────────
    // 已確認、倉庫上架尚未開始才可退回草稿；已有倉庫上架量就擋下，狀態維持已確認。

    [Fact]
    public async Task CancelConfirm_Confirmed_NoPutAway_ShouldSetDraft()
    {
        SeedDoc("IO2026051401", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var result = await BuildController().CancelConfirm("IO2026051401");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocStatus.Draft, (await _context.InternalOrders.FindAsync("IO2026051401"))!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("IO2026051401", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var result = await BuildController().CancelConfirm("IO2026051401");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(DocStatus.Confirmed, (await _context.InternalOrders.FindAsync("IO2026051401"))!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("IO2026051401", status, DocumentApprovalStatus.Approved);
        var result = await BuildController().CancelConfirm("IO2026051401");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(status, (await _context.InternalOrders.FindAsync("IO2026051401"))!.Status);
    }

    // ── Void POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_POST_NotPosted_ReturnsBadRequest()
    {
        SeedDoc("IO2026051401", status: DocStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Void("IO2026051401", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Void_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.Void("NONEXISTENT", null);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Void_POST_PostedDoc_SetsVoidStatus()
    {
        SeedDoc("IO2026051401", status: DocStatus.Posted, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Void("IO2026051401", "test reason");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.InternalOrders.Find("IO2026051401")!;
        Assert.Equal(DocStatus.Void, doc.Status);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_DraftRecord_ReturnsJsonSuccess()
    {
        SeedDoc("IO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Delete("IO2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.InternalOrders.Any());
    }

    [Fact]
    public async Task Delete_POST_PendingApproval_ReturnsJsonError()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IO2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Delete_POST_NotDraft_ReturnsJsonError()
    {
        SeedDoc("IO2026051401", status: DocStatus.Confirmed, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IO2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Delete_POST_ReferencedByMrpRun_ReturnsJsonError()
    {
        // 已納入 MRP 執行單的內部訂單不可刪除（MrpRunLine.SalesOrderId 沿用為來源訂單號）
        SeedDoc("IO2026051401");
        _context.MrpRuns.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRun
        {
            MrpRunId = "MRP-0001", PlantId = PlantId,
            Status = SmartFactory.MES.Modules.MFG.Models.MrpRunStatus.Draft,
            Lines = { new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
            {
                MrpRunId = "MRP-0001", LineSeq = 1, SalesOrderId = "IO2026051401",
                SalesOrderLineSeq = 1, ProductCode = "PRD001", RequiredQty = 10,
                RequiredDate = DateTime.UtcNow,
            } },
        });
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Delete("IO2026051401");

        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.True(_context.InternalOrders.Any());   // 未被刪除
    }

    // ── CloseLine / ReopenLine / CloseDoc（僅核准後可結案）───────────────────

    [Fact]
    public async Task CloseLine_POST_NotApproved_ReturnsBadRequestAndDoesNotClose()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.CloseLine("IO2026051401", 1);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.InternalOrders.Include(d => d.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseLine_POST_Approved_ClosesLine()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CloseLine("IO2026051401", 1);
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.InternalOrders.Include(d => d.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseLine_POST_Voided_ReturnsBadRequest()
    {
        SeedDoc("IO2026051401", status: DocStatus.Void, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CloseLine("IO2026051401", 1);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ReopenLine_POST_NotApproved_ReturnsBadRequest()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.ReopenLine("IO2026051401", 1);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CloseDoc_POST_NotApproved_ReturnsBadRequestAndDoesNotCloseAny()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.CloseDoc("IO2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.InternalOrders.Include(d => d.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseDoc_POST_Approved_ClosesAllLines()
    {
        SeedDoc("IO2026051401", appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CloseDoc("IO2026051401");
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.InternalOrders.Include(d => d.Lines).First().Lines.Single().IsClosed);
    }
}
