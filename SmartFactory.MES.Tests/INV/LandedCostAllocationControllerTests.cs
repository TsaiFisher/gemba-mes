using Microsoft.AspNetCore.Mvc;
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

/// <summary>LandedCostAllocationController 生命週期測試：Index / Save / Submit / Withdraw / Confirm / Delete。</summary>
public class LandedCostAllocationControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser     = new();
    private readonly Mock<IPermissionService> _mockPerm     = new();
    private readonly Mock<IDateTimeService>   _mockDt       = new();
    private readonly Mock<IApprovalService>   _mockApproval = new();
    private readonly Mock<IDocNumberService>  _mockDocNo    = new();
    private readonly Mock<ILandedCostService> _mockLanded   = new();
    private readonly Mock<ICurrencyService>   _mockCurrency;
    private readonly Mock<IExportService>     _mockExport   = new();

    private const string PlantId = "P001";
    private const string UserId  = "user1";
    private const string DocId   = "LCA-2606-00001";

    public LandedCostAllocationControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DocId);

        _mockCurrency = GetMockCurrencyService();
    }

    private LandedCostAllocationController BuildController()
    {
        var ctrl = new LandedCostAllocationController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockLanded.Object,
            _mockCurrency.Object, _mockExport.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
        return ctrl;
    }

    private LandedCostAllocation SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr)
    {
        var doc = new LandedCostAllocation
        {
            LandedCostAllocationId = id,
            DocDate = new DateOnly(2026, 6, 11),
            Basis = LandedCostBasis.Amount,
            TotalLandedCost = 1000m,
            CurrencyCode = "TWD",
            CreatedBy = UserId,
            PlantId = PlantId,
            Status = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new LandedCostAllocationLine
        {
            LandedCostAllocationId = id, LineNo = 1,
            GoodsReceiptId = "PE2026000001", GoodsReceiptLineNo = 1,
            ProductCode = "MAT001", LocationCode = "A-01", LotNo = "",
            ReceiptQty = 100m, PurchaseAmount = 1000m, Weight = 0m, OriginalUnitCost = 10m,
        });
        _context.LandedCostAllocations.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private LandedCostAllocationFormVm BuildVm(string? id = null) => new()
    {
        LandedCostAllocationId = id,
        DocDate = new DateOnly(2026, 6, 11),
        Basis = LandedCostBasis.Amount,
        TotalLandedCost = 1000m,
        CurrencyCode = "TWD",
        CreatedBy = UserId,
        Lines = new List<LandedCostAllocationLineVm>
        {
            new() { GoodsReceiptId = "PE2026000001", GoodsReceiptLineNo = 1, ProductCode = "MAT001",
                    LocationCode = "A-01", ReceiptQty = 100m, PurchaseAmount = 1000m, Weight = 0m, OriginalUnitCost = 10m },
        }
    };

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<LandedCostAllocation>>(view.Model);
        Assert.Single(model);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_New_NoApprovalConfig_ShouldCreate_AutoApproved()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("toastSuccess", content.Content);
        Assert.Single(_context.LandedCostAllocations);
        Assert.Equal(DocumentApprovalStatus.Approved, _context.LandedCostAllocations.First().ApprovalStatus);
    }

    [Fact]
    public async Task Save_New_WithApprovalConfig_ShouldCreate_DraftApproval()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DocumentApprovalConfig { DocumentType = "LandedCostAllocation", DocumentTypeName = "進料費用分攤", ApprovalTemplateId = "TPL1" });

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm());

        Assert.IsType<ContentResult>(result);
        Assert.Equal(DocumentApprovalStatus.Draft, _context.LandedCostAllocations.First().ApprovalStatus);
    }

    [Fact]
    public async Task Save_NoLines_ShouldRejectWithError()
    {
        var ctrl = BuildController();
        var vm = BuildVm();
        vm.Lines.Clear();

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
        Assert.Empty(_context.LandedCostAllocations);
    }

    [Fact]
    public async Task Save_Edit_PendingApproval_ShouldRejectWithError()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var vm = BuildVm(DocId);
        vm.ApprovalStatus = DocumentApprovalStatus.PendingApproval;

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
    }

    // ── Submit / Withdraw ──────────────────────────────────────────────────

    [Fact]
    public async Task Submit_DraftDoc_ShouldSetPendingApproval()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.Draft);
        _mockApproval.Setup(a => a.GetApprovalConfig("LandedCostAllocation", PlantId))
            .ReturnsAsync(new DocumentApprovalConfig { ApprovalTemplateId = "TPL1" });
        _mockApproval.Setup(a => a.SubmitForApproval(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ApprovalRequest { Id = "REQ1" });

        var ctrl = BuildController();
        var result = await ctrl.Submit(DocId);

        Assert.IsType<OkObjectResult>(result);
        var doc = _context.LandedCostAllocations.Find(DocId);
        Assert.Equal(DocumentApprovalStatus.PendingApproval, doc!.ApprovalStatus);
        Assert.Equal("REQ1", doc.ApprovalRequestId);
    }

    [Fact]
    public async Task Withdraw_PendingDoc_ShouldSetDraft()
    {
        var doc = SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        doc.ApprovalRequestId = "REQ1";
        _context.SaveChanges();
        _mockApproval.Setup(a => a.WithdrawRequest("REQ1", UserId)).ReturnsAsync(true);

        var ctrl = BuildController();
        var result = await ctrl.Withdraw(DocId);

        Assert.IsType<OkObjectResult>(result);
        var updated = _context.LandedCostAllocations.Find(DocId);
        Assert.Equal(DocumentApprovalStatus.Draft, updated!.ApprovalStatus);
        Assert.Null(updated.ApprovalRequestId);
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_Approved_ShouldCallService()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockLanded.Setup(s => s.ConfirmAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LandedCostAllocation { LandedCostAllocationId = DocId });

        var ctrl = BuildController();
        var result = await ctrl.Confirm(DocId);

        Assert.IsType<OkObjectResult>(result);
        _mockLanded.Verify(s => s.ConfirmAsync(DocId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm(DocId);
        Assert.IsType<BadRequestObjectResult>(result);
        _mockLanded.Verify(s => s.ConfirmAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_NotDraft_ShouldReject()
    {
        SeedDoc(DocId, DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm(DocId);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Delete(DocId);

        Assert.IsType<JsonResult>(result);
        Assert.False(_context.LandedCostAllocations.Any());
    }

    [Fact]
    public async Task Delete_Confirmed_ShouldReject()
    {
        SeedDoc(DocId, DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Delete(DocId);

        Assert.IsType<JsonResult>(result);
        Assert.True(_context.LandedCostAllocations.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc(DocId, DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete(DocId);

        Assert.IsType<JsonResult>(result);
        Assert.True(_context.LandedCostAllocations.Any());
    }
}
