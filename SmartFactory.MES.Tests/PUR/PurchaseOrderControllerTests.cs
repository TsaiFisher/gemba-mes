using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;
using SmartFactory.MES;

namespace SmartFactory.MES.Tests.PUR;

public class PurchaseOrderControllerTests : TestBase
{
    private readonly Mock<IPurchaseService>  _mockPurchaseSvc = new();
    private readonly Mock<IUserContext>      _mockUser        = new();
    private readonly Mock<IPermissionService> _mockPerm       = new();
    private readonly Mock<IDateTimeService>  _mockDt          = new();
    private readonly Mock<IApprovalService>  _mockApproval    = new();
    private readonly Mock<IUomService>       _mockUom         = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private readonly PurchaseOrderController _controller;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public PurchaseOrderControllerTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);

        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);

        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(new DateOnly(2025, 1, 1));

        _mockApproval.Setup(a => a.GetApprovalConfig(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((DocumentApprovalConfig?)null);

        _mockUom.Setup(u => u.GetAllDecimalPlacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, int>)new Dictionary<string, int>());

        _mockCurrency = GetMockCurrencyService(defaultDp: 2, baseCurrency: "TWD");

        _controller = new PurchaseOrderController(
            _context,
            _mockPurchaseSvc.Object,
            _mockUser.Object,
            _mockPerm.Object,
            _mockDt.Object,
            _mockApproval.Object,
            _mockUom.Object,
            _mockCurrency.Object,
            GetMockLocalizer<ProjectZSharedResource>().Object);

        // Details 走 CanReadDocumentAsync（ModuleControllerBase → RequestServices），需含服務的 context
        _controller.ControllerContext = GetControllerContextWithServices(userId: UserId);
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private PurchaseOrder SeedPurchaseOrder(string id = "PO2025000001")
    {
        var po = new PurchaseOrder
        {
            PurchaseOrderId = id,
            OrderDate       = new DateOnly(2025, 1, 1),
            SupplierCode    = "SUP001",
            CreatedBy       = UserId,
            PlantId         = PlantId,
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = id, LineNo = 1,
            ProductCode = "P001",
            OrderQty    = 10, UomCode = "PC",
            UnitPrice   = 100, CurrencyCode = "TWD",
            LineAmount  = 1000, TotalAmount = 1050,
        });
        _context.PurchaseOrders.Add(po);
        _context.SaveChanges();
        return po;
    }

    // ── Create GET ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_ReturnsPartialView()
    {
        SetAjax();
        var result = await _controller.Create();
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Create_GET_SetsCurrencyDecimalsViewBag()
    {
        SetAjax();
        var result = await _controller.Create();

        var view = Assert.IsType<PartialViewResult>(result);
        var dict = view.ViewData["CurrencyDecimals"] as IReadOnlyDictionary<string, int>;
        Assert.NotNull(dict);
        Assert.True(dict.ContainsKey("TWD"));
        Assert.Equal(2, dict["TWD"]);
    }

    // ── Edit GET ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_GET_ExistingRecord_ReturnsPartialView()
    {
        SetAjax();
        SeedPurchaseOrder("PO2025000001");

        var result = await _controller.Edit("PO2025000001");
        Assert.IsType<PartialViewResult>(result);
    }

    [Fact]
    public async Task Edit_GET_ExistingRecord_SetsCurrencyDecimalsViewBag()
    {
        SetAjax();
        SeedPurchaseOrder("PO2025000001");

        var result = await _controller.Edit("PO2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        var dict = view.ViewData["CurrencyDecimals"] as IReadOnlyDictionary<string, int>;
        Assert.NotNull(dict);
        Assert.True(dict.ContainsKey("TWD"));
        Assert.Equal(2, dict["TWD"]);
    }

    [Fact]
    public async Task Edit_GET_NotFound_Returns404()
    {
        SetAjax();
        var result = await _controller.Edit("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Edit GET — value 格式化前提條件 ────────────────────────────────────
    // View 使用 CurrencyStepHelper.GetDp(currencyDecimals, line.CurrencyCode)
    // 計算 cdp，再以 line.UnitPrice.ToString("F" + cdp) 格式化 value 屬性。
    // 以下測試確認 ViewModel 中的明細金額精度：NUMERIC(18,4) 欄位讀回後
    // 應維持原始值，由 View 層負責格式化顯示（不在 Controller 截斷）。

    [Fact]
    public async Task Edit_GET_LineAmountPreservedInViewModel()
    {
        SetAjax();
        var po   = SeedPurchaseOrder("PO2025000002");
        var line = po.Lines.First();
        line.UnitPrice   = 1500.0000m;
        line.LineAmount  = 15000.0000m;
        line.TotalAmount = 15750.0000m;
        await _context.SaveChangesAsync();

        var result = await _controller.Edit("PO2025000002");

        var view = Assert.IsType<PartialViewResult>(result);
        var vm   = Assert.IsType<PurchaseOrderFormVm>(view.Model);
        Assert.Single(vm.Lines);
        Assert.Equal(1500.0000m, vm.Lines[0].UnitPrice);
        Assert.Equal(15000.0000m, vm.Lines[0].LineAmount);
        Assert.Equal(15750.0000m, vm.Lines[0].TotalAmount);
    }

    // ── Details GET ───────────────────────────────────────────────────────

    [Fact]
    public async Task Details_GET_SetsCurrencyDecimalsViewBag()
    {
        SetAjax();
        SeedPurchaseOrder("PO2025000001");

        var result = await _controller.Details("PO2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        var dict = view.ViewData["CurrencyDecimals"] as IReadOnlyDictionary<string, int>;
        Assert.NotNull(dict);
        Assert.True(dict.ContainsKey("TWD"));
    }

    [Fact]
    public async Task Details_GET_SetsIsReadOnlyTrue()
    {
        SetAjax();
        SeedPurchaseOrder("PO2025000001");

        var result = await _controller.Details("PO2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.True((bool)view.ViewData["IsReadOnly"]!);
    }

    [Fact]
    public async Task Details_GET_NotFound_Returns404()
    {
        SetAjax();
        var result = await _controller.Details("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Save POST (create) ────────────────────────────────────────────────

    [Fact]
    public async Task Save_POST_NewRecord_CallsCreateAndReturnsContent()
    {
        _mockPurchaseSvc
            .Setup(s => s.CreatePurchaseOrderAsync(
                It.IsAny<PurchaseOrderFormVm>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PurchaseOrder { PurchaseOrderId = "PO2025000001", PlantId = PlantId });

        var vm = new PurchaseOrderFormVm
        {
            OrderDate    = new DateOnly(2025, 1, 1),
            SupplierCode = "SUP001",
            Lines        = new List<PurchaseOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", OrderQty = 5, UomCode = "PC",
                        UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1,
                        LineAmount = 500, TotalAmount = 525 }
            }
        };

        var result = await _controller.Save(vm);

        Assert.IsType<ContentResult>(result);
        _mockPurchaseSvc.Verify(s => s.CreatePurchaseOrderAsync(
            It.IsAny<PurchaseOrderFormVm>(), UserId, PlantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // 單位換算缺主檔是「主檔沒建好」，訊息（品號＋兩個單位）要回到面板上讓人去補，
    // 不能變成一頁 500 或開發環境的堆疊頁。新增、修改兩條路徑都會走 BuildPoLinesAsync。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_POST_UomConversionMissing_ReturnsFormWithMessage(bool isUpdate)
    {
        var ex = new UomConversionNotFoundException("P001", "KG", "g");
        _mockPurchaseSvc
            .Setup(s => s.CreatePurchaseOrderAsync(
                It.IsAny<PurchaseOrderFormVm>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        _mockPurchaseSvc
            .Setup(s => s.UpdatePurchaseOrderAsync(
                It.IsAny<PurchaseOrderFormVm>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        var vm = new PurchaseOrderFormVm
        {
            PurchaseOrderId = isUpdate ? SeedPurchaseOrder().PurchaseOrderId : null,
            OrderDate       = new DateOnly(2025, 1, 1),
            SupplierCode    = "SUP001",
            Lines = new List<PurchaseOrderLineVm>
            {
                new() { LineNo = 1, ProductCode = "P001", OrderQty = 5, UomCode = "KG", StockUomCode = "g",
                        UnitPrice = 100, CurrencyCode = "TWD", ExchangeRate = 1 }
            }
        };

        var result = await _controller.Save(vm);

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("_Form", view.ViewName);
        var error = Assert.Single(_controller.ModelState[string.Empty]!.Errors);
        Assert.Equal(ex.Message, error.ErrorMessage);
    }

    // ── Delete POST ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_POST_ExistingRecord_ReturnsOk()
    {
        _mockPurchaseSvc
            .Setup(s => s.DeletePurchaseOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.Delete("PO2025000001");

        var ok = Assert.IsType<OkObjectResult>(result);
        var data = ok.Value!;
        Assert.True((bool)data.GetType().GetProperty("success")!.GetValue(data)!);
    }

    [Fact]
    public async Task Delete_POST_ServiceThrows_ReturnsBadRequest()
    {
        _mockPurchaseSvc
            .Setup(s => s.DeletePurchaseOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("已有進貨記錄，無法刪除"));

        var result = await _controller.Delete("PO2025000001");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── CloseDoc（僅核准後可結案）─────────────────────────────────────────

    [Fact]
    public async Task CloseDoc_POST_NotApproved_ReturnsBadRequestAndDoesNotClose()
    {
        SeedPurchaseOrder("PO2025000001"); // 預設 ApprovalStatus = Draft
        var result = await _controller.CloseDoc("PO2025000001");
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(_context.PurchaseOrders.Include(p => p.Lines).First().Lines.Single().IsClosed);
    }

    [Fact]
    public async Task CloseDoc_POST_Approved_ClosesAllLines()
    {
        var po = SeedPurchaseOrder("PO2025000001");
        po.ApprovalStatus = DocumentApprovalStatus.Approved;
        _context.SaveChanges();

        var result = await _controller.CloseDoc("PO2025000001");
        Assert.IsType<OkObjectResult>(result);
        Assert.True(_context.PurchaseOrders.Include(p => p.Lines).First().Lines.Single().IsClosed);
    }
}
