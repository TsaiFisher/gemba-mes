using Microsoft.AspNetCore.Mvc;
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

/// <summary>LoanDocController 生命週期測試：Save / TypeCode 驗證 / Confirm / CancelConfirm / Void / Delete。</summary>
public class LoanDocControllerTests : TestBase
{
    private readonly Mock<IUserContext>        _mockUser     = new();
    private readonly Mock<IPermissionService>  _mockPerm     = new();
    private readonly Mock<IDateTimeService>    _mockDt       = new();
    private readonly Mock<IApprovalService>    _mockApproval = new();
    private readonly Mock<IPlantParamService> _mockSysParam = new();
    private readonly Mock<IDocNumberService>   _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService> _mockPosting = new();
    private readonly Mock<IUomService>         _mockUom      = new();
    private readonly Mock<ICurrencyService>    _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public LoanDocControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IB2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "借用料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "借用倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "B-01", LocationName = "借用區" });
        _context.Companies.Add(new Company { CompanyCode = "CUS001", CompanyName = "客戶 A", IsCustomer = true });
        _context.Companies.Add(new Company { CompanyCode = "SUP001", CompanyName = "供應商 A", IsSupplier = true });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "借用料",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "B-01",
        });
        // IB, BR, IL, LR are pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private LoanDocController BuildController()
    {
        var ctrl = new LoanDocController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockSysParam.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private LoanDoc SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr,
        string typeCode = "IB", decimal qty = 10, decimal putAway = 0)
    {
        var doc = new LoanDoc
        {
            LoanDocId  = id,
            LoanDate   = new DateOnly(2026, 4, 19),
            TypeCode   = typeCode,
            EntityType = "Customer",
            EntityCode = "CUS001",
            CreatedBy  = UserId,
            PlantId    = PlantId,
            Status     = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new LoanDocLine
        {
            LoanDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 5, WhCode = "WH01", LocationCode = "B-01",
            QtyPutAway = putAway,
        });
        _context.LoanDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private LoanDocFormVm BuildVm(string? id = null, string typeCode = "IB") => new()
    {
        LoanDocId  = id,
        LoanDate   = new DateOnly(2026, 4, 19),
        TypeCode   = typeCode,
        EntityType = "Customer",
        EntityCode = "CUS001",
        CreatedBy  = UserId,
        Lines = new List<LoanDocLineVm>
        {
            new() { ProductCode = "MAT001", Qty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC", UnitPrice = 5, WhCode = "WH01", LocationCode = "B-01" }
        }
    };

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<LoanDoc>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    // ── Save ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_New_ValidTypeCode_ShouldCreate()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm(typeCode: "IB"));

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("toastSuccess", content.Content);
        Assert.Single(_context.LoanDocs);
    }

    [Fact]
    public async Task Save_New_InvalidTypeCode_ShouldReject()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm(typeCode: "IJ")); // IJ is ScrapDoc, not LoanDoc

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
    }

    [Theory]
    [InlineData("IB")]
    [InlineData("IL")]
    [InlineData("BR")]
    [InlineData("LR")]
    public async Task Save_AllValidTypeCodes_ShouldAccept(string typeCode)
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync($"{typeCode}2026041901");

        var ctrl = BuildController();
        var vm = BuildVm(typeCode: typeCode);
        // BR / LR are return TypeCodes that require SourceDocId; qty check is bypassed for "Loan" category
        if (typeCode is "BR" or "LR")
            vm.Lines[0].SourceDocId = "IB2026041901";
        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_Edit_PendingApproval_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var vm = BuildVm("IB2026041901");
        vm.ApprovalStatus = DocumentApprovalStatus.PendingApproval;

        var result = await ctrl.Save(vm);
        Assert.IsType<PartialViewResult>(result);
    }

    /// <summary>
    /// 偽造 hidden 欄位：資料庫是 PendingApproval，表單硬說是 Draft，仍須被擋下。
    ///
    /// _Form 的 ApprovalStatus 是 hidden 欄位，而且刻意放在 &lt;fieldset disabled&gt; 外面
    /// （disabled 的欄位瀏覽器不送），所以使用者改得動它。守衛若採信 vm 帶回的值，
    /// 自行組一個 POST 就能改掉一張審核中的單，而簽核申請那筆記錄完全沒動——
    /// 簽核人核准的是他沒看過的內容。13 支單據 controller 曾一起中這個招。
    ///
    /// 上面那支（vm 與 DB 都是 PendingApproval）擋得住，證明不了守衛讀的是哪一邊；
    /// 要靠這支讓兩邊不一致才分得出來。
    /// </summary>
    [Fact]
    public async Task Save_Edit_PendingApprovalInDb_TamperedVmStatus_ShouldStillReject()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var vm = BuildVm("IB2026041901");
        vm.ApprovalStatus = DocumentApprovalStatus.Draft;   // ← 偽造

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_Approved_CompleteMode_ShouldSetConfirmed()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("IB2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = _context.LoanDocs.Find("IB2026041901");
        Assert.Equal(DocStatus.Confirmed, doc!.Status);
    }

    [Fact]
    public async Task Confirm_Draft_Approved_SimplifiedMode_ShouldCallPosting()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ctrl = BuildController();
        await ctrl.Confirm("IB2026041901");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Confirm("IB2026041901"));
    }

    // ── CancelConfirm ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_Confirmed_NoOps_ShouldSetDraft()
    {
        SeedDoc("IB2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IB2026041901");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocStatus.Draft, _context.LoanDocs.Find("IB2026041901")!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved, putAway: 5);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.CancelConfirm("IB2026041901"));
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("IB2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Void("IB2026041901", "error entry");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("IB2026041901", null));
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("IB2026041901");
        Assert.False(_context.LoanDocs.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("IB2026041901");
        Assert.IsType<JsonResult>(result);
        Assert.True(_context.LoanDocs.Any());
    }

    [Fact]
    public async Task Delete_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, putAway: 2);
        var ctrl = BuildController();
        await ctrl.Delete("IB2026041901");
        Assert.True(_context.LoanDocs.Any());
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("IB2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("IB2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IB2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.LoanDocs.IgnoreQueryFilters().Count());
        var newDoc = _context.LoanDocs.IgnoreQueryFilters().First(d => d.LoanDocId == "IB2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("IB2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("IB2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
