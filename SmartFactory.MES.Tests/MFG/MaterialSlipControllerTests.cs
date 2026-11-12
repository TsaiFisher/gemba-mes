using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// MaterialSlipController 狀態機整合測試（mfg-domain.md §17.3）。
/// 涵蓋：Submit / Approve / MarkPicking / Confirm / Issue / Cancel 六個動作，
/// 以及 Issue 後 PostDocSimplifiedAsync 呼叫驗證與 WorkOrderMaterial 回寫。
/// </summary>
public class MaterialSlipControllerTests : TestBase
{
    private readonly Mock<IDocNumberService>        _mockDocNo   = new();
    private readonly Mock<IInventoryPostingService> _mockPosting = new();
    private readonly Mock<IInventoryReservationService> _mockReservations = new();
    private readonly Mock<IUserContext>             _mockUser    = new();
    private readonly Mock<IPermissionService>       _mockPerm    = new();

    private const string PlantId = "P001";
    private const string UserId  = "usr1";
    private const string SlipId  = "MI2026001";
    private const string WoId    = "WO2026001";

    public MaterialSlipControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        // 過帳 mock：執行 SetDocPosted callback（令 slip.Status = Posted）
        _mockPosting
            .Setup(p => p.PostDocSimplifiedAsync(
                It.IsAny<PostDocSimplifiedRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => r.SetDocPosted())
            .Returns(Task.CompletedTask);
    }

