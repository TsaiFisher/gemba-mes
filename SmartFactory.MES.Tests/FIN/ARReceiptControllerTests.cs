using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class ARReceiptControllerTests : TestBase
{
    private readonly Mock<IFinPaymentService> _mockPaySvc = new();
    private readonly Mock<IUserContext>       _mockUser   = new();
    private readonly Mock<IPermissionService> _mockPerm   = new();
    private readonly Mock<IDateTimeService>   _mockDt     = new();
    private readonly Mock<IStringLocalizer<ProjectZSharedResource>> _mockLoc = new();
    private readonly ARReceiptController _controller;

    private const string PlantId = "P001";
    private const string UserId  = "user1";

    public ARReceiptControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockUser.Setup(u => u.UserId).Returns(UserId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanUpdate(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(false);
        _mockLoc.Setup(x => x[It.IsAny<string>()])
                .Returns<string>(k => new LocalizedString(k, k));
        _mockDt.Setup(d => d.Now).Returns(DateTime.UtcNow);

        _controller = new ARReceiptController(
            _context, _mockPaySvc.Object, _mockUser.Object,
            _mockPerm.Object, _mockDt.Object, _mockLoc.Object);
        _controller.ControllerContext = GetControllerContext(userId: UserId);
    }

    private void SetAjax() =>
        _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

    private ARReceipt SeedReceipt(string id = "ARR2025000001")
    {
        var rec = new ARReceipt
        {
            ARReceiptId  = id,
            CustomerCode = "CUST001",
            ReceiptDate  = new DateOnly(2025, 3, 15),
            Method       = PaymentMethod.Wire,
            Amount       = 20000m,
            AmountLocal  = 20000m,
            Status       = ARReceiptStatus.Draft,
            PlantId      = PlantId,
        };
        _context.ARReceipts.Add(rec);
        _context.SaveChanges();
        return rec;
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
        SeedReceipt("ARR2025000001");
        SeedReceipt("ARR2025000002");

        var result = await _controller.Index(null, null, null, searched: true);
        var view = Assert.IsType<ViewResult>(result);
        var list = Assert.IsAssignableFrom<IEnumerable<ARReceipt>>(view.Model);
        Assert.Equal(2, list.Count());
    }

    // ── Create GET ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_GET_AjaxReturnsPartialView()
    {
        SetAjax();
        var result = await _controller.Create();
        var view = Assert.IsType<PartialViewResult>(result);
        Assert.IsType<ARReceiptFormVm>(view.Model);
    }

    // ── Details ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Details_ExistingId_AjaxReturnsPartialViewReadOnly()
    {
        SetAjax();
        SeedReceipt("ARR2025000001");

        var result = await _controller.Details("ARR2025000001");

        var view = Assert.IsType<PartialViewResult>(result);
        Assert.True((bool)view.ViewData["IsReadOnly"]!);
    }

    [Fact]
    public async Task Details_NotFound_Returns404()
    {
        var result = await _controller.Details("NONEXISTENT");
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Confirm POST ──────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_Success_CallsServiceAndReturnsJsonSuccess()
    {
        _mockPaySvc.Setup(s => s.ConfirmARReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

        var result = await _controller.Confirm("ARR2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);
        _mockPaySvc.Verify(s => s.ConfirmARReceiptAsync("ARR2025000001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Confirm_ServiceThrows_ReturnsJsonFailure()
    {
        _mockPaySvc.Setup(s => s.ConfirmARReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("已確認，無法重複操作"));

        var result = await _controller.Confirm("ARR2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    // ── Void POST ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Void_Success_CallsServiceAndReturnsJsonSuccess()
    {
        _mockPaySvc.Setup(s => s.VoidARReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);

        var result = await _controller.Void("ARR2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.True(success);
    }

    [Fact]
    public async Task Void_ServiceThrows_ReturnsJsonFailure()
    {
        _mockPaySvc.Setup(s => s.VoidARReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("收款已過帳"));

        var result = await _controller.Void("ARR2025000001");

        var json = Assert.IsType<JsonResult>(result);
        var success = (bool)json.Value!.GetType().GetProperty("success")!.GetValue(json.Value)!;
        Assert.False(success);
    }

    // ── GetOpenInvoices ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOpenInvoices_ReturnsJsonWithMatchingInvoices()
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId    = "ARI001", CustomerCode = "CUST001",
            InvoiceDate    = new DateOnly(2025, 1, 1), DueDate = new DateOnly(2025, 2, 1),
            TotalAmount    = 10000m, AmountLocal = 10000m, ReceivedAmount = 0m,
            ExchangeRate   = 1m, Status = ARInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId    = "ARI002", CustomerCode = "CUST999",
            InvoiceDate    = new DateOnly(2025, 1, 1), DueDate = new DateOnly(2025, 2, 1),
            TotalAmount    = 5000m, AmountLocal = 5000m, ReceivedAmount = 0m,
            ExchangeRate   = 1m, Status = ARInvoiceStatus.Open, PlantId = PlantId,
        });
        _context.SaveChanges();

        var result = await _controller.GetOpenInvoices("CUST001");

        var json = Assert.IsType<JsonResult>(result);
        var list = json.Value as IEnumerable<object>;
        Assert.NotNull(list);
        Assert.Single(list);
    }
}
