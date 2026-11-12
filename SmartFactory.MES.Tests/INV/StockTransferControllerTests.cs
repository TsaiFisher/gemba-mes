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

/// <summary>StockTransferController 生命週期測試：Index / Void / VoidAndReopen / Delete。</summary>
public class StockTransferControllerTests : TestBase
{
    private readonly Mock<IUserContext>              _mockUser     = new();
    private readonly Mock<IPermissionService>        _mockPerm     = new();
    private readonly Mock<IDateTimeService>          _mockDt       = new();
    private readonly Mock<IApprovalService>          _mockApproval = new();
    private readonly Mock<ISystemParamService>       _mockSysParam = new();
    private readonly Mock<IDocNumberService>         _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService>  _mockPosting  = new();
    private readonly Mock<IUomService>               _mockUom      = new();
    private readonly Mock<ICurrencyService>          _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public StockTransferControllerTests()
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
            .ReturnsAsync("WT2026041901");

        _mockPosting.Setup(p => p.PostSamePlantTransferAsync(
                It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostSamePlantTransferRequest, CancellationToken>((req, _) =>
            {
                foreach (var l in req.Lines) l.SetQtyPutAway(l.StockQty);
                req.SetDocPosted();
            })
            .Returns(Task.CompletedTask);

        _mockPosting.Setup(p => p.VoidByMovementsAsync(
                It.IsAny<IReadOnlyList<StockMovement>>(), It.IsAny<VoidByMovementsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<StockMovement>, VoidByMovementsRequest, CancellationToken>((_, req, _) =>
                req.SetDocVoid?.Invoke())
            .Returns(Task.CompletedTask);

        _mockCurrency = GetMockCurrencyService();

        SeedMasterData();
    }

    private void SeedMasterData()
    {
        _context.Products.Add(new Product { ProductCode = "MAT001", ProductName = "調撥料 A", StockUom = "PC", CategoryCode = "CAT01" });
        _context.Uoms.Add(new Uom { UomCode = "PC", UomName = "個" });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH01", WhName = "來源倉", PlantId = PlantId });
        _context.Warehouses.Add(new Warehouse { WhCode = "WH02", WhName = "目的倉", PlantId = PlantId });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH01", LocationCode = "A-01", LocationName = "來源位" });
        _context.StorageLocations.Add(new StorageLocation { WhCode = "WH02", LocationCode = "B-01", LocationName = "目的位" });
        _context.ProductCategories.Add(new ProductCategory { CategoryCode = "CAT01", CategoryName = "調撥料" });
        // WT is seeded here for PrepareFormDropdowns lookup (not in HasData)
        _context.InventoryTransactionTypes.Add(new InventoryTransactionType { TypeCode = "WT", TypeName = "廠內調撥", Direction = 0, TypeCategory = "Transfer" });
        _context.SaveChanges();
    }

    private StockTransferController BuildController()
    {
        var ctrl = new StockTransferController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        return ctrl;
    }

    private StockTransfer SeedDoc(string id, DocStatus status, DocumentApprovalStatus appr, decimal qty = 10)
    {
        var doc = new StockTransfer
        {
            StockTransferId = id,
            TransferDate    = new DateOnly(2026, 4, 19),
            TypeCode        = "WT",
            CreatedBy       = UserId,
            PlantId         = PlantId,
            FromWhCode      = "WH01",
            ToWhCode        = "WH02",
            Status          = status,
            ApprovalStatus  = appr,
        };
        doc.Lines.Add(new StockTransferLine
        {
            StockTransferId  = id, LineNo = 1,
            ProductCode      = "MAT001", Qty = qty, UomCode = "PC",
            StockQty         = qty, StockUomCode = "PC",
            UnitPrice        = 0,
            FromWhCode       = "WH01", FromLocationCode = "A-01",
            ToWhCode         = "WH02", ToLocationCode   = "B-01",
        });
        _context.StockTransfers.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ShouldReturnPaginatedList()
    {
        SeedDoc("WT2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<StockTransfer>>(view.Model);
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
        SeedDoc("IT2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IT2026041901");

        Assert.IsType<OkObjectResult>(result);
        var doc = await _context.StockTransfers.FindAsync("IT2026041901");
        Assert.Equal(DocStatus.Draft, doc!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("IT2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IT2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.StockTransfers.FindAsync("IT2026041901");
        Assert.Equal(DocStatus.Confirmed, doc!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("IT2026041901", status, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("IT2026041901");

        Assert.IsType<BadRequestObjectResult>(result);
        var doc = await _context.StockTransfers.FindAsync("IT2026041901");
        Assert.Equal(status, doc!.Status);
    }

    // ── Void ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Posted_ShouldSetVoid()
    {
        SeedDoc("WT2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Void("WT2026041901", "error entry");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.StockTransfers.IgnoreQueryFilters().FirstOrDefault(d => d.StockTransferId == "WT2026041901");
        Assert.Equal(DocStatus.Void, doc!.Status);
    }

    [Fact]
    public async Task Void_NotPosted_ShouldReject()
    {
        SeedDoc("WT2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        Assert.IsType<BadRequestObjectResult>(await ctrl.Void("WT2026041901", null));
    }

    // ── VoidAndReopen ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidAndReopen_Posted_ShouldVoidAndCreateNewDraft()
    {
        SeedDoc("WT2026041901", DocStatus.Posted, DocumentApprovalStatus.Approved);
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("WT2026041902");

        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("WT2026041901", "test reason");

        Assert.IsType<PartialViewResult>(result);
        Assert.Equal(2, _context.StockTransfers.IgnoreQueryFilters().Count());
        var newDoc = _context.StockTransfers.IgnoreQueryFilters().First(d => d.StockTransferId == "WT2026041902");
        Assert.Equal(DocStatus.Draft, newDoc.Status);
        var oldDoc = _context.StockTransfers.IgnoreQueryFilters().First(d => d.StockTransferId == "WT2026041901");
        Assert.Equal(DocStatus.Void, oldDoc.Status);
    }

    [Fact]
    public async Task VoidAndReopen_NotPosted_ShouldReject()
    {
        SeedDoc("WT2026041901", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.VoidAndReopen("WT2026041901", null);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_DraftDoc_ShouldRemove()
    {
        SeedDoc("WT2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        await ctrl.Delete("WT2026041901");
        Assert.False(_context.StockTransfers.Any());
    }

    [Fact]
    public async Task Delete_PendingApproval_ShouldReject()
    {
        SeedDoc("WT2026041901", DocStatus.Draft, DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("WT2026041901");
        Assert.IsType<JsonResult>(result);
        Assert.True(_context.StockTransfers.Any());
    }

    // ── 修改後重複過帳回歸（R16 動態面）────────────────────────────────────

    /// <summary>
    /// 修改草稿後（免簽核流程 Save 當下就過帳），過帳請求只能含使用者這次送出的明細。
    ///
    /// 守的是 <c>RemoveRange</c> 殘留：EF 只把舊行標記 Deleted，**不會從 doc.Lines
    /// 記憶體集合移除**，若少了 Clear()，重新 Add 的新行會與殘留舊行並存，
    /// 過帳時一併算入 → 沿用舊行號的那行被扣兩次庫存。壞法極隱蔽：
    /// 存檔成功、單據畫面正常，只有庫存數字悄悄多扣。
    ///
    /// 規則 R16 已靜態守住「RemoveRange 後必須 Clear 或整個換掉集合」，
    /// 但靜態規則看不出「Clear 了但過帳仍取到舊集合」這類變形，故本測試從
    /// 過帳請求這個唯一觀察點斷言。移倉是 INV 過帳量最大的單據。
    /// </summary>
    [Fact]
    public async Task Save_EditReplacesLines_PostRequestCarriesOnlyNewLines()
    {
        SeedDoc("WT2026041901", DocStatus.Draft, DocumentApprovalStatus.Draft, qty: 10);
        _context.ChangeTracker.Clear();

        // 預設 mock 的 RoundQtyAsync 回 0，會把數量整個吃掉；此處讓它原值回傳。
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal q, CancellationToken _) => q);

        PostSamePlantTransferRequest? captured = null;
        _mockPosting.Setup(p => p.PostSamePlantTransferAsync(
                It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostSamePlantTransferRequest, CancellationToken>((req, _) =>
            {
                captured = req;
                foreach (var l in req.Lines) l.SetQtyPutAway(l.StockQty);
                req.SetDocPosted();
            })
            .Returns(Task.CompletedTask);

        // 使用者把第 1 行數量從 10 改成 4，另外加一行：送出 2 行。
        var vm = new StockTransferFormVm
        {
            StockTransferId = "WT2026041901",
            TransferDate = new DateOnly(2026, 4, 19),
            FromWhCode = "WH01", ToWhCode = "WH02",
            Lines = new List<StockTransferLineVm>
            {
                new() { LineNo = 1, ProductCode = "MAT001", Qty = 4, UomCode = "PC", StockQty = 4, StockUomCode = "PC",
                        FromWhCode = "WH01", FromLocationCode = "A-01", ToWhCode = "WH02", ToLocationCode = "B-01" },
                new() { LineNo = 2, ProductCode = "MAT001", Qty = 7, UomCode = "PC", StockQty = 7, StockUomCode = "PC",
                        FromWhCode = "WH01", FromLocationCode = "A-01", ToWhCode = "WH02", ToLocationCode = "B-01" },
            },
        };

        await BuildController().Save(vm);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Lines.Count);
        Assert.Equal(new[] { 1, 2 }, captured.Lines.Select(l => l.LineNo).ToArray());
        // 殘留舊行的話這裡會多出一筆 StockQty=10 的行號 1。
        Assert.Equal(4m, captured.Lines.Single(l => l.LineNo == 1).StockQty);
        Assert.Equal(7m, captured.Lines.Single(l => l.LineNo == 2).StockQty);
        Assert.Equal(11m, captured.Lines.Sum(l => l.StockQty));
    }

    // ── 批號（container-tracking.md §9.3）──────────────────────────────────

    /// <summary>
    /// 明細批號要一路帶到過帳請求：過帳服務靠它決定扣哪一份庫存、搬哪一層成本。
    /// 前後空白去掉（掃描槍常帶尾端空白）；空白＝不追蹤批號，傳空字串而非 null。
    /// </summary>
    [Fact]
    public async Task Save_LineLotNo_IsTrimmedAndPassedToPosting_BlankMeansNoLot()
    {
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal q, CancellationToken _) => q);

        PostSamePlantTransferRequest? captured = null;
        _mockPosting.Setup(p => p.PostSamePlantTransferAsync(
                It.IsAny<PostSamePlantTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostSamePlantTransferRequest, CancellationToken>((req, _) =>
            {
                captured = req;
                foreach (var l in req.Lines) l.SetQtyPutAway(l.StockQty);
                req.SetDocPosted();
            })
            .Returns(Task.CompletedTask);

        var vm = new StockTransferFormVm
        {
            TransferDate = new DateOnly(2026, 4, 19),
            FromWhCode = "WH01", ToWhCode = "WH02",
            Lines = new List<StockTransferLineVm>
            {
                new() { ProductCode = "MAT001", Qty = 5, UomCode = "PC", LotNo = " WO-01-03 ",
                        FromWhCode = "WH01", FromLocationCode = "A-01", ToWhCode = "WH02", ToLocationCode = "B-01" },
                new() { ProductCode = "MAT001", Qty = 2, UomCode = "PC", LotNo = "  ",
                        FromWhCode = "WH01", FromLocationCode = "A-01", ToWhCode = "WH02", ToLocationCode = "B-01" },
            },
        };

        await BuildController().Save(vm);

        Assert.NotNull(captured);
        Assert.Equal("WO-01-03", captured!.Lines.Single(l => l.LineNo == 1).LotNo);
        Assert.Equal("",         captured.Lines.Single(l => l.LineNo == 2).LotNo);

        var saved = _context.StockTransferLines.OrderBy(l => l.LineNo).ToList();
        Assert.Equal("WO-01-03", saved[0].LotNo);
        Assert.Null(saved[1].LotNo);
    }
}
