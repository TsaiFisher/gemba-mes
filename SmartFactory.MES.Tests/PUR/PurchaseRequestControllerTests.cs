using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.PUR;

public class PurchaseRequestControllerTests : TestBase
{
    private readonly Mock<IPurchaseService>   _mockPurchaseSvc = new();
    private readonly Mock<IUserContext>       _mockUser        = new();
    private readonly Mock<IPermissionService> _mockPerm        = new();
    private readonly Mock<IDateTimeService>   _mockDt          = new();
    private readonly Mock<IApprovalService>   _mockApproval    = new();
    private readonly Mock<IUomService>        _mockUom         = new();
    private readonly PurchaseRequestController _controller;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public PurchaseRequestControllerTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2025, 1, 1));

        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());

        _controller = new PurchaseRequestController(
            _context,
            _mockPurchaseSvc.Object,
            _mockUser.Object,
            _mockPerm.Object,
            _mockDt.Object,
            _mockApproval.Object,
            _mockUom.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);

        _controller.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private PurchaseRequest SeedPurchaseRequest(string id = "PR2025000001",
        DocumentApprovalStatus status = DocumentApprovalStatus.Draft)
    {
        var pr = new PurchaseRequest
        {
            PurchaseRequestId = id,
            RequestDate       = new DateOnly(2025, 1, 1),
            RequestedBy       = UserId,
            PlantId           = PlantId,
            ApprovalStatus    = status,
        };
        pr.Lines.Add(new PurchaseRequestLine
        {
            PurchaseRequestId = id, LineNo = 1,
            ProductCode = "P001", RequestQty = 10, UomCode = "PC",
        });
        _context.PurchaseRequests.Add(pr);
        _context.SaveChanges();
        return pr;
    }

    // ── Create GET ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_ReturnsPartialView()
    {
        SetAjax();
        var result = await _controller.Create();
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Create_GET_SetsUomDecimalsViewBag()
    {
        SetAjax();
        var result = await _controller.Create();

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.NotNull(view.ViewData["UomDecimals"]);
    }

    // ── Edit GET ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsPartialView()
    {
        SetAjax();
        SeedPurchaseRequest("PR2025000001");

        var result = await _controller.Edit("PR2025000001");
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        SetAjax();
        var result = await _controller.Edit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_GET_LoadsLinesIntoViewModel()
    {
        SetAjax();
        SeedPurchaseRequest("PR2025000001");

        var result = await _controller.Edit("PR2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        var vm   = Assert.IsType<PurchaseRequestFormVm>(view.Model);
        Assert.Single(vm.Lines);
        Assert.Equal("P001", vm.Lines[0].ProductCode);
    }

    // ── Details GET ───────────────────────────────────────────────────────

    [Fact]
    public async Task Details_GET_SetsIsReadOnlyTrue()
    {
        SetAjax();
        SeedPurchaseRequest("PR2025000001");

        var result = await _controller.Details("PR2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.True((bool)view.ViewData["IsReadOnly"]!);
    }

    [Fact]
    public async Task Details_GET_NotFound_Returns404()
    {
        SetAjax();
        var result = await _controller.Details("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Save POST (create) ────────────────────────────────────────────────

    [Fact]
    public async Task Save_POST_NewRecord_CallsCreateAndReturnsContent()
    {
        _mockPurchaseSvc
            .Setup(s => s.CreatePurchaseRequestAsync(
                It.IsAny<PurchaseRequestFormVm>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PurchaseRequest
            {
                PurchaseRequestId = "PR2025000001",
                PlantId           = PlantId,
            });

        var vm = new PurchaseRequestFormVm
        {
            RequestDate = new DateOnly(2025, 1, 1),
            RequestedBy = UserId,
            Lines = new List<PurchaseRequestLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", RequestQty = 5, UomCode = "PC" }
            }
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        _mockPurchaseSvc.Verify(s => s.CreatePurchaseRequestAsync(
            It.IsAny<PurchaseRequestFormVm>(), UserId, PlantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_POST_PendingApproval_ReturnsFormWithError()
    {
        // 狀態必須真的種進資料庫：守衛讀的是 DB 而非 vm 帶回的 hidden 欄位
        // （vm 那個值是使用者可改的，採信它等於守衛可被偽造的 POST 繞過）。
        SeedPurchaseRequest("PR2025000001", DocumentApprovalStatus.PendingApproval);

        var vm = new PurchaseRequestFormVm
        {
            PurchaseRequestId = "PR2025000001",
            ApprovalStatus    = DocumentApprovalStatus.PendingApproval,
            RequestDate       = new DateOnly(2025, 1, 1),
        };

        var result = await _controller.Save(vm);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    [Fact]
    public async Task Save_POST_ServiceThrows_ReturnsFormWithError()
    {
        _mockPurchaseSvc
            .Setup(s => s.CreatePurchaseRequestAsync(
                It.IsAny<PurchaseRequestFormVm>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("品號不存在"));

        var vm = new PurchaseRequestFormVm
        {
            RequestDate = new DateOnly(2025, 1, 1),
            Lines       = new List<PurchaseRequestLineVm>
            {
                new() { ProductCode = "UNKNOWN", RequestQty = 1 }
            }
        };

        var result = await _controller.Save(vm);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(_controller.ModelState.IsValid);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedPurchaseRequest("PR2025000001");

        var result = await _controller.Submit("PR2025000001");

        var ok   = Assert.IsType<OkObjectResult>(result);
        var pr   = _context.PurchaseRequests.Find("PR2025000001")!;
        Assert.Equal(DocumentApprovalStatus.Approved, pr.ApprovalStatus);
    }

    [Fact]
    public async Task Submit_POST_NotFound_Returns404()
    {
        var result = await _controller.Submit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Submit_POST_AlreadyPending_ReturnsBadRequest()
    {
        SeedPurchaseRequest("PR2025000001", DocumentApprovalStatus.PendingApproval);

        var result = await _controller.Submit("PR2025000001");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Withdraw POST ─────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_POST_NotFound_Returns404()
    {
        var result = await _controller.Withdraw("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Withdraw_POST_NotPending_ReturnsBadRequest()
    {
        SeedPurchaseRequest("PR2025000001", DocumentApprovalStatus.Draft);

        var result = await _controller.Withdraw("PR2025000001");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_ExistingRecord_ReturnsOk()
    {
        _mockPurchaseSvc
            .Setup(s => s.DeletePurchaseRequestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.Delete("PR2025000001");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Delete_POST_ServiceThrows_ReturnsBadRequest()
    {
        _mockPurchaseSvc
            .Setup(s => s.DeletePurchaseRequestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("此資料正在審核中，無法刪除。"));

        var result = await _controller.Delete("PR2025000001");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── CloseDoc（僅核准後可結案）─────────────────────────────────────────

    [Fact]
    public async Task CloseDoc_POST_NotApproved_ReturnsBadRequestAndDoesNotClose()
    {
        SeedPurchaseRequest("PR2025000001", DocumentApprovalStatus.Draft);
        var result = await _controller.CloseDoc("PR2025000001");
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.PurchaseRequests.Include(p => p.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseDoc_POST_Approved_ClosesAllLines()
    {
        SeedPurchaseRequest("PR2025000001", DocumentApprovalStatus.Approved);
        var result = await _controller.CloseDoc("PR2025000001");
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.PurchaseRequests.Include(p => p.Lines).First().Lines.Single().IsClosed);
    }
}
