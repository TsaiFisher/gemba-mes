using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.ORD;

public class GoodsShipmentControllerTests : TestBase
{
    private readonly Mock<IUserContext>               _mockUser     = new();
    private readonly Mock<IPermissionService>         _mockPerm     = new();
    private readonly Mock<IDateTimeService>           _mockDt       = new();
    private readonly Mock<IApprovalService>           _mockApproval = new();
    private readonly Mock<IDocNumberService>          _mockDocNo    = new();
    private readonly Mock<IInventoryPostingService>   _mockPosting  = new();
    private readonly Mock<IInventoryService>          _mockInvSvc   = new();
    private readonly Mock<IUomService>                _mockUom      = new();
    private readonly Mock<IPlantParamService>        _mockSysParam  = new();
    private readonly Mock<ICurrencyService>           _mockCurrency;
    private readonly Mock<IARInvoiceService>          _mockArInvoice = new();

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public GoodsShipmentControllerTests()
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
            .ReturnsAsync("GS2026051401");

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

        _mockInvSvc.Setup(i => i.GetAvailableQtyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(999m);
        _mockInvSvc.Setup(i => i.UpdateQtyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockCurrency = GetMockCurrencyService();
    }

    private GoodsShipmentController BuildController()
    {
        var ctrl = new GoodsShipmentController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object, _mockPosting.Object, _mockInvSvc.Object,
            _mockUom.Object, _mockCurrency.Object, _mockSysParam.Object, _mockArInvoice.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        ctrl.ControllerContext.HttpContext.RequestServices = BuildServiceProvider(
            (typeof(SmartFactory.MES.Services.IPermissionService), _mockPerm.Object),
            (typeof(SmartFactory.MES.Services.IUserContext), _mockUser.Object),
            (typeof(SmartFactory.MES.Data.ApplicationDbContext), _context),
            (typeof(SmartFactory.MES.Services.IApprovalService), _mockApproval.Object),
            (typeof(Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>), GetMockLocalizer<ProjectZSharedResource>().Object));
        return ctrl;
    }

    private void SetAjax(GoodsShipmentController ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private GoodsShipment SeedDoc(string id, DocStatus status = DocStatus.Draft,
        DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var doc = new GoodsShipment
        {
            GoodsShipmentId = id,
            ShipDate        = new DateOnly(2026, 5, 14),
            CompanyCode     = "C001",
            CreatedBy       = UserId,
            PlantId         = PlantId,
            Status          = status,
            ApprovalStatus  = appr,
        };
        doc.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = id, LineNo = 1,
            ProductCode = "PRD001", ShipQty = 10, UomCode = "PC",
        });
        _context.GoodsShipments.Add(doc);
        _context.SaveChanges();
        return doc;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        SeedDoc("GS2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<GoodsShipment>>(view.Model);
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
        SeedDoc("GS2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("GS2026051401");
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
        SeedDoc("GS2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("GS2026051401");
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
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 5, UomCode = "PC" }
            }
        };
        var result = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_POST_NonDraftStatus_ReturnsFormWithError()
    {
        SeedDoc("GS2026051401", status: DocStatus.Confirmed, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            GoodsShipmentId = "GS2026051401",
            Status          = DocStatus.Confirmed,
            ShipDate        = new DateOnly(2026, 5, 14),
            CompanyCode     = "C001",
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedDoc("GS2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Submit("GS2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.GoodsShipments.Find("GS2026051401")!;
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
        SeedDoc("GS2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("GS2026051401");
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
        SeedDoc("GS2026051401", appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("GS2026051401");
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
        SeedDoc("GS2026051401", status: DocStatus.Posted, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("GS2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Confirm_POST_NotApproved_ReturnsBadRequest()
    {
        SeedDoc("GS2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("GS2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
        // 未核准即被擋下，不得產生應收發票
        _mockArInvoice.Verify(a => a.GenerateFromShipmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_POST_ApprovedDraft_SetsConfirmedStatus()
    {
        SeedDoc("GS2026051401", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("GS2026051401");
        Assert.IsType<OkObjectResult>(result);
        var doc = _context.GoodsShipments.Find("GS2026051401")!;
        Assert.Equal(DocStatus.Confirmed, doc.Status);
        // 出貨確認完成必自動產生應收發票（否則不會出現在應收帳款）
        _mockArInvoice.Verify(a => a.GenerateFromShipmentAsync("GS2026051401", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_POST_SimplifiedMode_ShouldGenerateArInvoice()
    {
        // 簡化模式：確認即過帳（Posted），同樣是一條「完成」路徑，必須產生應收發票。
        var seeded = SeedDoc("GS2026051402", status: DocStatus.Draft, appr: DocumentApprovalStatus.Approved);
        // 簡化模式過帳需明細儲位（無則回退產品類別預設，本測試直接指定）
        seeded.Lines.First().WhCode       = "WH01";
        seeded.Lines.First().LocationCode = "A-01";
        _context.SaveChanges();
        _mockSysParam.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("true");

        var ctrl = BuildController();
        var result = await ctrl.Confirm("GS2026051402");

        Assert.IsType<OkObjectResult>(result);
        _mockArInvoice.Verify(a => a.GenerateFromShipmentAsync("GS2026051402", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_POST_AlreadyPosted_ShouldNotRegenerateArInvoice()
    {
        // 已過帳單據不可重複確認，也不得重複產生應收發票。
        SeedDoc("GS2026051403", status: DocStatus.Posted, appr: DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.Confirm("GS2026051403");

        Assert.IsType<BadRequestObjectResult>(result);
        _mockArInvoice.Verify(a => a.GenerateFromShipmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_POST_NoWorkflow_ShouldStayDraft_NoArInvoice()
    {
        // 出貨單與進貨單結構不同：存檔（即使無簽核流程直接核准）不會自動完成，
        // 仍須手動 Confirm 才過帳。故存檔階段不得產生應收發票。
        _mockApproval.Setup(a => a.ResolveSaveStatusAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(DocumentApprovalStatus.Approved);

        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 5, UomCode = "PC" }
            }
        };
        await ctrl.Save(vm);

        var doc = _context.GoodsShipments.First(d => d.CompanyCode == "C001");
        Assert.Equal(DocStatus.Draft, doc.Status);
        _mockArInvoice.Verify(a => a.GenerateFromShipmentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CancelConfirm POST ────────────────────────────────────────────────
    // 已確認、揀貨尚未開始才可退回草稿；已有揀貨量就擋下，狀態維持已確認。

    [Fact]
    public async Task CancelConfirm_Confirmed_NoPutAway_ShouldSetDraft()
    {
        SeedDoc("GS2026051401", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        var result = await BuildController().CancelConfirm("GS2026051401");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocStatus.Draft, (await _context.GoodsShipments.FindAsync("GS2026051401"))!.Status);
    }

    [Fact]
    public async Task CancelConfirm_WithQtyPutAway_ShouldRejectAndKeepConfirmed()
    {
        var seeded = SeedDoc("GS2026051401", DocStatus.Confirmed, DocumentApprovalStatus.Approved);
        seeded.Lines.First().QtyPutAway = 5;
        _context.SaveChanges();
        var result = await BuildController().CancelConfirm("GS2026051401");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(DocStatus.Confirmed, (await _context.GoodsShipments.FindAsync("GS2026051401"))!.Status);
    }

    [Theory]
    [InlineData(DocStatus.Draft)]
    [InlineData(DocStatus.Posted)]
    public async Task CancelConfirm_NotConfirmed_ShouldReject(DocStatus status)
    {
        SeedDoc("GS2026051401", status, DocumentApprovalStatus.Approved);
        var result = await BuildController().CancelConfirm("GS2026051401");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(status, (await _context.GoodsShipments.FindAsync("GS2026051401"))!.Status);
    }

    // ── Void POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_POST_NotPosted_ReturnsBadRequest()
    {
        SeedDoc("GS2026051401", status: DocStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Void("GS2026051401", null);
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
        SeedDoc("GS2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Delete("GS2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.GoodsShipments.Any());
    }

    [Fact]
    public async Task Delete_POST_PendingApproval_ReturnsJsonError()
    {
        SeedDoc("GS2026051401", appr: DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("GS2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    /// <summary>
    /// 刪除出貨單回沖訂單已出貨量：正常情形減回去。
    /// </summary>
    [Fact]
    public async Task Delete_POST_ReversesSalesOrderShippedQty()
    {
        SeedDocLinkedToSalesOrder("GS2026051410", "SO-REV-01", soShippedQty: 10m);
        var ctrl = BuildController();

        await ctrl.Delete("GS2026051410");

        Assert.Equal(0m, _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-REV-01").ShippedQty);
    }

    /// <summary>
    /// 回沖有下限 0：已出貨量若已被減到 0（資料漂移或先前重複回沖），再刪一次不得變成負數。
    ///
    /// 負的已出貨量沒有任何守門會擋，而訂單的剩餘可出量＝OrderQty − ShippedQty，
    /// 會因此算得比訂購量還大，多出來的額度可以一路出貨出去。
    /// 對稱寫法見 PurchaseService.RestorePrOrderedQtyAsync 與 GoodsReceiptLine.ReturnedQty 回沖。
    /// </summary>
    [Fact]
    public async Task Delete_POST_ShippedQtyReversal_FloorsAtZero()
    {
        SeedDocLinkedToSalesOrder("GS2026051411", "SO-REV-02", soShippedQty: 0m);
        var ctrl = BuildController();

        await ctrl.Delete("GS2026051411");

        var shipped = _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-REV-02").ShippedQty;
        Assert.Equal(0m, shipped);
        Assert.True(shipped >= 0m, $"已出貨量不得為負，實際 {shipped}");
    }

    // ── 正向累加上限（累計出貨不得超過訂購量）─────────────────────────────

    /// <summary>
    /// 累計出貨超過訂購量必須被拒絕。
    ///
    /// 沒有這道守門，訂單剩餘可出量（OrderQty − ShippedQty）會變成負數，
    /// 而負的剩餘量沒有任何地方會擋——同一張訂單可以無限出貨，
    /// 且每一次出貨都照常開立應收，客戶被多開的發票沒有對應訂單可查。
    /// 採購側的 <c>PurchaseService.UpdatePoReceivedQtyAsync</c> 一開始就有這道守門，
    /// 銷售側一直沒跟上；本測試釘的就是這個對稱性。
    /// </summary>
    [Fact]
    public async Task Save_POST_ShippedQtyExceedingOrderQty_Rejected()
    {
        SeedSalesOrder("SO-CAP-01", orderQty: 100m, shippedQty: 95m);
        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                // 95 + 6 = 101 > 100
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 6, UomCode = "PC",
                        SalesOrderId = "SO-CAP-01", SoLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        Assert.Equal(95m, _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-CAP-01").ShippedQty);
    }

    /// <summary>
    /// 出貨單位與訂單單位不同時，守門必須在庫存基準單位上比對。
    ///
    /// 訂 100 PC（庫存基準亦 100 PC），改以 BOX 出貨、1 BOX = 10 PC。
    /// 出 11 BOX 換算是 110 PC，已經超過訂購量——但若照舊寫法直接比業務單位量，
    /// 看到的是「11 &gt; 100 嗎？」，答案是否，於是超出的 10 PC 靜默通過。
    /// 守門與累加當時是「一致地錯」，帳面自洽，只有實際點貨才會發現多出來。
    /// </summary>
    [Fact]
    public async Task Save_POST_ShipQtyInDifferentUom_GuardsOnStockBasis()
    {
        SeedSalesOrder("SO-CAP-03", orderQty: 100m, shippedQty: 0m);
        SeedProductWithStockUom("PRD001", "PC");
        _mockUom.Setup(u => u.ConvertAsync("PRD001", "BOX", "PC", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string __, string ___, decimal qty, CancellationToken ____) => qty * 10m);

        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                // 11 BOX = 110 PC > 100 PC。業務單位量 11 遠小於 100，舊寫法一定放行。
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 11, UomCode = "BOX",
                        SalesOrderId = "SO-CAP-03", SoLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
        var soLine = _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-CAP-03");
        Assert.Equal(0m, soLine.ShippedStockQty);
        Assert.Equal(0m, soLine.ShippedQty);
    }

    /// <summary>
    /// 同上但出 10 BOX（= 100 PC，剛好出滿）必須放行，且兩個累計欄各自以自己的單位落帳。
    /// 沒有這一格，上一格可以靠「一律拒絕」通過。
    /// </summary>
    [Fact]
    public async Task Save_POST_ShipQtyInDifferentUom_ExactlyOrderQty_Allowed()
    {
        SeedSalesOrder("SO-CAP-04", orderQty: 100m, shippedQty: 0m);
        SeedProductWithStockUom("PRD001", "PC");
        _mockUom.Setup(u => u.ConvertAsync("PRD001", "BOX", "PC", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string __, string ___, decimal qty, CancellationToken ____) => qty * 10m);
        _mockUom.Setup(u => u.ConvertAsync("PRD001", "PC", "PC", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string __, string ___, decimal qty, CancellationToken ____) => qty);

        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 10, UomCode = "BOX",
                        SalesOrderId = "SO-CAP-04", SoLineNo = 1 }
            }
        };

        await ctrl.Save(vm);

        var soLine = _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-CAP-04");
        Assert.Equal(100m, soLine.ShippedStockQty);   // 庫存基準單位（PC）
        Assert.Equal(100m, soLine.ShippedQty);        // 訂單行單位（PC），非出貨單的 10 BOX
    }

    /// <summary>
    /// 邊界：剛好出滿訂購量必須放行（守門是 <c>&gt;</c> 不是 <c>&gt;=</c>）。
    /// 這條是上一個測試的另一半——守得太緊會讓最後一批貨永遠出不掉。
    /// </summary>
    [Fact]
    public async Task Save_POST_ShippedQtyExactlyOrderQty_Allowed()
    {
        SeedSalesOrder("SO-CAP-02", orderQty: 100m, shippedQty: 95m);
        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            ShipDate    = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<GoodsShipmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 5, UomCode = "PC",
                        SalesOrderId = "SO-CAP-02", SoLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
        Assert.Equal(100m, _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-CAP-02").ShippedQty);
    }

    /// <summary>
    /// 修改草稿時，守門比對的是「扣掉本單舊值之後」的餘額——本單自己出過的量
    /// 不能被算成別人的額度。改單前先 ReverseShippedQtyAsync 再累加，
    /// 若順序或下限寫錯，這裡會變成把自己的 10 當成已出量而誤擋。
    /// </summary>
    [Fact]
    public async Task Save_POST_EditDraft_DoesNotCountItsOwnQtyTwice()
    {
        // 訂購 100、已出 100（全部來自本單的 10 加上他單的 90）。
        SeedDocLinkedToSalesOrder("GS2026051412", "SO-CAP-03", soShippedQty: 100m);
        var ctrl = BuildController();
        var vm = new GoodsShipmentFormVm
        {
            GoodsShipmentId = "GS2026051412",
            ShipDate        = new DateOnly(2026, 5, 14),
            CompanyCode     = "C001",
            Lines           = new List<GoodsShipmentLineVm>
            {
                // 本單由 10 改成 10（等量改單）：100 − 10 + 10 = 100，不得被擋。
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 10, UomCode = "PC",
                        SalesOrderId = "SO-CAP-03", SoLineNo = 1 }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
        Assert.Equal(100m, _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-CAP-03").ShippedQty);
    }

    /// <summary>銷貨訂單明細一行（單價 5、單位 PC），訂購量與已出貨量由參數指定。</summary>
    /// <summary>
    /// 出貨明細的庫存單位一律取自品號主檔（ApplyLinesAsync 不信任前端傳入的 StockUomCode）。
    /// 沒有這筆主檔，換算就退回「以出貨單位為庫存單位」，混單位的情境根本組不出來。
    /// </summary>
    private void SeedProductWithStockUom(string productCode, string stockUom)
    {
        if (_context.Products.Any(p => p.ProductCode == productCode)) return;
        _context.Products.Add(new Product
        {
            ProductCode = productCode, ProductName = productCode, StockUom = stockUom,
        });
        _context.SaveChanges();
    }

    private void SeedSalesOrder(string soId, decimal orderQty, decimal shippedQty)
    {
        var so = new SalesOrder
        {
            SalesOrderId = soId, OrderDate = new DateOnly(2026, 5, 14),
            CompanyCode = "C001", CreatedBy = UserId, PlantId = PlantId,
            ApprovalStatus = DocumentApprovalStatus.Approved,
        };
        so.Lines.Add(new SalesOrderLine
        {
            SalesOrderId = soId, LineNo = 1, ProductCode = "PRD001",
            OrderQty = orderQty, StockQty = orderQty, UomCode = "PC", StockUomCode = "PC",
            UnitPrice = 5m, ShippedQty = shippedQty,
        });
        _context.SalesOrders.Add(so);
        _context.SaveChanges();
    }

    // ── 修改後重複累加回歸（R16 動態面）───────────────────────────────────

    /// <summary>
    /// 修改草稿後，訂單已出貨量必須剛好等於這次送出的明細合計。
    ///
    /// 出貨單的 Save 不過帳（過帳在 Confirm，屆時單據已重新載入），
    /// 所以 <c>RemoveRange</c> 殘留在這張單上不是變成重複過帳，而是**重複累加上游欄位**——
    /// 同一請求內 <c>ReverseShippedQtyAsync</c> 扣掉舊值後，
    /// <c>UpdateSalesOrderShippedQtyAsync</c> 會走過 doc.Lines 再加回去；
    /// 若殘留舊行還在集合裡，舊行的量會被再加一次，訂單已出貨量憑空膨脹。
    /// 這種殘留完全不留庫存異動與傳票，任何以單號查過帳足跡的檢查都看不見它。
    ///
    /// 規則 R16 只保證「RemoveRange 後有 Clear 或換掉集合」，看不出這種副作用是否吃到舊集合。
    /// </summary>
    [Fact]
    public async Task Save_POST_EditReplacesLines_DoesNotReAccumulateStaleLines()
    {
        // 訂購 100、已出 10（全部來自本單那一行）。
        SeedDocLinkedToSalesOrder("GS2026051413", "SO-DUP-01", soShippedQty: 10m);
        var ctrl = BuildController();

        // 第 1 行由 10 改成 4，另外加一行 7：本單新合計 11。
        var vm = new GoodsShipmentFormVm
        {
            GoodsShipmentId = "GS2026051413",
            ShipDate        = new DateOnly(2026, 5, 14),
            CompanyCode     = "C001",
            Lines           = new List<GoodsShipmentLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", ShipQty = 4, UomCode = "PC",
                        SalesOrderId = "SO-DUP-01", SoLineNo = 1 },
                new() { LineNo = 2, ProductCode = "PRD001", ShipQty = 7, UomCode = "PC",
                        SalesOrderId = "SO-DUP-01", SoLineNo = 1 },
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<ContentResult>(result);
        // 10 − 10 + 11 = 11。殘留舊行的話會是 21（舊的 10 被再加一次）。
        Assert.Equal(11m, _context.SalesOrderLines.First(l => l.SalesOrderId == "SO-DUP-01").ShippedQty);
        // 單據本身也只該留下兩行。
        var lines = _context.GoodsShipmentLines.Where(l => l.GoodsShipmentId == "GS2026051413").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(11m, lines.Sum(l => l.ShipQty));
    }

    /// <summary>草稿出貨單 + 已連結的銷貨訂單明細（訂購 100、已出貨量由參數指定）。</summary>
    private void SeedDocLinkedToSalesOrder(string id, string soId, decimal soShippedQty)
    {
        SeedSalesOrder(soId, orderQty: 100m, shippedQty: soShippedQty);

        var doc = SeedDoc(id);
        var line = doc.Lines.First();
        line.SalesOrderId = soId;
        line.SoLineNo     = 1;
        _context.SaveChanges();
    }
}
