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

/// <summary>ScrapDocController 生命週期測試：Save / Confirm / CancelConfirm / Void / Delete / Submit / Withdraw。</summary>
public class ScrapDocControllerTests : TestBase
{
    private readonly Mock<IUserContext>        _mockUser     = new();
    private readonly Mock<IPermissionService>  _mockPerm     = new();
    private readonly Mock<IDateTimeService>    _mockDt       = new();
    private readonly Mock<IApprovalService>    _mockApproval = new();
    private readonly Mock<ISystemParamService> _mockSysParam = new();
    private readonly Mock<IDocNumberService>   _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService> _mockPosting = new();
    private readonly Mock<IUomService>         _mockUom      = new();
    private readonly Mock<ICurrencyService>    _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public ScrapDocControllerTests()
    {
        _mockUom.WithStockConversion(_context);   // 存檔改由 ToStockQtyAsync 換算庫存數量（2026-10-12）
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IJ2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "廢料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "廢料倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "S-01", LocationName = "報廢區" });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "廢料",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "S-01",
        });
        // IJ is pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private ScrapDocController BuildController()
    {
        var ctrl = new ScrapDocController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContextWithServices(
            userId: UserId, permService: _mockPerm.Object, userContext: _mockUser.Object,
            approvalService: _mockApproval.Object,
            sharedLocalizer: GetMockLocalizer<ProjectZSharedResource>().Object);
        return ctrl;
    }

    private ScrapDoc SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr, decimal qty = 10, decimal putAway = 0)
    {
        var doc = new ScrapDoc
        {
            ScrapDocId = id,
            ScrapDate  = new DateOnly(2026, 4, 19),
            TypeCode   = "IJ",
            CreatedBy  = UserId,
            PlantId    = PlantId,
            Status     = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new ScrapDocLine
        {
            ScrapDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 5, WhCode = "WH01", LocationCode = "S-01",
            QtyPutAway = putAway,
        });
        _context.ScrapDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private ScrapDocFormVm BuildVm(string? id = null) => new()
    {
        ScrapDocId = id,
        ScrapDate  = new DateOnly(2026, 4, 19),
        TypeCode   = "IJ",
        CreatedBy  = UserId,
        Lines = new List<ScrapDocLineVm>
        {
            new() { ProductCode = "MAT001", Qty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC", UnitPrice = 5, WhCode = "WH01", LocationCode = "S-01" }
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
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PaginatedList<ScrapDoc>>(view.Model);
        Assert.Single(model);
    }

    // ── Save (new) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_New_NoApprovalConfig_ShouldCreate_AutoApproved()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode)).ReturnsAsync("false");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("toastSuccess", content.Content);
        Assert.Single(_context.ScrapDocs);
        Assert.Equal(DocumentApprovalStatus.Approved, _context.ScrapDocs.First().ApprovalStatus);
    }

    [Fact]
    public async Task Save_New_WithApprovalConfig_ShouldCreate_DraftApproval()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new DocumentApprovalConfig { DocumentType = "ScrapDoc", DocumentTypeName = "報廢單", ApprovalTemplateId = "TPL1" });

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm());

        Assert.IsType<ContentResult>(result);
        Assert.Equal(DocumentApprovalStatus.Draft, _context.ScrapDocs.First().ApprovalStatus);
    }

    [Fact]
    public async Task Save_Edit_PendingApproval_ShouldRejectWithError()
    {
        var doc = SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var vm = BuildVm("IJ2026041901");
        vm.ApprovalStatus = DocumentApprovalStatus.PendingApproval;

        var result = await ctrl.Save(vm);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
    }

    [Fact]
    public async Task Save_Edit_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, qty: 10, putAway: 5);
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm("IJ2026041901"));

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
    }

    // ── Submit / Withdraw ──────────────────────────────────────────────────

    [Fact]
    public async Task Submit_DraftDoc_ShouldSetPendingApproval()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        _mockApproval.Setup(a => a.GetApprovalConfig("ScrapDoc", PlantId))
            .ReturnsAsync(new DocumentApprovalConfig { ApprovalTemplateId = "TPL1" });
        _mockApproval.Setup(a => a.SubmitForApproval(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ApprovalRequest { Id = "REQ1" });

        var ctrl = BuildController();
        var result = await ctrl.Submit("IJ2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = _context.ScrapDocs.Find("IJ2026041901");
        Assert.Equal(DocumentApprovalStatus.PendingApproval, doc!.ApprovalStatus);
        Assert.Equal("REQ1", doc.ApprovalRequestId);
    }

    [Fact]
    public async Task Submit_NonDraft_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Withdraw_PendingDoc_ShouldSetDraft()
    {
        var doc = SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        doc.ApprovalRequestId = "REQ1";
        _context.SaveChanges();

        _mockApproval.Setup(a => a.WithdrawRequest("REQ1", UserId)).ReturnsAsync(true);

        var ctrl = BuildController();
        var result = await ctrl.Withdraw("IJ2026041901");

        Assert.IsType<OkObjectResult>(result);
        var updated = _context.ScrapDocs.Find("IJ2026041901");
        Assert.Equal(DocumentApprovalStatus.Draft, updated!.ApprovalStatus);
        Assert.Null(updated.ApprovalRequestId);
    }

    [Fact]
    public async Task Withdraw_NotPending_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_Approved_CompleteMode_ShouldSetConfirmed()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("IJ2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_Draft_Approved_SimplifiedMode_ShouldCallPosting()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode)).ReturnsAsync("true");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("IJ2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_NotDraft_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── CancelConfirm ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_Confirmed_NoOps_ShouldSetDraft()
    {
        SeedDoc("IJ2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IJ2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.ScrapDocs.FindAsync("IJ2026041901");
        Assert.Equal(DocStatus.Draft, doc!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved, putAway: 5);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CancelConfirm_NotConfirmed_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IJ2026041901");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("IJ2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Void("IJ2026041901", "damaged");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Void("IJ2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IJ2026041901");

        var json = Assert.IsType<JsonResult>(result);
        Assert.False(_context.ScrapDocs.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IJ2026041901");

        var json = Assert.IsType<JsonResult>(result);
        dynamic val = json.Value!;
        Assert.False((bool)val.GetType().GetProperty("success")!.GetValue(val)!);
        Assert.True(_context.ScrapDocs.Any());
    }

    [Fact]
    public async Task Delete_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, putAway: 3);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IJ2026041901");

        Assert.IsType<JsonResult>(result);
        Assert.True(_context.ScrapDocs.Any());
    }

    [Fact]
    public async Task Delete_NotFound_ShouldReturnJsonError()
    {
        var ctrl = BuildController();
        var result = await ctrl.Delete("NOPE");
        var json = Assert.IsType<JsonResult>(result);
        Assert.NotNull(json.Value);
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("IJ2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IJ2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IJ2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.ScrapDocs.IgnoreQueryFilters().Count());
        var newDoc = _context.ScrapDocs.IgnoreQueryFilters().First(d => d.ScrapDocId == "IJ2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("IJ2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IJ2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── 修改後重複過帳回歸（R16 動態面）────────────────────────────────────

    /// <summary>
    /// 修改草稿後（免簽核流程 Save 當下就過帳），過帳請求只能含使用者這次送出的明細。
    ///
    /// 報廢單是 R16 規則註解引用的那支——「RemoveRange 只標記 Deleted、不會從
    /// doc.Lines 記憶體集合移除」這句就寫在本檔 Controller 的 Clear() 上方，
    /// 但那行註解本身沒有任何測試釘住。少了 Clear()，殘留舊行會與新行一起進過帳請求，
    /// 沿用舊行號的那行被重複扣庫存，而存檔與畫面都看不出異常。
    /// </summary>
    [Fact]
    public async Task Save_EditReplacesLines_PostRequestCarriesOnlyNewLines()
    {
        SeedDoc("IJ2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, qty: 10);
        _context.ChangeTracker.Clear();

        // 預設 mock 的 RoundQtyAsync 回 0，會把數量整個吃掉；此處讓它原值回傳。
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal q, CancellationToken _) => q);

        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(
                It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        // 使用者把第 1 行數量從 10 改成 3，另外加一行：送出 2 行。
        var vm = new ScrapDocFormVm
        {
            ScrapDocId = "IJ2026041901",
            ScrapDate  = new DateOnly(2026, 4, 19),
            TypeCode   = "IJ",
            CreatedBy  = UserId,
            Lines = new List<ScrapDocLineVm>
            {
                new() { ProductCode = "MAT001", Qty = 3, UomCode = "PC", StockQty = 3, StockUomCode = "PC",
                        UnitPrice = 5, WhCode = "WH01", LocationCode = "S-01" },
                new() { ProductCode = "MAT001", Qty = 6, UomCode = "PC", StockQty = 6, StockUomCode = "PC",
                        UnitPrice = 5, WhCode = "WH01", LocationCode = "S-01" },
            },
        };

        await BuildController().Save(vm);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Lines.Count);
        Assert.Equal(new[] { 1, 2 }, captured.Lines.Select(l => l.LineNo).ToArray());
        // 殘留舊行的話這裡會多出一筆 Qty=10 的行號 1。
        Assert.Equal(3m, captured.Lines.Single(l => l.LineNo == 1).Qty);
        Assert.Equal(6m, captured.Lines.Single(l => l.LineNo == 2).Qty);
        Assert.Equal(9m, captured.Lines.Sum(l => l.Qty));
    }
}
