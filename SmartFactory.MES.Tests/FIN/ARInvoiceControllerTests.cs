using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class ARInvoiceControllerTests : TestBase
{
    private readonly Mock<IARInvoiceService>  _mockSvc  = new();
    private readonly Mock<IUserContext>       _mockUser = new();
    private readonly Mock<IPermissionService> _mockPerm = new();
    private readonly Mock<IDateTimeService>   _mockDt   = new();
    private readonly Mock<ICurrencyService>   _mockCur  = new();
    private readonly Mock<IApprovalService>   _mockAppr = new();
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();
    private readonly ARInvoiceController _controller;

    private const string PlantId = "P001";

    public ARInvoiceControllerTests()
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

        _controller = new ARInvoiceController(
            _context, _mockSvc.Object, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockCur.Object, _mockAppr.Object, _mockLoc.Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private ARInvoice SeedInvoice(string id = "ARI2025000001", string customerCode = "CUST001")
    {
        var inv = new ARInvoice
        {
            ARInvoiceId  = id,
            CustomerCode = customerCode,
            InvoiceDate  = new DateOnly(2025, 1, 15),
            DueDate      = new DateOnly(2025, 2, 15),
            TotalAmount  = 20000m,
            AmountLocal  = 20000m,
            PlantId      = PlantId,
        };
        _context.ARInvoices.Add(inv);
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
        SeedInvoice("ARI2025000001");
        SeedInvoice("ARI2025000002", "CUST002");

        var result = await _controller.Index(null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<ARInvoice>>(view.Model);
        Assert.Equal(2, list.Count());
    }

    [Fact]
    public async Task Index_CustomerFilter_ReturnsMatchingInvoices()
    {
        SeedInvoice("ARI2025000001", "CUST001");
        SeedInvoice("ARI2025000002", "CUST999");

        var result = await _controller.Index("CUST001", null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<ARInvoice>>(view.Model);
        Assert.Single(list);
    }

    // ── Details ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Details_ExistingId_AjaxReturnsPartialView()
    {
        SetAjax();
        SeedInvoice("ARI2025000001");

        var result = await _controller.Details("ARI2025000001");

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

        var result = await _controller.Void("ARI2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);
        _mockSvc.Verify(s => s.VoidAsync("ARI2025000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Void_ServiceThrows_ReturnsJsonFailure()
    {
        _mockSvc.Setup(s => s.VoidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("已收款，無法作廢"));

        var result = await _controller.Void("ARI2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    /// <summary>
    /// 手動開票的稅額：`Tax.TaxRate` 以**小數**存放（0.05 = 5%），不可再除以 100。
    ///
    /// 自動開票（ARInvoiceService.GenerateFromShipmentAsync）早就修掉這個算式，
    /// 註解甚至寫著「早期誤加 / 100 使發票稅額縮成百分之一」——但手動這條路徑沒跟上。
    /// 於是同一個稅別 T005，自動開票收 5% 稅、手動開票收 0.05%，
    /// 而兩張傳票都借貸平衡、任何「借＝貸」的稽核都不會叫。
    ///
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
        var controller = new ARInvoiceController(
            _context, _mockSvc.Object, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, cur.Object, _mockAppr.Object, _mockLoc.Object);
        controller.ControllerContext = GetControllerContext();
        controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

        ARInvoice? captured = null;
        _mockSvc.Setup(s => s.CreateManualAsync(It.IsAny<ARInvoice>(), It.IsAny<DocumentApprovalStatus>(),
                                                It.IsAny<CancellationToken>()))
                .Callback<ARInvoice, DocumentApprovalStatus?, CancellationToken>((i, _, _) => captured = i)
                .ReturnsAsync((ARInvoice i, DocumentApprovalStatus? _, CancellationToken _) => i);

        await controller.CreateManual(new ManualARInvoiceViewModel
        {
            CustomerCode = "CUST001",
            InvoiceDate  = new DateOnly(2025, 1, 15),
            DueDate      = new DateOnly(2025, 2, 15),
            CurrencyCode = "TWD",
            ExchangeRate = 1m,
            Lines = { new ManualARInvoiceLineViewModel
                      { AccountCode = "4100", Qty = 1m, UnitPrice = 1000m, TaxCode = "T005" } },
        });

        Assert.NotNull(captured);
        Assert.Equal(50m, captured!.Lines.Single().TaxAmount);
        Assert.Equal(50m, captured.TaxAmount);
        Assert.Equal(1050m, captured.TotalAmount);
    }
}