    private MaterialSlipController BuildController()
    {
        // 非 AJAX 開表單時會 RedirectToAction 導回列表，而 ControllerBase.RedirectToAction
        // 會取 Url → 需要 IUrlHelperFactory，且 UrlHelper 的建構子會讀 ActionContext.RouteData。
        var ctx = GetControllerContextWithServices(
            userId: UserId,
            userContext: _mockUser.Object,
            permService: _mockPerm.Object,
            configureServices: sc => sc.AddSingleton<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory,
                                                     Microsoft.AspNetCore.Mvc.Routing.UrlHelperFactory>());
        ctx.RouteData = new Microsoft.AspNetCore.Routing.RouteData();

        return new MaterialSlipController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            _mockDocNo.Object,
            _mockPosting.Object,
            _mockReservations.Object,
            Timezone.TimezoneTestKit.FixedLocalDate(new DateOnly(2026, 7, 1)).Object)
        {
            ControllerContext = ctx,
        };
    }

    private MaterialSlip SeedSlip(
        string id,
        MaterialSlipStatus status,
        MaterialSlipCategory category = MaterialSlipCategory.InternalIssue,
        string? sourceDocId = null,
        bool withLine = true)
    {
        var slip = new MaterialSlip
        {
            SlipId        = id,
            SlipNo        = id,
            Category      = category,
            Status        = status,
            SlipDate      = new DateOnly(2026, 6, 17),
            SourceDocType = sourceDocId != null ? "WorkOrder" : "",
            SourceDocId   = sourceDocId ?? "",
            PlantId       = PlantId,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };
        if (withLine)
        {
            slip.Lines.Add(new MaterialSlipLine
            {
                SlipId       = id,
                LineNo       = 1,
                ProductCode  = "MAT001",
                RequestedQty = 5m,
                UomCode      = "PC",
                SourceLineId = "10_1",   // OperationSeq=10, BomSeq=1
                // 過帳前守門：發料類要有來源倉/儲位、退料類要有退回倉/儲位
                FromWarehouseCode = "W01", FromLocationCode = "W01-A01",
                ToWarehouseCode   = "W01", ToLocationCode   = "W01-A01",
            });
        }
        _context.MaterialSlips.Add(slip);
        _context.SaveChanges();
        return slip;
    }

    private void SeedWorkOrderWithMaterial(string woId, int opSeq = 10, int bomSeq = 1)
    {
        var op = new WorkOrderOperation
        {
            WorkOrderId  = woId,
            OperationSeq = opSeq,
            OperationName = "組裝",
            WorkCenterId = "WC001",
        };
        op.Materials.Add(new WorkOrderMaterial
        {
            WorkOrderId  = woId,
            OperationSeq = opSeq,
            BomSeq       = bomSeq,
            ProductCode  = "MAT001",
            RequiredQty  = 5m,
            UomCode      = "PC",
            IssueMethod  = MaterialIssueMethod.Push,
        });
        var wo = new WorkOrder
        {
            WorkOrderId      = woId,
            ProductCode      = "FG001",
            Quantity         = 10m,
            PlantId          = PlantId,
            Status           = WorkOrderStatus.Released,
            ApprovalStatus   = DocumentApprovalStatus.Approved,
            PlannedStartDate = DateTime.UtcNow,
            PlannedEndDate   = DateTime.UtcNow.AddDays(7),
        };
        wo.Operations.Add(op);
        _context.WorkOrders.Add(wo);
        _context.SaveChanges();
    }

    private static bool IsSuccess(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        return (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
    }

    // ── Save（建單）────────────────────────────────────────────────────────

    // 迴歸：手動「新增備料/領料單」面板存檔曾靜默失敗、永遠建不出單。根因是 Save 直接綁
    // MaterialSlip entity，其表頭 SlipNo 與明細 SlipId 皆 [Required] 但由伺服器端存檔時才產生、
    // Create 面板不渲染也不送出 → 真實 model binding 為這些欄位補上 [Required] 錯誤 →
    // ModelState 恆 invalid → 表單重繪、無任何資料落地。
    // 既有測試都直接 SaveChanges 塞好 SlipNo/SlipId、ModelState 預設 valid，故從未觸及此縫。
    // 這裡刻意模擬 binder 對未提交伺服器欄位加的錯誤，斷言 Save 仍建出單（驗 ModelState.Remove）。
    [Fact]
    public async Task Save_NewSlip_ServerGeneratedRequiredFieldsMissing_StillCreates()
    {
        _mockDocNo
            .Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("MI2026099");

        var controller = BuildController();

        var model = new MaterialSlip
        {
            Category      = MaterialSlipCategory.InternalIssue,
            SlipDate      = new DateOnly(2026, 6, 17),
            SourceDocType = "Manual",   // 非 WorkOrder，避開來源工單核准檢查
            SourceDocId   = "M-TEST",
        };
        model.Lines.Add(new MaterialSlipLine
        {
            ProductCode  = "MAT001",
            RequestedQty = 3m,
            UomCode      = "PC",
        });

        // 模擬真實 model binding 對未提交的伺服器產生 [Required] 欄位補的錯誤
        controller.ModelState.AddModelError(nameof(MaterialSlip.SlipNo), "Required");
        controller.ModelState.AddModelError("Lines[0].SlipId", "Required");

        await controller.Save(model);

        var created = _context.MaterialSlips.Include(s => s.Lines)
            .FirstOrDefault(s => s.SlipId == "MI2026099");
        Assert.NotNull(created);                                   // 單真的落地
        Assert.Equal(MaterialSlipStatus.Draft, created!.Status);
        Assert.Equal("MI2026099", created.SlipNo);                 // 伺服器回填採番
        Assert.Single(created.Lines);
        Assert.Equal("MI2026099", created.Lines[0].SlipId);        // 明細 SlipId 伺服器回填
    }

    // ── Submit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_InternalRequisition_Draft_TransitionsToPending()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Draft, MaterialSlipCategory.InternalRequisition);
        var ctrl = BuildController();

        var result = await ctrl.Submit(SlipId);

        Assert.True(IsSuccess(result));
        var slip = _context.MaterialSlips.Find(SlipId)!;
        Assert.Equal(MaterialSlipStatus.Pending, slip.Status);
    }

    [Fact]
    public async Task Submit_NonRequisitionCategory_Fails()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Draft, MaterialSlipCategory.InternalIssue);
        var ctrl = BuildController();

        var result = await ctrl.Submit(SlipId);

        Assert.False(IsSuccess(result));
        var slip = _context.MaterialSlips.Find(SlipId)!;
        Assert.Equal(MaterialSlipStatus.Draft, slip.Status); // 未改變
    }

    // ── Approve ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_InternalRequisition_Pending_TransitionsToApproved()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Pending, MaterialSlipCategory.InternalRequisition);
        var ctrl = BuildController();

        var result = await ctrl.Approve(SlipId);

        Assert.True(IsSuccess(result));
        var slip = _context.MaterialSlips.Find(SlipId)!;
        Assert.Equal(MaterialSlipStatus.Approved, slip.Status);
        Assert.Equal(UserId, slip.ApprovedBy);
    }

    [Fact]
    public async Task Approve_InternalIssue_Draft_TransitionsToApproved()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Draft, MaterialSlipCategory.InternalIssue);
        var ctrl = BuildController();

        var result = await ctrl.Approve(SlipId);

        Assert.True(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Approved, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Approve_InternalRequisition_Draft_Fails_MustSubmitFirst()
    {
        // MR 類必須先 Submit(→Pending) 才能 Approve
        SeedSlip(SlipId, MaterialSlipStatus.Draft, MaterialSlipCategory.InternalRequisition);
        var ctrl = BuildController();

        var result = await ctrl.Approve(SlipId);

        Assert.False(IsSuccess(result));
    }

    // ── MarkPicking ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkPicking_Approved_TransitionsToPicking()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Approved);
        var ctrl = BuildController();

        var result = await ctrl.MarkPicking(SlipId);

        Assert.True(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Picking, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task MarkPicking_Draft_Fails()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Draft);
        var ctrl = BuildController();

        var result = await ctrl.MarkPicking(SlipId);

        Assert.False(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Draft, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    // ── Confirm ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Picking_TransitionsToConfirmed_BackfillsConfirmedQty()
    {
        var slip = SeedSlip(SlipId, MaterialSlipStatus.Picking);
        // 給 ApprovedQty（應回填到 ConfirmedQty）
        slip.Lines.First().ApprovedQty = 4m;
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Confirm(SlipId);

        Assert.True(IsSuccess(result));
        var updated = _context.MaterialSlips.Include(s => s.Lines).First(s => s.SlipId == SlipId);
        Assert.Equal(MaterialSlipStatus.Confirmed, updated.Status);
        Assert.Equal(4m, updated.Lines.First().ConfirmedQty); // ApprovedQty 優先
    }

    [Fact]
    public async Task Confirm_Picking_NoApprovedQty_UsesRequestedQty()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Picking); // RequestedQty = 5m, ApprovedQty = null

        var ctrl = BuildController();
        await ctrl.Confirm(SlipId);

        var updated = _context.MaterialSlips.Include(s => s.Lines).First(s => s.SlipId == SlipId);
        Assert.Equal(5m, updated.Lines.First().ConfirmedQty);
    }

    // ── Issue ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_發料行沒有來源倉儲位_擋下且不過帳()
    {
        // 原本空值一路帶進過帳、在庫存服務深處才失敗，訊息指不出是哪一行。
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed);
        var line = _context.MaterialSlipLines.First(l => l.SlipId == SlipId);
        line.FromLocationCode = null;
        _context.SaveChanges();

        var result = await BuildController().Issue(SlipId);

        Assert.False(IsSuccess(result));
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(MaterialSlipStatus.Confirmed, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Issue_退料行沒有退回倉儲位_擋下()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, MaterialSlipCategory.InternalReturn);
        var line = _context.MaterialSlipLines.First(l => l.SlipId == SlipId);
        line.ToLocationCode = null;
        line.FromLocationCode = null;
        _context.SaveChanges();

        Assert.False(IsSuccess(await BuildController().Issue(SlipId)));
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Issue_Confirmed_CallsPostingService_StatusBecomesPosted()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed);
        var ctrl = BuildController();

        var result = await ctrl.Issue(SlipId);

        Assert.True(IsSuccess(result));
        _mockPosting.Verify(p =>
            p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(MaterialSlipStatus.Posted, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Issue_Confirmed_WithWorkOrder_BackfillsIssuedQty()
    {
        SeedWorkOrderWithMaterial(WoId, opSeq: 10, bomSeq: 1);
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, sourceDocId: WoId);
        // 設定 ConfirmedQty（Issue 優先使用 ConfirmedQty）
        var slipLine = _context.MaterialSlipLines.First(l => l.SlipId == SlipId);
        slipLine.ConfirmedQty = 3m;
        _context.SaveChanges();

        var ctrl = BuildController();
        await ctrl.Issue(SlipId);

        var wom = await _context.WorkOrderMaterials.FindAsync(WoId, 10, 1);
        Assert.Equal(3m, wom!.IssuedQty);
    }

    [Fact]
    public async Task Issue_NotConfirmed_Fails()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Picking);
        var ctrl = BuildController();

        var result = await ctrl.Issue(SlipId);

        Assert.False(IsSuccess(result));
        _mockPosting.Verify(p =>
            p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Issue_TypeCode_InternalIssue_UsesIO()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, MaterialSlipCategory.InternalIssue);
        var ctrl = BuildController();
        PostDocSimplifiedRequest? captured = null;
        _mockPosting
            .Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => { captured = r; r.SetDocPosted(); })
            .Returns(Task.CompletedTask);

        await ctrl.Issue(SlipId);

        Assert.NotNull(captured);
        Assert.Equal("IO", captured!.TypeCode);
        Assert.Equal("Production", captured.TypeCategory);
    }

    [Fact]
    public async Task Issue_TypeCode_InternalReturn_UsesIR()
    {
        // 退料要能決定「發料當下的成本」，故先有一張已過帳的發料單（見下一支測試）
        SeedPostedIssue("WO-RTN", "MI-PRIOR", qty: 10m, unitCost: 12m);
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, MaterialSlipCategory.InternalReturn,
                 sourceDocId: "WO-RTN");
        var ctrl = BuildController();
        PostDocSimplifiedRequest? captured = null;
        _mockPosting
            .Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => { captured = r; r.SetDocPosted(); })
            .Returns(Task.CompletedTask);

        await ctrl.Issue(SlipId);

        Assert.Equal("IR", captured!.TypeCode);
        // 退料必須帶單價，否則入庫端無從建立成本層（FIFO 會直接擋下、移動平均會悄悄取錯值）
        Assert.Equal(12m, Assert.Single(captured.Lines).UnitPrice);
    }

    /// <summary>
    /// 退料的單位成本取「發料當下」凍結在庫存異動上的成本，同品號多次發料取加權平均。
    ///
    /// 取現在的庫存價是錯的：料在廠內放了一陣子、期間又進了幾批新價的貨，
    /// 「現在的價」跟「當初領走的那筆錢」是兩回事，用錯的話工單會被扣一個
    /// 它從來沒有付過的金額，而且沒有任何一張傳票會不平衡（生產類異動不入總帳）。
    /// </summary>
    [Fact]
    public async Task Issue_Return_CarriesWeightedAverageOfPriorIssueCosts()
    {
        SeedPostedIssue("WO-RTN", "MI-A", qty: 8m,  unitCost: 300m);   // 2,400
        SeedPostedIssue("WO-RTN", "MI-B", qty: 4m,  unitCost: 250m);   // 1,000
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, MaterialSlipCategory.InternalReturn,
                 sourceDocId: "WO-RTN");
        var ctrl = BuildController();
        PostDocSimplifiedRequest? captured = null;
        _mockPosting
            .Setup(p => p.PostDocSimplifiedAsync(It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocSimplifiedRequest, CancellationToken>((r, _) => { captured = r; r.SetDocPosted(); })
            .Returns(Task.CompletedTask);

        await ctrl.Issue(SlipId);

        Assert.Equal(3400m / 12m, Assert.Single(captured!.Lines).UnitPrice);
    }

    /// <summary>
    /// 查不到任何已過帳發料時，退料應被擋下而不是照樣過帳。
    ///
    /// 修正前是不帶單價就送出去，於是 FIFO 入庫落到 ConsumeLayersAsync：
    /// 對一筆**入庫**去扣成本層，數量加回來、成本層被扣光，
    /// 之後這支料每一次領用都以 0 元計價（人工驗收 W2-F8）。猜一個成本比擋下來更糟。
    /// </summary>
    [Fact]
    public async Task Issue_Return_WithoutPriorIssue_IsRejected_AndNothingIsPosted()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed, MaterialSlipCategory.InternalReturn,
                 sourceDocId: "WO-RTN");
        var ctrl = BuildController();

        var result = await ctrl.Issue(SlipId);

        Assert.False(IsSuccess(result));
        _mockPosting.Verify(p => p.PostDocSimplifiedAsync(
            It.IsAny<PostDocSimplifiedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(MaterialSlipStatus.Confirmed,
                     _context.MaterialSlips.Single(s => s.SlipId == SlipId).Status);
    }

    /// <summary>一張已過帳的發料單＋對應的出庫異動（退料成本的唯一來源）。</summary>
    private void SeedPostedIssue(string woId, string slipId, decimal qty, decimal unitCost)
    {
        _context.MaterialSlips.Add(new MaterialSlip
        {
            SlipId = slipId, SlipNo = slipId, Category = MaterialSlipCategory.InternalIssue,
            Status = MaterialSlipStatus.Posted, SlipDate = new DateOnly(2026, 6, 16),
            SourceDocType = "WorkOrder", SourceDocId = woId, PlantId = PlantId,
        });
        _context.StockMovements.Add(new SmartFactory.MES.Modules.INV.Models.StockMovement
        {
            MovementId = Guid.NewGuid(), TypeCode = "IO", TypeCategory = "Production",
            DocNo = slipId, DocLineNo = 1, MovementDate = new DateOnly(2026, 6, 16),
            PlantId = PlantId, ProductCode = "MAT001", LocationCode = "L1", LotNo = "",
            UomCode = "PC", Qty = qty, StockUomCode = "PC", StockQty = qty,
            Direction = -1, UnitCost = unitCost, TotalCost = qty * unitCost,
        });
        _context.SaveChanges();
    }

    // ── Form 入口 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 直接開網址（非 AJAX）要導回列表，不能把這張表單當成獨立頁面給人用。
    ///
    /// 表單上的核准／開始揀料／確認揀料／過帳都是原生 POST，靠面板腳本攔成 AJAX 才會有
    /// toast 與錯誤訊息。獨立開啟時腳本沒載入，按下去會整頁導到一段
    /// {"success":...} 原始 JSON——單據狀態已經變了，畫面上卻什麼都沒說（W2-F3）。
    /// </summary>
    [Fact]
    public async Task Form_NonAjax_RedirectsToIndex_InsteadOfRenderingStandalonePage()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed);

        var result = await BuildController().Form(SlipId);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MaterialSlipController.Index), redirect.ActionName);
        Assert.Equal(SlipId, redirect.RouteValues!["searchText"]);
    }

    /// <summary>面板（AJAX）走的是 partial，非草稿階段同樣要開得起來——列表的檢視入口靠它。</summary>
    [Fact]
    public async Task Form_Ajax_ReturnsPartial_ForNonDraftSlip()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed);
        var ctrl = BuildController();
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        var result = await ctrl.Form(SlipId);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_Form", partial.ViewName);
    }

    // ── Cancel ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_Draft_TransitionsToCancelled()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Draft);
        var ctrl = BuildController();

        var result = await ctrl.Cancel(SlipId);

        Assert.True(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Cancelled, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Cancel_Picking_TransitionsToCancelled()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Picking);
        var ctrl = BuildController();

        var result = await ctrl.Cancel(SlipId);

        Assert.True(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Cancelled, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Cancel_Posted_Fails()
    {
        SeedSlip(SlipId, MaterialSlipStatus.Posted);
        var ctrl = BuildController();

        var result = await ctrl.Cancel(SlipId);

        Assert.False(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Posted, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Fact]
    public async Task Cancel_Confirmed_TransitionsToCancelled()
    {
        // A6：確認只改狀態、無庫存異動，可取消（揀出的料由倉庫放回）
        SeedSlip(SlipId, MaterialSlipStatus.Confirmed);
        var ctrl = BuildController();

        var result = await ctrl.Cancel(SlipId);

        Assert.True(IsSuccess(result));
        Assert.Equal(MaterialSlipStatus.Cancelled, _context.MaterialSlips.Find(SlipId)!.Status);
    }

    [Theory]
    [InlineData(MaterialSlipStatus.Closed)]
    [InlineData(MaterialSlipStatus.Cancelled)]
    public async Task Cancel_ClosedOrCancelled_Fails(MaterialSlipStatus status)
    {
        SeedSlip(SlipId, status);

        Assert.False(IsSuccess(await BuildController().Cancel(SlipId)));
        Assert.Equal(status, _context.MaterialSlips.Find(SlipId)!.Status);
    }
}
