using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Modules.ACT.Controllers;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.ACT.ViewModels;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 呆帳提列／ECL／NRV 的計算口徑（2026-09-30 統一）。三支原本各算各的：
/// 呆帳提列拿出貨單代 AR、ECL 用交易幣、NRV 用交易幣＋銷售單位的簡單平均。
/// 統一後的口徑與 AR 帳齡表相同：**本幣未收餘額、依到期日**；售價為**本幣、每庫存單位、加權**。
/// </summary>
public class ProvisionBasisTests : TestBase
{
    private const string Plant = "P1";
    private static readonly DateOnly AsOf = new(2026, 9, 30);

    public ProvisionBasisTests() => _context.CurrentPlantId = Plant;

    private void AddAr(string id, decimal amountLocal, decimal received = 0, decimal rate = 1,
        int dueOffset = 0, int invoiceOffset = -30, ARInvoiceStatus status = ARInvoiceStatus.Open,
        string customer = "C1", decimal? total = null)
    {
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId    = id,
            CustomerCode   = customer,
            InvoiceDate    = AsOf.AddDays(invoiceOffset),
            DueDate        = AsOf.AddDays(dueOffset),
            ExchangeRate   = rate,
            TotalAmount    = total ?? amountLocal / rate,
            AmountLocal    = amountLocal,
            ReceivedAmount = received,
            Status         = status,
            PlantId        = Plant,
        });
        _context.SaveChanges();
    }

    // ── ECL ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ecl_外幣發票以本幣未收餘額計入_不是原幣金額()
    {
        // USD 100 @ 30 = 本幣 3000，已收 USD 40 → 未收本幣 3000 − 40×30 = 1800。
        // 舊算法 TotalAmount − ReceivedAmount = 60（原幣），直接與台幣相加。
        AddAr("USD1", amountLocal: 3000, received: 40, rate: 30, dueOffset: -15, total: 100);

        var rows = await new EclService(_context).ComputeEclAsync("202609", Plant, AsOf);

        var bucket = Assert.Single(rows, r => r.AgingBucket == "1-30");
        Assert.Equal(1800m, bucket.ArBalance);
        Assert.Equal(Math.Round(1800m * 0.02m, 4), bucket.EclAmount);
    }

    [Fact]
    public async Task Ecl_基準日之後才開立的發票不列入()
    {
        AddAr("BEFORE", 1000, dueOffset: 10, invoiceOffset: -1);
        AddAr("AFTER",  5000, dueOffset: 40, invoiceOffset: +5);

        var rows = await new EclService(_context).ComputeEclAsync("202609", Plant, AsOf);

        Assert.Equal(1000m, rows.Sum(r => r.ArBalance));
    }

    [Fact]
    public async Task Ecl_有本廠損失率設定時用設定_呆帳提列跟著用同一份()
    {
        _context.EclLossRates.Add(
            new EclLossRate { AgingBucket = "ALL", AgingDaysFrom = int.MinValue, AgingDaysTo = -1, LossRate = 0.10m, PlantId = Plant });
        _context.SaveChanges();
        AddAr("X", 1000, dueOffset: -15);

        var rates = await new EclService(_context).GetLossRatesAsync(Plant);
        Assert.Equal("ALL", Assert.Single(rates).AgingBucket);

        var row = Assert.Single(await BadDebtAsync());
        Assert.Equal("ALL", row.AgingBucket);
        Assert.Equal(100m, row.Provision);
    }

    [Fact]
    public async Task Ecl_沒有任何設定時用預設五區間()
    {
        var rates = await new EclService(_context).GetLossRatesAsync(Plant);
        Assert.Equal(new[] { "Current", "1-30", "31-60", "61-90", "90+" }, rates.Select(r => r.AgingBucket));
    }

    [Theory]
    [InlineData("2026-08-31", "2026-09-30", "2026-08-31")]   // 補跑上個月：期末
    [InlineData("2026-09-30", "2026-09-15", "2026-09-15")]   // 當期未結束：今天
    [InlineData(null,         "2026-09-15", "2026-09-15")]   // 查無期間：今天
    public void Ecl_基準日取期末_期間未結束才用今天(string? periodEnd, string today, string expected)
    {
        var end = periodEnd is null ? (DateOnly?)null : DateOnly.Parse(periodEnd);
        Assert.Equal(DateOnly.Parse(expected), EclService.AsOfFor(end, DateOnly.Parse(today)));
    }

    // ── 呆帳提列 ─────────────────────────────────────────────────────────

    private async Task<List<BadDebtRowVm>> BadDebtAsync()
    {
        var perm = new Mock<IPermissionService>();
        perm.Setup(p => p.CanRead(It.IsAny<string>())).ReturnsAsync(true);
        var user = new Mock<IUserContext>();
        user.Setup(u => u.PlantId).Returns(Plant);
        var ctrl = new BadDebtProvisionController(_context, user.Object, perm.Object,
            GetMockExportService().Object, GetMockDateTimeService().Object,
            GetMockLocalizer<ProjectZSharedResource>().Object, new EclService(_context));

        var view = Assert.IsType<ViewResult>(await ctrl.Index(AsOf.ToString("yyyy-MM-dd"), null, searched: true));
        return Assert.IsType<List<BadDebtRowVm>>(view.Model);
    }

    [Fact]
    public async Task 呆帳提列_來源是未結AR發票_已收清與作廢不列入_出貨單不再計入()
    {
        AddAr("OPEN",  2000, dueOffset: -15);
        AddAr("PAID",  9000, received: 9000, dueOffset: -15, status: ARInvoiceStatus.Received);
        AddAr("VOID",  8000, dueOffset: -15, status: ARInvoiceStatus.Void);
        // 舊算法的來源：已過帳出貨單。改寫後不該有任何影響。
        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = "GS1", CompanyCode = "C1", ShipDate = AsOf.AddDays(-200),
            Status = DocStatus.Posted, TotalAmount = 70000, PlantId = Plant,
        });
        _context.SaveChanges();

        var rows = await BadDebtAsync();

        var row = Assert.Single(rows);
        Assert.Equal("1-30", row.AgingBucket);
        Assert.Equal(2000m, row.TotalAmount);
        Assert.Equal(0.02m, row.LossRate);            // 與 ECL 同一份損失率
        Assert.Equal(40m, row.Provision);
    }

    [Fact]
    public async Task 呆帳提列_部分收款扣除_外幣以本幣計_帳齡依到期日()
    {
        // 開立很久但還沒到期：舊算法從出貨日起算會落在 180d+ 提 50%；依到期日是「未到期」0.5%。
        AddAr("NOTDUE", 4000, dueOffset: +10, invoiceOffset: -300);
        // USD 100 @ 30、已收 USD 40 → 本幣未收 1800，逾期 45 天 → 31-60（5%）
        AddAr("USD", 3000, received: 40, rate: 30, dueOffset: -45, total: 100);

        var rows = await BadDebtAsync();

        var notDue = Assert.Single(rows, r => r.AgingBucket == "Current");
        Assert.Equal(4000m, notDue.TotalAmount);
        Assert.Equal(20m, notDue.Provision);
        var usd = Assert.Single(rows, r => r.AgingBucket == "31-60");
        Assert.Equal(1800m, usd.TotalAmount);
        Assert.Equal(90m, usd.Provision);
    }

    // ── NRV ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nrv_售價以本幣每庫存單位加權平均()
    {
        _context.Products.Add(new Product { ProductCode = "P", ProductName = "P", StockUom = "PC" });
        _context.Inventories.Add(new Inventory { PlantId = Plant, ProductCode = "P", LocationCode = "L", QtyOnHand = 10, UnitCost = 100 });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _context.ARInvoices.AddRange(
            // 台幣：1 打（12 個）賣 960 → 本幣 960
            new ARInvoice { ARInvoiceId = "A1", CustomerCode = "C1", InvoiceDate = today.AddDays(-10), DueDate = today, ExchangeRate = 1, PlantId = Plant,
                Lines = { new ARInvoiceLine { ARInvoiceId = "A1", LineNo = 1, ProductCode = "P", UomCode = "DZ", Qty = 1, UnitPrice = 960, LineAmount = 960 } } },
            // 美金：12 個 × USD 2.5 = USD 30 @ 32 → 本幣 960
            new ARInvoice { ARInvoiceId = "A2", CustomerCode = "C1", InvoiceDate = today.AddDays(-5), DueDate = today, ExchangeRate = 32, PlantId = Plant,
                Lines = { new ARInvoiceLine { ARInvoiceId = "A2", LineNo = 1, ProductCode = "P", UomCode = "PC", Qty = 12, UnitPrice = 2.5m, LineAmount = 30 } } },
            // 作廢單：不列入
            new ARInvoice { ARInvoiceId = "A3", CustomerCode = "C1", InvoiceDate = today.AddDays(-5), DueDate = today, ExchangeRate = 1, PlantId = Plant, Status = ARInvoiceStatus.Void,
                Lines = { new ARInvoiceLine { ARInvoiceId = "A3", LineNo = 1, ProductCode = "P", UomCode = "PC", Qty = 1, UnitPrice = 1, LineAmount = 1 } } });
        _context.SaveChanges();

        var uom = new Mock<IUomService>();
        uom.Setup(u => u.ConvertAsync("P", "DZ", "PC", 1m, It.IsAny<CancellationToken>())).ReturnsAsync(12m);

        var rows = await new NrvAssessmentService(_context, uom.Object, TodayClock()).RunAssessmentAsync("202609", Plant);

        // (960 + 960) ÷ (12 + 12) = 80 本幣／個。舊算法：(960 + 2.5) ÷ 2 = 481.25（混了打與個、美金與台幣）。
        var row = Assert.Single(rows);
        Assert.Equal(80m, row.NrvPrice);
        Assert.Equal(1.6m, row.SellingCost);
        Assert.Equal(216m, row.ImpairmentLoss);   // (100 − 78.4) × 10
    }

    [Fact]
    public async Task Nrv_缺單位換算時錯誤要浮出來_不是靜默跳過()
    {
        _context.Products.Add(new Product { ProductCode = "P", ProductName = "P", StockUom = "PC" });
        _context.Inventories.Add(new Inventory { PlantId = Plant, ProductCode = "P", LocationCode = "L", QtyOnHand = 10, UnitCost = 100 });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _context.ARInvoices.Add(new ARInvoice { ARInvoiceId = "A1", CustomerCode = "C1", InvoiceDate = today, DueDate = today, ExchangeRate = 1, PlantId = Plant,
            Lines = { new ARInvoiceLine { ARInvoiceId = "A1", LineNo = 1, ProductCode = "P", UomCode = "BOX", Qty = 1, LineAmount = 500 } } });
        _context.SaveChanges();

        var uom = new Mock<IUomService>();
        uom.Setup(u => u.ConvertAsync("P", "BOX", "PC", It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new UomConversionNotFoundException("P", "BOX", "PC"));

        await Assert.ThrowsAsync<UomConversionNotFoundException>(() =>
            new NrvAssessmentService(_context, uom.Object, TodayClock()).RunAssessmentAsync("202609", Plant));
    }

    /// <summary>NRV 以當地營業日為基準（找不到期間時）；測試的銷售日期以今天往回推，這裡給今天。</summary>
    private static SmartFactory.MES.Services.IDateTimeService TodayClock()
    {
        var dt = new Moq.Mock<SmartFactory.MES.Services.IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(Moq.It.IsAny<string?>())).Returns(DateOnly.FromDateTime(DateTime.UtcNow));
        return dt.Object;
    }
}
