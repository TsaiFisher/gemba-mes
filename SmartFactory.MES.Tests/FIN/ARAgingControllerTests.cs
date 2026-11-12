using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class ARAgingControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser   = new();
    private readonly Mock<IPermissionService> _mockPerm   = new();
    private readonly Mock<IExportService>     _mockExport = new();
    private readonly Mock<IDateTimeService>  _mockDt     = new();
    private readonly ARAgingController        _controller;

    private static readonly DateOnly LocalToday = new(2026, 5, 20);

    private const string PlantId = "P001";

    public ARAgingControllerTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockUser.Setup(u => u.PlantId).Returns(PlantId);
        _mockPerm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _mockPerm.Setup(p => p.CanCreate(It.IsAny<string>())).ReturnsAsync(true);
        // Export 閘門改用 CanPrint（資料出境管制），mock 未涵蓋此動詞會讓匯出測試誤紅
        _mockPerm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _mockExport.Setup(e => e.ToExcel(It.IsAny<ExportRequest>())).Returns(Array.Empty<byte>());

        // 帳齡基準日走 IDateTimeService（廠別當地日），未帶 asOf 時即由它決定分桶起點
        _mockDt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(LocalToday);

        _controller = new ARAgingController(
            _context, _mockUser.Object, _mockPerm.Object, _mockExport.Object, _mockDt.Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SeedARInvoice(string id, string customerCode, DateOnly dueDate, decimal total,
        ARInvoiceStatus status = ARInvoiceStatus.Open)
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId    = id,
            CustomerCode   = customerCode,
            InvoiceDate    = dueDate.AddDays(-30),
            DueDate        = dueDate,
            TotalAmount    = total,
            AmountLocal    = total,
            ReceivedAmount = 0m,
            ExchangeRate   = 1m,
            Status         = status,
            PlantId        = PlantId,
        });
        _context.SaveChanges();
    }

    // ── Index ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Index_NoSearch_ReturnsEmptyListView()
    {
        var result = await _controller.Index(null, null, null, searched: false);
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Empty(model);
    }

    [Fact]
    public async Task Index_WithSearch_ReturnsAgingRows()
    {
        var asOf = new DateOnly(2025, 4, 30);
        SeedARInvoice("ARI001", "CUST001", asOf.AddDays(-10), 8000m);  // 1-30天
        SeedARInvoice("ARI002", "CUST001", asOf.AddDays(-45), 4000m);  // 31-60天
        SeedARInvoice("ARI003", "CUST002", asOf.AddDays(10),  3000m);  // 未到期

        var result = await _controller.Index(asOf, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Equal(2, rows.Count);

        var cust1 = rows.First(r => r.PartyCode == "CUST001");
        Assert.Equal(8000m, cust1.Days30);
        Assert.Equal(4000m, cust1.Days60);
        Assert.Equal(12000m, cust1.Total);

        var cust2 = rows.First(r => r.PartyCode == "CUST002");
        Assert.Equal(3000m, cust2.Current);
    }

    [Fact]
    public async Task Index_CustomerFilter_ReturnsFilteredRows()
    {
        var asOf = new DateOnly(2025, 4, 30);
        SeedARInvoice("ARI001", "CUST001", asOf.AddDays(-10), 5000m);
        SeedARInvoice("ARI002", "CUST002", asOf.AddDays(-10), 2000m);

        var result = await _controller.Index(asOf, "CUST001", null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Single(rows);
        Assert.Equal("CUST001", rows[0].PartyCode);
    }

    /// <summary>
    /// 收訖之後才退貨開折讓：客戶多付 105，是我方欠客戶。發票狀態仍是已收訖（折讓不是收款），
    /// 原本帳齡按狀態排除它，這筆多付款在系統上完全看不到。
    /// </summary>
    [Fact]
    public async Task Index_CreditNoteAfterFullReceipt_ShowsCustomerCreditBalance()
    {
        var asOf = new DateOnly(2025, 4, 30);
        SeedARInvoice("ARI-OPEN", "CUST001", asOf.AddDays(-10), 5000m);
        SeedARInvoice("ARI-PAIDCN", "CUST001", asOf.AddDays(-20), 1050m, ARInvoiceStatus.Received);
        var paid = _context.ARInvoices.Find("ARI-PAIDCN")!;
        paid.ReceivedAmount = 1050m;
        paid.CreditedAmount = 105m;
        _context.SaveChanges();

        var view = Assert.IsType<ViewResult>(await _controller.Index(asOf, null, null, searched: true));
        var row = Assert.Single(Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model));

        Assert.Equal(4895m, row.Days30);   // 5000 − 105
        Assert.Equal(4895m, row.Total);
    }

    [Fact]
    public async Task Index_ReceivedAndVoidInvoices_ExcludedFromAging()
    {
        var asOf = new DateOnly(2025, 4, 30);
        SeedARInvoice("ARI001", "CUST001", asOf.AddDays(-10), 5000m);
        SeedARInvoice("ARI002", "CUST001", asOf.AddDays(-20), 3000m, ARInvoiceStatus.Received);
        SeedARInvoice("ARI003", "CUST001", asOf.AddDays(-30), 2000m, ARInvoiceStatus.Void);

        var result = await _controller.Index(asOf, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Single(rows);
        Assert.Equal(5000m, rows[0].Days30);
    }

    // ── Export ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ReturnsFileResult()
    {
        SeedARInvoice("ARI001", "CUST001", new DateOnly(2025, 4, 1), 2000m);

        var result = await _controller.Export(new DateOnly(2025, 4, 30), null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Contains("spreadsheetml", file.ContentType);
        _mockExport.Verify(e => e.ToExcel(It.IsAny<ExportRequest>()), Times.Once);
    }
}
