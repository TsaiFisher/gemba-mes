using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Controllers;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 採購金額分析的來源別（2026-09-21）。
///
/// <para><b>修的是一個靜默低估</b>：這張表原本只讀 <c>GoodsReceiptLines</c>，委外加工費
/// 一毛都不在裡面。採購總支出因此少一塊，而且少得毫無徵兆——畫面上就是一份看起來完整的
/// 年度金額表。委外做得越多的廠，低估得越多。</para>
///
/// <para>預設含委外（<c>All</c>）＝行為變更：既有部署升級後總額會變大。保留 <c>PO</c>
/// 讓舊口徑仍查得到，兩者的差額就是委外加工費。</para>
/// </summary>
public class PurchaseAmountOutsourceTests : TestBase
{
    private const string PlantId = "P001";
    private const int Year = 2026;

    private readonly Mock<IUserContext>       _user = new();
    private readonly Mock<IPermissionService> _perm = new();
    private readonly Mock<IDateTimeService>   _dt   = new();

    public PurchaseAmountOutsourceTests()
    {
        _context.CurrentPlantId = PlantId;
        _user.Setup(u => u.PlantId).Returns(PlantId);
        _perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        _perm.Setup(p => p.CanPrint(It.IsAny<string>())).ReturnsAsync(true);
        _dt.Setup(d => d.Now).Returns(new DateTime(Year, 6, 1));

        _context.Companies.Add(new Company
        {
            CompanyCode = "S1", CompanyName = "供應商一", IsSupplier = true, IsActive = true,
        });

        // 採購進貨 1,000
        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = "GR-1", PlantId = PlantId, SupplierCode = "S1",
            ReceiptDate = new DateOnly(Year, 3, 10), Status = DocStatus.Posted,
        });
        _context.GoodsReceiptLines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR-1", LineNo = 1, ProductCode = "PRD-A", AmountLocal = 1000m,
        });

        // 委外收貨 250（原本完全不進這張表）
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-1", PlantId = PlantId, SupplierCode = "S1",
            ReceiptDate = new DateOnly(Year, 3, 20), Status = DocStatus.Confirmed,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR-1", LineNo = 1, ProductCode = "PRD-A", AmountLocal = 250m,
        });

        _context.SaveChanges();
    }

    private PurchaseAmountController Build()
        => new(_context, _user.Object, _perm.Object, GetMockExportService().Object, _dt.Object,
               new SmartFactory.MES.Modules.PUR.Reporting.PurchaseAmountTool(
               _context,
               new SmartFactory.MES.Modules.RPS.Services.RelativePeriodResolver(_dt.Object, _context),
               _dt.Object),
               GetMockLocalizer<SmartFactory.MES.ProjectZSharedResource>().Object,
               GetMockLocalizer<SmartFactory.MES.ProjectZReportHeaderResource>().Object)
           { ControllerContext = GetControllerContextWithServices() };

    private static List<PurchaseAmountRowVm> Rows(IActionResult r)
        => (List<PurchaseAmountRowVm>)((ViewResult)r).Model!;

    [Theory]
    [InlineData("Supplier")]
    [InlineData("Product")]
    public async Task 預設含委外_總額是採購加委外(string groupBy)
    {
        var rows = Rows(await Build().Index(Year, groupBy, source: null, searched: true));

        Assert.Equal(1250m, rows.Sum(r => r.TotalAmount));

        // 兩張單同屬 3 月、同一供應商／品號，合併後必須是**一列**。
        // 兩個來源各自分組再串起來的話，這裡會是兩列——畫面上看起來像同一個月出現兩次。
        var row = Assert.Single(rows);
        Assert.Equal(3, row.Month);
        Assert.Equal(1250m, row.TotalAmount);
        Assert.Equal(2, row.ReceiptCount);   // 單據筆數也要跨來源去重後相加
    }

    [Fact]
    public async Task 不同月份的採購與委外各自成列()
    {
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-JUL", PlantId = PlantId, SupplierCode = "S1",
            ReceiptDate = new DateOnly(Year, 7, 5), Status = DocStatus.Posted,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR-JUL", LineNo = 1, ProductCode = "PRD-A", AmountLocal = 400m,
        });
        _context.SaveChanges();

        var rows = Rows(await Build().Index(Year, "Supplier", source: "All", searched: true));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1250m, rows.Single(r => r.Month == 3).TotalAmount);
        Assert.Equal(400m,  rows.Single(r => r.Month == 7).TotalAmount);
    }

    [Fact]
    public async Task 只看採購時回到舊口徑()
    {
        var rows = Rows(await Build().Index(Year, "Supplier", source: "PO", searched: true));

        Assert.Equal(1000m, rows.Sum(r => r.TotalAmount));
    }

    [Fact]
    public async Task 只看委外時只剩加工費()
    {
        var rows = Rows(await Build().Index(Year, "Supplier", source: "OSP", searched: true));

        Assert.Equal(250m, rows.Sum(r => r.TotalAmount));
    }

    [Fact]
    public async Task 草稿委外收貨不計入()
    {
        _context.OutsourceReceipts.Add(new OutsourceReceipt
        {
            OutsourceReceiptId = "OR-DRAFT", PlantId = PlantId, SupplierCode = "S1",
            ReceiptDate = new DateOnly(Year, 4, 1), Status = DocStatus.Draft,
        });
        _context.OutsourceReceiptLines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = "OR-DRAFT", LineNo = 1, ProductCode = "PRD-A", AmountLocal = 9999m,
        });
        _context.SaveChanges();

        var rows = Rows(await Build().Index(Year, "Supplier", source: "All", searched: true));

        Assert.Equal(1250m, rows.Sum(r => r.TotalAmount));
    }
}
