using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.ORD;

public class SalesReturnControllerTests : TestBase
{
    private readonly Mock<IUserContext>               _mockUser     = new();
    private readonly Mock<IPermissionService>         _mockPerm     = new();
    private readonly Mock<IDateTimeService>           _mockDt       = new();
    private readonly Mock<IApprovalService>           _mockApproval = new();
    private readonly Mock<IDocNumberService>          _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService>   _mockPosting  = new();
    private readonly Mock<IUomService>                _mockUom      = new();
    private readonly Mock<IPlantParamService>        _mockSysParam = new();
    private readonly Mock<ICurrencyService>           _mockCurrency;
    private readonly Mock<ISamplingPlanService>       _mockSampling = new();

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public SalesReturnControllerTests()
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
            .ReturnsAsync("SR2026051401");

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal qty, CancellationToken __) => qty);

        _mockPosting.Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockPosting.Setup(p => p.VoidDocAsync(It.IsAny<VoidDocRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VoidDocRequest, CancellationToken>((r, _) => r.SetDocVoid())
            .Returns(Task.CompletedTask);

        // Simplified mode off: Confirm takes the non-posting path (Confirmed status)
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("false");

        _mockCurrency = GetMockCurrencyService();
    }

    private SalesReturnController BuildController(
        SmartFactory.MES.Modules.FIN.Services.IARInvoiceService? arInvoice = null)
    {
        _mockSampling
            .Setup(s => s.GetDecisionAsync(It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SamplingDecision(true, 4, 5, "Normal"));

        var ctrl = new SalesReturnController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object,
            _mockUom.Object, _mockCurrency.Object, _mockSysParam.Object,
            _mockSampling.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object,
            arInvoice);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        ctrl.ControllerContext.HttpContext.RequestServices = BuildServiceProvider(
            (typeof(SmartFactory.MES.Services.IPermissionService), _mockPerm.Object),
            (typeof(SmartFactory.MES.Services.IUserContext), _mockUser.Object),
            (typeof(SmartFactory.MES.Data.ApplicationDbContext), _context),
            (typeof(SmartFactory.MES.Services.IApprovalService), _mockApproval.Object),
            (typeof(Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>), GetMockLocalizer<ProjectZSharedResource>().Object));
        return ctrl;
    }

    private void SetAjax(SalesReturnController ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private SalesReturn SeedDoc(string id, DocStatus status = DocStatus.Draft,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var doc = new SalesReturn
        {
            SalesReturnId  = id,
            ReturnDate     = new DateOnly(2026, 5, 14),
            CompanyCode    = "C001",
            CreatedBy      = UserId,
            PlantId        = PlantId,
            Status         = status,
            ApprovalStatus = appr,
        };
        doc.Lines.Add(new SalesReturnLine
        {
            SalesReturnId = id, LineNo = 1,
            ProductCode = "PRD001", ReturnQty = 10, UomCode = "PC",
        });
        _context.SalesReturns.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        SeedDoc("SR2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<SalesReturn>>(view.Model);
    }

    [Fact]
    public async Task Index_NotSearched_ReturnsNullModel()
    {
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: false);
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
        SeedDoc("SR2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("SR2026051401");
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
        SeedDoc("SR2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("SR2026051401");
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
        var vm = new SalesReturnFormVm
        {
            ReturnDate  = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<SalesReturnLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ReturnQty = 5, UomCode = "PC" }
            }
        };
        var result = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_POST_PendingApproval_ReturnsFormWithError()
    {
        var ctrl = BuildController();
        var vm = new SalesReturnFormVm
        {
            SalesReturnId  = "SR2026051401",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval,
            ReturnDate     = new DateOnly(2026, 5, 14),
            CompanyCode    = "C001",
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedDoc("SR2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Submit("SR2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.SalesReturns.Find("SR2026051401")!;
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
        SeedDoc("SR2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("SR2026051401");
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
        SeedDoc("SR2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("SR2026051401");
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
    public async Task Confirm_POST_AlreadyPosted_ReturnsBadRequest()
    {
        SeedDoc("SR2026051401", status: DocStatus.Posted, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("SR2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_NotApproved_ReturnsBadRequest()
    {
        SeedDoc("SR2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("SR2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_ApprovedDraft_SetsConfirmedStatus()
    {
        SeedDoc("SR2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("SR2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.SalesReturns.Find("SR2026051401")!;
        Assert.Equal(DocStatus.Confirmed, doc.Status);
    }

    [Fact]
    public async Task Confirm_SimplifiedMode_ReversesArForSourceShipment()
    {
        // 簡化模式完成過帳＝退回貨入正常倉 → 負向回沖來源出貨單應收（依來源出貨行號對應）。
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var doc = SeedDoc("SR2026051403", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        doc.Lines.First().GoodsShipmentId = "GS-SRC-01";
        doc.Lines.First().ShipmentLineNo = 1;
        doc.Lines.First().ReturnQty = 12m;
        // 簡化過帳需儲位（此明細品號類別無預設儲位 → 明確指定）
        doc.Lines.First().WhCode = "WH01";
        doc.Lines.First().LocationCode = "A-01";
        _context.SaveChanges();

        var mockAr = new Mock<SmartFactory.MES.Modules.FIN.Services.IARInvoiceService>();
        mockAr.Setup(s => s.ReverseForReturnedQtyAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmartFactory.MES.Modules.FIN.Services.ReturnReversalResult.NoInvoice);

        var ctrl = BuildController(mockAr.Object);
        var result = await ctrl.Confirm("SR2026051403");

        Assert.IsType<OkObjectResult>(result);
        mockAr.Verify(s => s.ReverseForReturnedQtyAsync("GS-SRC-01", 1, 12m, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CancelConfirm POST ────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirm_POST_NotFound_Returns404()
    {
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CancelConfirm_POST_NotConfirmed_ReturnsBadRequest()
    {
        SeedDoc("SR2026051401", status: DocStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("SR2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CancelConfirm_POST_ConfirmedDoc_SetsDraftStatus()
    {
        SeedDoc("SR2026051401", status: DocStatus.Confirmed, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CancelConfirm("SR2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.SalesReturns.Find("SR2026051401")!;
        Assert.Equal(DocStatus.Draft, doc.Status);
    }

    // ── Void POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_POST_NotPosted_ReturnsBadRequest()
    {
        SeedDoc("SR2026051401", status: DocStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Void("SR2026051401", null);
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
    public async Task Void_POST_Posted_ReversesGsReturnedQty()
    {
        // §3.9 修正點：銷退作廢須回沖出貨明細累計退回量（對齊出貨單作廢回沖 ShippedQty）
        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = "GS2026051401", CompanyCode = "C001",
            Status = DocStatus.Posted, PlantId = PlantId,
            ShipDate = new DateOnly(2026, 5, 1), CreatedBy = UserId,
        });
        _context.GoodsShipmentLines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = "GS2026051401", LineNo = 1, ProductCode = "PRD001",
            ShipQty = 100, ReturnedQty = 10,
        });
        var doc = SeedDoc("SR2026051402", status: DocStatus.Posted, appr: DocumentApprovalStatus.Approved);
        doc.Lines.First().GoodsShipmentId = "GS2026051401";
        doc.Lines.First().ShipmentLineNo = 1;
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Void("SR2026051402", "測試作廢");

        Assert.IsType<OkObjectResult>(result);
        var gsLine = await _context.GoodsShipmentLines.FindAsync("GS2026051401", 1);
        Assert.Equal(0m, gsLine!.ReturnedQty);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_DraftRecord_ReturnsJsonSuccess()
    {
        SeedDoc("SR2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Delete("SR2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.SalesReturns.Any());
    }

    [Fact]
    public async Task Delete_POST_PendingApproval_ReturnsJsonError()
    {
        SeedDoc("SR2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("SR2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    /// <summary>刪除退回單回沖來源出貨的已退貨量：正常情形減回去。</summary>
    [Fact]
    public async Task Delete_POST_ReversesShipmentReturnedQty()
    {
        SeedDocLinkedToShipment("SR2026051410", "GS-REV-01", gsReturnedQty: 10m);
        var ctrl = BuildController();

        await ctrl.Delete("SR2026051410");

        Assert.Equal(0m, _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == "GS-REV-01").ReturnedQty);
    }

    /// <summary>
    /// 回沖有下限 0：已退貨量若已是 0，再刪一次不得變成負數。
    /// 負的已退量會讓這張出貨的可退額度（ShipQty − ReturnedQty）算得比出貨量還多。
    /// </summary>
    [Fact]
    public async Task Delete_POST_ReturnedQtyReversal_FloorsAtZero()
    {
        SeedDocLinkedToShipment("SR2026051411", "GS-REV-02", gsReturnedQty: 0m);
        var ctrl = BuildController();

        await ctrl.Delete("SR2026051411");

        var returned = _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == "GS-REV-02").ReturnedQty;
        Assert.Equal(0m, returned);
        Assert.True(returned >= 0m, $"已退貨量不得為負，實際 {returned}");
    }

    // ── 正向累加上限（累計退貨不得超過出貨量）─────────────────────────────

    /// <summary>
    /// 累計退貨超過出貨量必須被拒絕。
    ///
    /// 超退的傷害不只是數字難看：退回會負向回沖來源出貨單的 AR 發票，
    /// 退超過出貨量等於把應收沖到低於實際應收——客戶少付的那一段
    /// 在帳上看起來是「已開立金額」，對帳時只會顯示這張出貨的應收與出貨金額對不起來。
    /// 庫存側也會憑空多入一批從未出去過的貨。
    /// </summary>
    [Fact]
    public async Task Save_POST_ReturnQtyExceedingShipQty_Rejected()
    {
        SeedShipment("GS-CAP-01", shipQty: 100m, returnedQty: 95m);
        var ctrl = BuildController();
        var vm = new SalesReturnFormVm
        {
            ReturnDate  = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<SalesReturnLineVm>
            {
                // 95 + 6 = 101 > 100
                new() { LineNo = 1, ProductCode = "PRD001", ReturnQty = 6, UomCode = "PC",
                        GoodsShipmentId = "GS-CAP-01", ShipmentLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.Equal(95m, _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == "GS-CAP-01").ReturnedQty);
    }

    /// <summary>邊界：剛好全退必須放行（守門是 <c>&gt;</c> 不是 <c>&gt;=</c>）。</summary>
    [Fact]
    public async Task Save_POST_ReturnQtyExactlyShipQty_Allowed()
    {
        SeedShipment("GS-CAP-02", shipQty: 100m, returnedQty: 95m);
        var ctrl = BuildController();
        var vm = new SalesReturnFormVm
        {
            ReturnDate  = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<SalesReturnLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ReturnQty = 5, UomCode = "PC",
                        GoodsShipmentId = "GS-CAP-02", ShipmentLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
        Assert.Equal(100m, _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == "GS-CAP-02").ReturnedQty);
    }

    /// <summary>出貨明細一行（出貨量與已退量由參數指定），狀態 Posted。</summary>
    // ── 修改後重複累加回歸（R16 動態面）───────────────────────────────────

    /// <summary>
    /// 修改草稿後，出貨單已退貨量必須剛好等於這次送出的明細合計。
    ///
    /// 與出貨單同一形態的殘留，但方向相反、代價更高：
    /// 退回會負向回沖來源應收發票，已退量被重複累加代表應收被多沖，
    /// 客戶帳面欠款直接少掉一截，而庫存與傳票上完全找不到對應的異動。
    /// </summary>
    [Fact]
    public async Task Save_POST_EditReplacesLines_DoesNotReAccumulateStaleLines()
    {
        // 出貨 100、已退 10（全部來自本單那一行）。
        SeedDocLinkedToShipment("SR2026051413", "GS-DUP-01", gsReturnedQty: 10m);
        var ctrl = BuildController();

        // 第 1 行由 10 改成 4，另外加一行 7：本單新合計 11。
        var vm = new SalesReturnFormVm
        {
            SalesReturnId = "SR2026051413",
            ReturnDate    = new DateOnly(2026, 5, 14),
            CompanyCode   = "C001",
            Lines         = new List<SalesReturnLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ReturnQty = 4, UomCode = "PC",
                        GoodsShipmentId = "GS-DUP-01", ShipmentLineNo = 1 },
                new() { LineNo = 2, ProductCode = "PRD001", ReturnQty = 7, UomCode = "PC",
                        GoodsShipmentId = "GS-DUP-01", ShipmentLineNo = 1 },
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
        // 10 − 10 + 11 = 11。殘留舊行的話會是 21（舊的 10 被再加一次）。
        Assert.Equal(11m, _context.GoodsShipmentLines.First(l => l.GoodsShipmentId == "GS-DUP-01").ReturnedQty);
        var lines = _context.SalesReturnLines.Where(l => l.SalesReturnId == "SR2026051413").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(11m, lines.Sum(l => l.ReturnQty));
    }

    private void SeedShipment(string gsId, decimal shipQty, decimal returnedQty)
    {
        var gs = new GoodsShipment
        {
            GoodsShipmentId = gsId, ShipDate = new DateOnly(2026, 5, 14),
            CompanyCode = "C001", CreatedBy = UserId, PlantId = PlantId,
            Status = DocStatus.Posted, ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        gs.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = gsId, LineNo = 1, ProductCode = "PRD001",
            ShipQty = shipQty, StockQty = shipQty, UomCode = "PC", StockUomCode = "PC",
            UnitPrice = 5m, ReturnedQty = returnedQty,
        });
        _context.GoodsShipments.Add(gs);
        _context.SaveChanges();
    }

    /// <summary>草稿退回單 + 已連結的出貨明細（出貨 100、已退量由參數指定）。</summary>
    private void SeedDocLinkedToShipment(string id, string gsId, decimal gsReturnedQty)
    {
        SeedShipment(gsId, shipQty: 100m, returnedQty: gsReturnedQty);

        var doc = SeedDoc(id);
        var line = doc.Lines.First();
        line.GoodsShipmentId = gsId;
        line.ShipmentLineNo  = 1;
        _context.SaveChanges();
    }
}
