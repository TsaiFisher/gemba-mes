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

/// <summary>OutsourceMaterialController 生命週期測試：Index / Void / VoidAndReopen / Delete。</summary>
public class OutsourceMaterialControllerTests : TestBase
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

    public OutsourceMaterialControllerTests()
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
            .ReturnsAsync("OO2026041901");

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "委外料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "委外倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "O-01", LocationName = "委外區" });
        _context.Companies.Add(new Company { CompanyCode = "SUP001", CompanyName = "委外廠商 A", IsSupplier = true });
        _context.ProductCategories.Add(new ProductCategory
        {
            CategoryCode = "CAT01", CategoryName = "委外料",
            DefaultWarehouseCode = "WH01", DefaultLocationCode = "O-01",
        });
        // OO and OR are pre-seeded by HasData — no explicit Add needed
        _context.SaveChanges();
    }

    private OutsourceMaterialController BuildController()
    {
        var ctrl = new OutsourceMaterialController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockSysParam.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private OutsourceMaterialDoc SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr,
        string typeCode = "OO", decimal qty = 10)
    {
        var doc = new OutsourceMaterialDoc
        {
            OutsourceMaterialDocId = id,
            IssueDate              = new DateOnly(2026, 4, 19),
            TypeCode               = typeCode,
            SupplierCode           = "SUP001",
            CreatedBy              = UserId,
            PlantId                = PlantId,
            Status                 = status,
            ApprovalStatus         = appr,
        };
        doc.Lines.Add(new OutsourceMaterialDocLine
        {
            OutsourceMaterialDocId = id, LineNo = 1,
            ProductCode = "MAT001", Qty = qty, UomCode = "PC",
            StockQty = qty, StockUomCode = "PC",
            UnitPrice = 0, WhCode = "WH01", LocationCode = "O-01",
        });
        _context.OutsourceMaterialDocs.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<OutsourceMaterialDoc>>(view.Model);
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
        SeedDoc("OO2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("OO2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.OutsourceMaterialDocs.FindAsync("OO2026041901");
        Assert.Equal(DocStatus.Draft, doc!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("OO2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("OO2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.OutsourceMaterialDocs.FindAsync("OO2026041901");
        Assert.Equal(DocStatus.Confirmed, doc!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("OO2026041901", status, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("OO2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.OutsourceMaterialDocs.FindAsync("OO2026041901");
        Assert.Equal(status, doc!.Status);
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldCallVoidDoc()
    {
        SeedDoc("OO2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();
        var result = await ctrl.Void("OO2026041901", "error entry");
        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("OO2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("OO2026041901", null));
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldCallVoidDocAndCreateNewDraft()
    {
        SeedDoc("OO2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OO2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("OO2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        _mockPosting.Verify(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _context.OutsourceMaterialDocs.IgnoreQueryFilters().Count());
        var newDoc = _context.OutsourceMaterialDocs.IgnoreQueryFilters().First(d => d.OutsourceMaterialDocId == "OO2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("OO2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("OO2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("OO2026041901");
        Assert.False(_context.OutsourceMaterialDocs.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("OO2026041901");
        Assert.IsType<JsonResult>(result);
        Assert.True(_context.OutsourceMaterialDocs.Any());
    }

    // ── Save：MFG 連結檢核（pur-domain.md §4.3）─────────────────────────────

    private void SeedMfgLinks(decimal requiredQty = 10m)
    {
        _context.Dispatches.Add(new SmartFactory.MES.Modules.MFG.Models.Dispatch
        {
            DispatchId = "DSP-OS-01", WorkOrderId = "WO-01", OperationSeq = 10,
            IsOutsource = true, PlantId = PlantId, Quantity = 10m,
        });
        _context.DispatchMaterials.Add(new SmartFactory.MES.Modules.MFG.Models.DispatchMaterial
        {
            DispatchId = "DSP-OS-01", BomSeq = 1, ProductCode = "MAT001",
            RequiredQty = requiredQty, UomCode = "PC",
        });
        _context.OutsourcePurchaseOrders.Add(new SmartFactory.MES.Modules.MFG.Models.OutsourcePurchaseOrder
        {
            OutsourcePurchaseOrderId = "OPO-01", SupplierCode = "SUP001", PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    private static OutsourceMaterialDocFormVm BuildOoVm(decimal qty,
        string? dispatchId = "DSP-OS-01", string? opoId = null) => new()
    {
        IssueDate = new DateOnly(2026, 7, 10), TypeCode = "OO", SupplierCode = "SUP001",
        DispatchId = dispatchId, OutsourcePurchaseOrderId = opoId,
        Lines = new List<OutsourceMaterialDocLineVm>
        {
            new() { LineNo = 1, ProductCode = "MAT001", Qty = qty, UomCode = "PC",
                    StockQty = qty, StockUomCode = "PC", WhCode = "WH01", LocationCode = "O-01" },
        },
    };

    private OutsourceMaterialController BuildSaveController()
    {
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal q, CancellationToken _) => q);
        return BuildController();
    }

    [Fact]
    public async Task Save_OoWithinRequiredQty_ShouldSucceedAndLinkMfg()
    {
        SeedMfgLinks(requiredQty: 10m);
        var ctrl = BuildSaveController();

        var result = await ctrl.Save(BuildOoVm(qty: 8m, opoId: "OPO-01"));

        Assert.IsType<ContentResult>(result);
        var doc = Assert.Single(_context.OutsourceMaterialDocs.ToList());
        Assert.Equal("DSP-OS-01", doc.DispatchId);
        Assert.Equal("OPO-01", doc.OutsourcePurchaseOrderId);
    }

    [Fact]
    public async Task Save_OoExceedsRequiredQty_ShouldReject()
    {
        SeedMfgLinks(requiredQty: 10m);
        var ctrl = BuildSaveController();

        var result = await ctrl.Save(BuildOoVm(qty: 15m));

        Assert.IsType<PartialViewResult>(result);
        Assert.Empty(_context.OutsourceMaterialDocs.ToList());
    }

    [Fact]
    public async Task Save_OoExceedsRemainingAfterOtherDocs_ShouldReject()
    {
        SeedMfgLinks(requiredQty: 10m);
        // 他單已發 6：剩餘可發 4
        var prior = SeedDoc("OO2026041800", DocStatus.Posted, DocumentApprovalStatus.Approved, qty: 6m);
        prior.DispatchId = "DSP-OS-01";
        _context.SaveChanges();
        // 各用獨立 controller：ModelState 錯誤會殘留在同一實例上
        Assert.IsType<PartialViewResult>(await BuildSaveController().Save(BuildOoVm(qty: 5m)));   // 超過剩餘
        Assert.IsType<ContentResult>(await BuildSaveController().Save(BuildOoVm(qty: 4m)));       // 恰好用盡
    }

    [Fact]
    public async Task Save_OoProductNotInDispatch_ShouldReject()
    {
        SeedMfgLinks();
        _context.Products.Add(new Product { ProductCode = "MAT999", ProductName = "非派工料", StockUom = "PC", CategoryCode = "CAT01" });
        _context.SaveChanges();
        var vm = BuildOoVm(qty: 1m);
        vm.Lines[0].ProductCode = "MAT999";
        var ctrl = BuildSaveController();

        Assert.IsType<PartialViewResult>(await ctrl.Save(vm));
        Assert.Empty(_context.OutsourceMaterialDocs.ToList());
    }

    [Fact]
    public async Task Save_UnknownOpoOrDispatch_ShouldReject()
    {
        SeedMfgLinks();
        var ctrl = BuildSaveController();

        Assert.IsType<PartialViewResult>(await ctrl.Save(BuildOoVm(qty: 1m, dispatchId: "DSP-NOT-EXIST")));
        Assert.IsType<PartialViewResult>(await ctrl.Save(BuildOoVm(qty: 1m, opoId: "OPO-NOT-EXIST")));
        Assert.Empty(_context.OutsourceMaterialDocs.ToList());
    }

    [Fact]
    public async Task Save_OpoSupplierMismatch_ShouldReject()
    {
        SeedMfgLinks();
        _context.Companies.Add(new Company { CompanyCode = "SUP002", CompanyName = "另一廠商", IsSupplier = true });
        _context.SaveChanges();
        var vm = BuildOoVm(qty: 1m, opoId: "OPO-01");
        vm.SupplierCode = "SUP002";
        var ctrl = BuildSaveController();

        Assert.IsType<PartialViewResult>(await ctrl.Save(vm));
        Assert.Empty(_context.OutsourceMaterialDocs.ToList());
    }

    [Fact]
    public async Task Save_OrWithOpoLink_ShouldSucceedWithoutQtyGate()
    {
        // OR 退料：關聯委外採購單即可，不套 OO 發料量守門；
        // 但 OR 類別種子（SourceTypeCode=OO）要求逐行指定來源發料單。
        SeedMfgLinks(requiredQty: 1m);
        SeedDoc("OO2026041800", DocStatus.Posted, DocumentApprovalStatus.Approved, qty: 100m);
        var vm = BuildOoVm(qty: 99m, opoId: "OPO-01");
        vm.TypeCode = "OR";
        vm.Lines[0].SourceDocId = "OO2026041800";
        vm.Lines[0].SourceLineNo = 1;
        var ctrl = BuildSaveController();

        Assert.IsType<ContentResult>(await ctrl.Save(vm));
        var doc = Assert.Single(_context.OutsourceMaterialDocs.Where(d => d.TypeCode == "OR").ToList());
        Assert.Equal("OPO-01", doc.OutsourcePurchaseOrderId);
    }

    // ── Confirm：庫存簡化模式（inv_simplified_mode）分支 ────────────────────

    [Fact]
    public async Task Confirm_SimplifiedMode_ShouldPostImmediatelyWithStockQty()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved, qty: 7m);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var ctrl = BuildController();
        var result = await ctrl.Confirm("OO2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("OO2026041901", captured!.DocNo);
        Assert.Equal(PlantId, captured.PlantId);
        var line = Assert.Single(captured.Lines);
        Assert.Equal("MAT001", line.ProductCode);
        Assert.Equal(7m, line.Qty);              // 過帳數量取庫存單位量
        Assert.Equal("WH01", line.WhCode);
        Assert.Equal("O-01", line.LocationCode);
    }

    [Fact]
    public async Task Confirm_FullMode_ShouldOnlyMarkConfirmedWithoutPosting()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("OO2026041901");

        Assert.IsType<OkObjectResult>(result);
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.OutsourceMaterialDocs.Single().Status);
    }

    /// <summary>參數從未設定（乾淨部署，GetAsync 回 null）時視同完整模式，不得直接過帳。</summary>
    [Fact]
    public async Task Confirm_ParamNeverSet_ShouldBehaveAsFullMode()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var ctrl = BuildController();
        await ctrl.Confirm("OO2026041901");

        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DocStatus.Confirmed, _context.OutsourceMaterialDocs.Single().Status);
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_PostingRejects_ShouldReturnBadRequestAndNotConfirm()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Approved);
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("庫存不足"));

        var ctrl = BuildController();
        var result = await ctrl.Confirm("OO2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(DocStatus.Draft, _context.OutsourceMaterialDocs.Single().Status);
    }

    // ── 修改後重複過帳回歸（R16 動態面）────────────────────────────────────

    /// <summary>
    /// 簡化模式下修改草稿（Save 當下就過帳），過帳請求只能含這次送出的明細。
    ///
    /// 委外發料的殘留代價比一般單據高一層：多扣的不只是庫存，
    /// 發料當下凍結的 AVCO 單位成本會跟著多記一筆在外原料，
    /// 後續委外超耗計算與對帳報表都以那份餘額為基準，錯誤會一路傳遞下去。
    /// </summary>
    [Fact]
    public async Task Save_EditReplacesLines_PostRequestCarriesOnlyNewLines()
    {
        SeedDoc("OO2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, qty: 10);
        _context.ChangeTracker.Clear();
        _mockSysParam.Setup(s => s.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        // 預設 mock 的 RoundQtyAsync 回 0，會把數量整個吃掉；此處讓它原值回傳。
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal q, CancellationToken _) => q);

        PostDocSimplifiedRequest? captured = null;
        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(
                It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        // 使用者把第 1 行數量從 10 改成 2，另外加一行：送出 2 行。
        var vm = new OutsourceMaterialDocFormVm
        {
            OutsourceMaterialDocId = "OO2026041901",
            IssueDate    = new DateOnly(2026, 4, 19),
            TypeCode     = "OO",
            SupplierCode = "SUP001",
            CreatedBy    = UserId,
            Lines = new List<OutsourceMaterialDocLineVm>
            {
                new() { ProductCode = "MAT001", Qty = 2, UomCode = "PC", StockQty = 2, StockUomCode = "PC",
                        UnitPrice = 0, WhCode = "WH01", LocationCode = "O-01" },
                new() { ProductCode = "MAT001", Qty = 5, UomCode = "PC", StockQty = 5, StockUomCode = "PC",
                        UnitPrice = 0, WhCode = "WH01", LocationCode = "O-01" },
            },
        };

        await BuildController().Save(vm);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Lines.Count);
        Assert.Equal(new[] { 1, 2 }, captured.Lines.Select(l => l.LineNo).ToArray());
        // 殘留舊行的話這裡會多出一筆 Qty=10 的行號 1。
        Assert.Equal(2m, captured.Lines.Single(l => l.LineNo == 1).Qty);
        Assert.Equal(5m, captured.Lines.Single(l => l.LineNo == 2).Qty);
        Assert.Equal(7m, captured.Lines.Sum(l => l.Qty));
    }
}
