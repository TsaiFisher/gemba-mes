using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class APInvoiceControllerTests : TestBase
{
    private readonly Mock<IAPInvoiceService>  _mockSvc  = new();
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IDateTimeService>   _mockDt   = new();
    private readonly Mock<ICurrencyService>   _mockCur  = new();
    private readonly Mock<IApprovalService>   _mockAppr = new();
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();
    private readonly APInvoiceController _controller;

    private const string PlantId = "P001";

    public APInvoiceControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanDelete(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        _mockLoc.Setup(x => x[It.IsAny<string>()])
                .Returns<string>(k => new LocalizedString(k, k));

        _controller = new APInvoiceController(
            _context, _mockSvc.Object, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockCur.Object, _mockAppr.Object, _mockLoc.Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private APInvoice SeedInvoice(string id = "API2025000001")
    {
        var inv = new APInvoice
        {
            APInvoiceId  = id,
            SupplierCode = "SUP001",
            InvoiceDate  = new DateOnly(2025, 1, 15),
            DueDate      = new DateOnly(2025, 2, 15),
            TotalAmount  = 10000m,
            AmountLocal  = 10000m,
            PlantId      = PlantId,
        };
        _context.APInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }

    // ── Index ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NoSearch_ReturnsNullModel()
    {
        var result = await _controller.Index(null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.Model);
    }

    [Fact]
    public async Task Index_WithSearch_ReturnsPaginatedList()
    {
        SeedInvoice("API2025000001");
        SeedInvoice("API2025000002");

        var result = await _controller.Index(null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<APInvoice>>(view.Model);
        Assert.Equal(2, list.Count());
    }

    [Fact]
    public async Task Index_SupplierFilter_ReturnsMatchingInvoices()
    {
        SeedInvoice("API2025000001");
        var inv2 = new APInvoice
        {
            APInvoiceId = "API2025000002", SupplierCode = "SUP999",
            InvoiceDate = new DateOnly(2025, 1, 20), DueDate = new DateOnly(2025, 2, 20),
            TotalAmount = 5000m, AmountLocal = 5000m, PlantId = PlantId,
        };
        _context.APInvoices.Add(inv2);
        _context.SaveChanges();

        var result = await _controller.Index("SUP001", null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<APInvoice>>(view.Model);
        Assert.Single(list);
    }

    // ── Details ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Details_ExistingId_AjaxReturnsPartialView()
    {
        SetAjax();
        SeedInvoice("API2025000001");

        var result = await _controller.Details("API2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.True((bool)view.ViewData["IsReadOnly"]!);
    }

    [Fact]
    public async Task Details_NotFound_Returns404()
    {
        var result = await _controller.Details("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Void ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Success_CallsServiceAndReturnsJsonSuccess()
    {
        _mockSvc.Setup(s => s.VoidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var result = await _controller.Void("API2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);
        _mockSvc.Verify(s => s.VoidAsync("API2025000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_ServiceThrows_ReturnsJsonFailure()
    {
        _mockSvc.Setup(s => s.VoidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("已付款，無法作廢"));

        var result = await _controller.Void("API2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    /// <summary>
    /// 手動開票的稅額：`Tax.TaxRate` 以**小數**存放（0.05 = 5%），不可再除以 100。
    /// 與 AR 側同一個缺陷、同一個形狀——服務層的自動認列早就修好，
    /// 兩支手動控制器一直沿用 `/ 100m`，稅額縮成百分之一而傳票照樣平衡。
    /// 1000 × 5% = 50；壞掉的版本是 0.5。
    /// </summary>
    [Fact]
    public async Task CreateManual_TaxRateIsDecimalFraction_NotPercent()
    {
        _context.Set<SmartFactory.MES.Modules.BAS.Models.Tax>().Add(
            new SmartFactory.MES.Modules.BAS.Models.Tax
            { TaxCode = "T005", TaxName = "5%", TaxRate = 0.05m, IsActive = true });
        _context.SaveChanges();

        var cur = GetMockCurrencyService();
        cur.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
           .ReturnsAsync(1m);
        var controller = new APInvoiceController(
            _context, _mockSvc.Object, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, cur.Object, _mockAppr.Object, _mockLoc.Object);
        controller.ControllerContext = GetControllerContext();
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        APInvoice? captured = null;
        _mockSvc.Setup(s => s.CreateManualAsync(It.IsAny<APInvoice>(), It.IsAny<DocumentApprovalStatus>(),
                                                It.IsAny<CancellationToken>()))
                .Callback<APInvoice, DocumentApprovalStatus?, CancellationToken>((i, _, _) => captured = i)
                .ReturnsAsync((APInvoice i, DocumentApprovalStatus? _, CancellationToken _) => i);

        await controller.CreateManual(new ManualAPInvoiceViewModel
        {
            SupplierCode = "SUP001",
            InvoiceDate  = new DateOnly(2025, 1, 15),
            DueDate      = new DateOnly(2025, 2, 15),
            CurrencyCode = "TWD",
            ExchangeRate = 1m,
            Lines = { new ManualAPInvoiceLineViewModel
                      { AccountCode = "5001", Qty = 1m, UnitPrice = 1000m, TaxCode = "T005" } },
        });

        Assert.NotNull(captured);
        Assert.Equal(50m, captured!.Lines.Single().TaxAmount);
        Assert.Equal(50m, captured.TaxAmount);
        Assert.Equal(1050m, captured.TotalAmount);
    }
}
