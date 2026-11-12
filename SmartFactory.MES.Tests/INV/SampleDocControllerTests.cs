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

/// <summary>SampleDocController 生命週期測試：Save / TypeCode 驗證 / Confirm / CancelConfirm / Void / Delete。</summary>
public class SampleDocControllerTests : TestBase
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

    public SampleDocControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("CI2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "SAMP001", ProductName = "樣品 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "樣品倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "SP-01", LocationName = "樣品區" });
        _context.Companies.Add(new Company { CompanyCode = "CUS001", CompanyName = "客戶 B", IsCustomer = true });
        _context.Companies.Add(new Company { CompanyCode = "SUP001", CompanyName = "供應商 B", IsSupplier = true });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "樣品",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "SP-01",
        });
        // CI, CR, SO, SR are pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private SampleDocController BuildController()
    {
        var ctrl = new SampleDocController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockSysParam.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private SampleDoc SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr,
        string typeCode = "CI", decimal qty = 10, decimal putAway = 0)
    {
        var doc = new SampleDoc
        {
            SampleDocId = id,
            SampleDate  = new DateOnly(2026, 4, 19),
            TypeCode    = typeCode,
            EntityType  = "Customer",
            EntityCode  = "CUS001",
            CreatedBy   = UserId,
            PlantId     = PlantId,
            Status      = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new SampleDocLine
        {
            SampleDocId = id, LineNo = 1,
            ProductCode = "SAMP001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 0, WhCode = "WH01", LocationCode = "SP-01",
            QtyPutAway = putAway,
        });
        _context.SampleDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    private SampleDocFormVm BuildVm(string? id = null, string typeCode = "CI") => new()
    {
        SampleDocId = id,
        SampleDate  = new DateOnly(2026, 4, 19),
        TypeCode    = typeCode,
        EntityType  = "Customer",
        EntityCode  = "CUS001",
        CreatedBy   = UserId,
        Lines = new List<SampleDocLineVm>
        {
            new() { ProductCode = "SAMP001", Qty = 2, UomCode = "PC", StockQty = 2, StockUomCode = "PC", UnitPrice = 0, WhCode = "WH01", LocationCode = "SP-01" }
        }
    };

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NotSearched_ShouldReturnNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<SampleDoc>>(view.Model);
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
        var result = await ctrl.Save(BuildVm(typeCode: "CI"));

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("toastSuccess", content.Content);
        Assert.Single(_context.SampleDocs);
    }

    [Fact]
    public async Task Save_New_InvalidTypeCode_ShouldReject()
    {
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm(typeCode: "IJ")); // IJ is ScrapDoc, not valid here

        Assert.IsType<PartialViewResult>(result);
        Assert.False(((Controller)ctrl).ModelState.IsValid);
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("CR")]
    [InlineData("SO")]
    [InlineData("SR")]
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
        // CR / SR are return TypeCodes that require SourceDocId; qty check is bypassed for "Sample" category
        if (typeCode is "CR" or "SR")
            vm.Lines[0].SourceDocId = "CI2026041901";
        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_Edit_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, putAway: 1);
        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        var ctrl = BuildController();
        var result = await ctrl.Save(BuildVm("CI2026041901"));

        Assert.IsType<PartialViewResult>(result);
    }

    // ── Confirm ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Draft_Approved_CompleteMode_ShouldSetConfirmed()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("CI2026041901");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocStatus.Confirmed, _context.SampleDocs.Find("CI2026041901")!.Status);
    }

    [Fact]
    public async Task Confirm_Draft_Approved_SimplifiedMode_ShouldCallPosting()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ctrl = BuildController();
        await ctrl.Confirm("CI2026041901");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_NotDraft_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Confirm("CI2026041901"));
    }

    [Fact]
    public async Task Confirm_NotApproved_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Confirm("CI2026041901"));
    }

    // ── CancelConfirm ──────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_Confirmed_NoOps_ShouldSetDraft()
    {
        SeedDoc("CI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("CI2026041901");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocStatus.Draft, _context.SampleDocs.Find("CI2026041901")!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved, putAway: 1);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.CancelConfirm("CI2026041901"));
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("CI2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Void("CI2026041901", "wrong entry");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("CI2026041901", null));
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("CI2026041901");
        Assert.False(_context.SampleDocs.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        await ctrl.Delete("CI2026041901");
        Assert.True(_context.SampleDocs.Any());
    }

    [Fact]
    public async Task Delete_WithQtyPutAway_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, putAway: 1);
        var ctrl = BuildController();
        await ctrl.Delete("CI2026041901");
        Assert.True(_context.SampleDocs.Any());
    }

    [Fact]
    public async Task Delete_NotFound_ShouldReturnJsonError()
    {
        var ctrl = BuildController();
        var result = await ctrl.Delete("NOPE");
        Assert.IsType<JsonResult>(result);
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("CI2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("CI2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("CI2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.SampleDocs.IgnoreQueryFilters().Count());
        var newDoc = _context.SampleDocs.IgnoreQueryFilters().First(d => d.SampleDocId == "CI2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("CI2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("CI2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
