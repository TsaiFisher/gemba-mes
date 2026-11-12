using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.FIN.Controllers;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

public class APAgingControllerTests : TestBase
{
    private readonly Mock<IUserContext>       _mockUser   = new();
    private readonly Mock<IPermissionService> _mockPerm   = new();
    private readonly Mock<IExportService>     _mockExport = new();
    private readonly Mock<IDateTimeService>  _mockDt     = new();
    private readonly APAgingController        _controller;

    private static readonly DateOnly LocalToday = new(2026, 5, 20);

    private const string PlantId = "P001";

    public APAgingControllerTests()
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

        _controller = new APAgingController(
            _context, _mockUser.Object, _mockPerm.Object, _mockExport.Object, _mockDt.Object);
        _controller.ControllerContext = GetControllerContext();
    }

    private void SeedAPInvoice(string id, string supplierCode, DateOnly dueDate, decimal total, decimal paid = 0m)
    {
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = id,
            SupplierCode = supplierCode,
            InvoiceDate  = dueDate.AddDays(-30),
            DueDate      = dueDate,
            TotalAmount  = total,
            AmountLocal  = total,
            PaidAmount   = paid,
            ExchangeRate = 1m,
            Status       = APInvoiceStatus.Open,
            PlantId      = PlantId,
        });
        _context.SaveChanges();
    }

    /// <summary>付清之後才開進貨折讓：供應商欠我們 105（負餘額），帳齡要看得到。</summary>
    [Fact]
    public async Task Index_DebitNoteAfterFullPayment_ShowsSupplierDebitBalance()
    {
        var asOf = new DateOnly(2025, 4, 30);
        SeedAPInvoice("API-PAIDDN", "SUP001", asOf.AddDays(-20), 1050m, paid: 1050m);
        var inv = _context.APInvoices.Find("API-PAIDDN")!;
        inv.Status = APInvoiceStatus.Paid;
        inv.DebitedAmount = 105m;
        _context.SaveChanges();

        var view = Assert.IsType<ViewResult>(await _controller.Index(asOf, null, null, searched: true));
        var row = Assert.Single(Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model));

        Assert.Equal(-105m, row.Total);
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
        var asOf = new DateOnly(2025, 3, 31);
        SeedAPInvoice("API001", "SUP001", asOf.AddDays(-10), 5000m);  // 1-30天
        SeedAPInvoice("API002", "SUP001", asOf.AddDays(-40), 3000m);  // 31-60天
        SeedAPInvoice("API003", "SUP002", asOf.AddDays(5),   2000m);  // 未到期

        var result = await _controller.Index(asOf, null, null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Equal(2, rows.Count);

        var sup1 = rows.First(r => r.PartyCode == "SUP001");
        Assert.Equal(5000m, sup1.Days30);
        Assert.Equal(3000m, sup1.Days60);
        Assert.Equal(8000m, sup1.Total);

        var sup2 = rows.First(r => r.PartyCode == "SUP002");
        Assert.Equal(2000m, sup2.Current);
    }

    [Fact]
    public async Task Index_WithSupplierFilter_ReturnsFilteredRows()
    {
        var asOf = new DateOnly(2025, 3, 31);
        SeedAPInvoice("API001", "SUP001", asOf.AddDays(-10), 5000m);
        SeedAPInvoice("API002", "SUP002", asOf.AddDays(-10), 3000m);

        var result = await _controller.Index(asOf, "SUP001", null, searched: true);

        var view = Assert.IsType<ViewResult>(result);
        var rows = Assert.IsAssignableFrom<List<AgingRowVm>>(view.Model);
        Assert.Single(rows);
        Assert.Equal("SUP001", rows[0].PartyCode);
    }

    [Fact]
    public async Task Index_VoidAndPaidInvoices_ExcludedFromAging()
    {
        var asOf = new DateOnly(2025, 3, 31);
        SeedAPInvoice("API001", "SUP001", asOf.AddDays(-10), 5000m);
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = "API002", SupplierCode = "SUP001",
            InvoiceDate = asOf.AddDays(-40), DueDate = asOf.AddDays(-40),
            TotalAmount = 3000m, AmountLocal = 3000m,
            Status = APInvoiceStatus.Paid, PlantId = PlantId,
        });
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = "API003", SupplierCode = "SUP001",
            InvoiceDate = asOf.AddDays(-20), DueDate = asOf.AddDays(-20),
            TotalAmount = 2000m, AmountLocal = 2000m,
            Status = APInvoiceStatus.Void, PlantId = PlantId,
        });
        _context.SaveChanges();

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
        SeedAPInvoice("API001", "SUP001", new DateOnly(2025, 3, 1), 1000m);

        var result = await _controller.Export(new DateOnly(2025, 3, 31), null);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Contains("spreadsheetml", file.ContentType);
        _mockExport.Verify(e => e.ToExcel(It.IsAny<ExportRequest>()), Times.Once);
    }
}
