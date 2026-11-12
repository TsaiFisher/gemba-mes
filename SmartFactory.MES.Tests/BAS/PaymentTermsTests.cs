using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 到期日依往來廠商付款條件（結帳日＋收付款天數）計算。原本自動產生的應收／應付一律 +30 天，
/// 主檔上的結帳日從來沒被讀過；帳齡、呆帳提列與 ECL 都依到期日分桶，所以這不只是顯示問題。
/// </summary>
public class PaymentTermsTests : TestBase
{
    [Theory]
    // 使用者確認的範例：結帳日 5、30 天 → 6/6～7/5 的單據在 7/5 結帳、8/4 到期
    [InlineData("2026-06-06", 5, 30, "2026-08-04")]
    [InlineData("2026-07-05", 5, 30, "2026-08-04")]   // 結帳日當天仍屬本期
    [InlineData("2026-07-06", 5, 30, "2026-09-04")]   // 過了結帳日 → 下一期
    [InlineData("2026-12-20", 5, 30, "2027-02-04")]   // 跨年
    // 結帳日大於當月天數取月底
    [InlineData("2026-02-10", 31, 0, "2026-02-28")]
    [InlineData("2028-02-10", 31, 0, "2028-02-29")]   // 閏年
    [InlineData("2026-01-31", 31, 10, "2026-02-10")]
    // 沒有結帳日：從單據日起算
    [InlineData("2026-06-06", null, 45, "2026-07-21")]
    // 天數沒填沿用 30（未設定付款條件者行為與修改前相同）
    [InlineData("2026-06-06", null, null, "2026-07-06")]
    [InlineData("2026-06-06", 5, null, "2026-08-04")]
    // 結帳日超出 1–31 視為沒填
    [InlineData("2026-06-06", 0, 30, "2026-07-06")]
    public void 到期日依結帳日與天數計算(string doc, int? closingDay, int? creditDays, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected),
            PaymentTerms.DueDate(DateOnly.Parse(doc), closingDay, creditDays));
    }

    [Fact]
    public async Task 出貨自動產生的應收發票_到期日依客戶付款條件()
    {
        _context.CurrentPlantId = "P001";
        _context.Companies.Add(new Company
        {
            CompanyCode = "C-MONTHLY", CompanyName = "月結客戶", IsCustomer = true,
            PaymentTermDays = 5, PaymentCreditDays = 30,
        });
        var ship = new GoodsShipment
        {
            GoodsShipmentId = "GS-PT1", ShipDate = new DateOnly(2026, 6, 6), CreatedBy = "u",
            CompanyCode = "C-MONTHLY", Status = DocStatus.Posted,
            ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = "P001",
        };
        ship.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = "GS-PT1", LineNo = 1, ProductCode = "P1", ShipQty = 1, UomCode = "PC",
            StockQty = 1, StockUomCode = "PC", UnitPrice = 100,
        });
        _context.GoodsShipments.Add(ship);
        await _context.SaveChangesAsync();

        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("ARInvoice", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync("ARI-PT1");
        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                .ReturnsAsync((decimal?)null);
        var svc = new ARInvoiceService(_context, docNo.Object, new Mock<IDateTimeService>().Object, currency.Object, periodSvc: null);

        var inv = await svc.GenerateFromShipmentAsync("GS-PT1");

        Assert.Equal(new DateOnly(2026, 8, 4), inv.DueDate);
    }

    [Fact]
    public async Task 收料自動產生的應付發票_到期日依供應商付款條件()
    {
        _context.CurrentPlantId = "P001";
        _context.Companies.Add(new Company
        {
            CompanyCode = "S-NET45", CompanyName = "45 天供應商", IsSupplier = true, PaymentCreditDays = 45,
        });
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = "GR-PT1", ReceiptDate = new DateOnly(2026, 6, 6), CreatedBy = "u",
            SupplierCode = "S-NET45", Status = DocStatus.Confirmed,
            ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = "P001",
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = "GR-PT1", LineNo = 1, ProductCode = "M1", ReceiptQty = 1, UomCode = "PC",
            StockQty = 1, StockUomCode = "PC", UnitPrice = 100, LineAmount = 100, TotalAmount = 100,
        });
        _context.GoodsReceipts.Add(gr);
        await _context.SaveChangesAsync();

        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync("API-PT1");
        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
                .ReturnsAsync((decimal?)null);
        var svc = new APInvoiceService(_context, docNo.Object, new Mock<IDateTimeService>().Object, currency.Object);

        var inv = await svc.GenerateFromGoodsReceiptAsync("GR-PT1");

        Assert.Equal(new DateOnly(2026, 7, 21), inv.DueDate);
    }
}
