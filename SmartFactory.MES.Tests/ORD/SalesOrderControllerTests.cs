using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.ORD.Controllers;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.ORD;

public class SalesOrderControllerTests : TestBase
{
    private readonly Mock<IUserContext>                  _mockUser        = new();
    private readonly Mock<IPermissionService>            _mockPerm        = new();
    private readonly Mock<IDateTimeService>              _mockDt          = new();
    private readonly Mock<IApprovalService>              _mockApproval    = new();
    private readonly Mock<IDocNumberService>             _mockDocNo       = new();
    private readonly Mock<IUomService>                   _mockUom         = new();
    private readonly Mock<IInventoryReservationService>  _mockReservations = new();
    private readonly Mock<ICurrencyService>              _mockCurrency;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public SalesOrderControllerTests()
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

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());
        _mockUom.Setup(u => u.RoundQtyAsync(It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, decimal qty, CancellationToken __) => qty);

        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SO2026051401");

        _mockCurrency = GetMockCurrencyService();
    }

    private SalesOrderController BuildController()
    {
        _mockReservations.Setup(r => r.ReleaseBySourceLineAsync(
                It.IsAny<SmartFactory.MES.Modules.INV.Models.ReservationSourceType>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var ctrl = new SalesOrderController(
            _context, _mockUser.Object, _mockPerm.Object, _mockDt.Object,
            _mockApproval.Object, _mockDocNo.Object,
            _mockUom.Object, _mockCurrency.Object, _mockReservations.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object,
            // §25.5 自動展開：預設不觸發（＝簡化模式關閉），使既有斷言維持原本行為。
            // 必須明確 Setup——Moq 對 Task<record> 的預設值是 null，直接取 .Triggered 會 NRE。
            NoAutoExpand());
        ctrl.ControllerContext = GetControllerContext(userId: UserId);
        ctrl.ControllerContext.HttpContext.RequestServices = BuildServiceProvider(
            (typeof(SmartFactory.MES.Services.IPermissionService), _mockPerm.Object),
            (typeof(SmartFactory.MES.Services.IUserContext), _mockUser.Object),
            (typeof(SmartFactory.MES.Data.ApplicationDbContext), _context),
            (typeof(SmartFactory.MES.Services.IApprovalService), _mockApproval.Object),
            (typeof(Microsoft.Extensions.Localization.IStringLocalizer<ProjectZSharedResource>), GetMockLocalizer<ProjectZSharedResource>().Object));
        return ctrl;
    }

    private void SetAjax(SalesOrderController ctrl) =>
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private SalesOrder SeedOrder(string id, DocumentApprovalStatus appr = DocumentApprovalStatus.Draft)
    {
        var so = new SalesOrder
        {
            SalesOrderId   = id,
            OrderDate      = new DateOnly(2026, 5, 14),
            CompanyCode    = "C001",
            CreatedBy      = UserId,
            PlantId        = PlantId,
            ApprovalStatus = appr,
        };
        so.Lines.Add(new SalesOrderLine
        {
            SalesOrderId = id, LineNo = 1,
            ProductCode = "PRD001", OrderQty = 10, UomCode = "PC",
        });
        _context.SalesOrders.Add(so);
        _context.SaveChanges();
        return so;
    }

    // ── Index ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Searched_ReturnsPaginatedList()
    {
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Index(null, null, null, null, null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<PaginatedList<SalesOrder>>(view.Model);
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
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("SO2026051401");
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Edit_GET_LoadsLines()
    {
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Edit("SO2026051401");
        var view = Assert.IsType<PartialViewResult>(result);
        var vm   = Assert.IsType<SalesOrderFormVm>(view.Model);
        Assert.Single(vm.Lines);
        Assert.Equal("PRD001", vm.Lines[0].ProductCode);
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
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        SetAjax(ctrl);
        var result = await ctrl.Details("SO2026051401");
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
        var vm = new SalesOrderFormVm
        {
            OrderDate   = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<SalesOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", OrderQty = 5, UomCode = "PC" }
            }
        };
        var result = await ctrl.Save(vm);
        Assert.IsType<ContentResult>(result);
    }

    [Fact]
    public async Task Save_POST_ServiceThrows_ReturnsFormWithError()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var vm = new SalesOrderFormVm
        {
            SalesOrderId   = "SO2026051401",
            ApprovalStatus = DocumentApprovalStatus.PendingApproval,
            OrderDate      = new DateOnly(2026, 5, 14),
            CompanyCode    = "C001",
        };
        var result = await ctrl.Save(vm);
        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.False(ctrl.ModelState.IsValid);
    }

    // 訂單在 controller 內直接換算（ResolveLineStockQtyAsync），不經 service——
    // 與採購那條是不同的路徑，缺換算時同樣要回到面板而不是 500。
    [Fact]
    public async Task Save_POST_UomConversionMissing_ReturnsFormWithMessage()
    {
        var ex = new UomConversionNotFoundException("PRD001", "KG", "PC");
        _mockUom.Setup(u => u.ConvertAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        var ctrl = BuildController();
        var vm = new SalesOrderFormVm
        {
            OrderDate   = new DateOnly(2026, 5, 14),
            CompanyCode = "C001",
            Lines       = new List<SalesOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "PRD001", OrderQty = 5, UomCode = "KG", StockUomCode = "PC" }
            }
        };

        var result = await ctrl.Save(vm);

        Assert.IsType<PartialViewResult>(result);
        var error = Assert.Single(ctrl.ModelState[string.Empty]!.Errors);
        Assert.Equal(ex.Message, error.ErrorMessage);
    }

    // ── Submit POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_POST_NoApprovalConfig_ApprovesDirectly()
    {
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Submit("SO2026051401");
        Assert.IsType<OkObjectResult>(result);
        var so = _context.SalesOrders.Find("SO2026051401")!;
        Assert.Equal(DocumentApprovalStatus.Approved, so.ApprovalStatus);
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
        SeedOrder("SO2026051401", DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Submit("SO2026051401");
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
        SeedOrder("SO2026051401", DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.Withdraw("SO2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_ExistingRecord_ReturnsJsonSuccess()
    {
        SeedOrder("SO2026051401");
        var ctrl = BuildController();
        var result = await ctrl.Delete("SO2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.True((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.False(_context.SalesOrders.Any());
    }

    [Fact]
    public async Task Delete_POST_PendingApproval_ReturnsJsonError()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.Delete("SO2026051401");
        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
    }

    [Fact]
    public async Task Delete_POST_ReferencedByMrpRun_ReturnsJsonError()
    {
        // 已納入 MRP 執行單的訂單不可刪除（否則需求列懸空、展開失去需求來源）
        SeedOrder("SO2026051401", DocumentApprovalStatus.Approved);
        _context.MrpRuns.Add(new SmartFactory.MES.Modules.MFG.Models.MrpRun
        {
            MrpRunId = "MRP-0001", PlantId = PlantId,
            Status = SmartFactory.MES.Modules.MFG.Models.MrpRunStatus.Executed,
            Lines = { new SmartFactory.MES.Modules.MFG.Models.MrpRunLine
            {
                MrpRunId = "MRP-0001", LineSeq = 1, SalesOrderId = "SO2026051401",
                SalesOrderLineSeq = 1, ProductCode = "PRD001", RequiredQty = 10,
                RequiredDate = DateTime.UtcNow,
            } },
        });
        _context.SaveChanges();

        var ctrl = BuildController();
        var result = await ctrl.Delete("SO2026051401");

        var json = Assert.IsType<JsonResult>(result);
        Assert.False((bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!);
        Assert.True(_context.SalesOrders.Any());   // 未被刪除
    }

    // ── CloseLine / ReopenLine / CloseDoc（僅核准後可結案）───────────────────

    [Fact]
    public async Task CloseLine_POST_NotApproved_ReturnsBadRequestAndDoesNotClose()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.CloseLine("SO2026051401", 1);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.SalesOrders.Include(s => s.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseLine_POST_Approved_ClosesLine()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CloseLine("SO2026051401", 1);
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.SalesOrders.Include(s => s.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task ReopenLine_POST_NotApproved_ReturnsBadRequest()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.Draft);
        var ctrl = BuildController();
        var result = await ctrl.ReopenLine("SO2026051401", 1);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CloseDoc_POST_NotApproved_ReturnsBadRequestAndDoesNotCloseAny()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.PendingApproval);
        var ctrl = BuildController();
        var result = await ctrl.CloseDoc("SO2026051401");
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.SalesOrders.Include(s => s.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseDoc_POST_Approved_ClosesAllLines()
    {
        SeedOrder("SO2026051401", DocumentApprovalStatus.Approved);
        var ctrl = BuildController();
        var result = await ctrl.CloseDoc("SO2026051401");
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.SalesOrders.Include(s => s.Lines).First().Lines.Single().IsClosed);
    }
}
