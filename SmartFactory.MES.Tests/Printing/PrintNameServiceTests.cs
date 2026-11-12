using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared.Printing;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.PUR.Models;
using Xunit;

namespace SmartFactory.MES.Tests.Printing;

/// <summary>列印單代碼配名稱：代碼依屬性名收集（表頭＋明細一層），查回名稱；查不到原樣顯示。</summary>
public class PrintNameServiceTests
{
    private static ApplicationDbContext Seed()
    {
        var ctx = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) { CurrentPlantId = "P1" };
        ctx.Products.AddRange(
            new Product { ProductCode = "A-01", ProductName = "鋁板" },
            new Product { ProductCode = "B-02", ProductName = "螺絲" },
            new Product { ProductCode = "Z-99", ProductName = "沒用到的品號" });
        ctx.Companies.Add(new Company { CompanyCode = "S001", CompanyName = "大成金屬" });
        ctx.Uoms.Add(new Uom { UomCode = "PCS", UomName = "個" });
        ctx.Uoms.Add(new Uom { UomCode = "KG", UomName = "KG" });
        ctx.Currencies.Add(new Currency { CurrencyCode = "USD", CurrencyName = "美元" });
        ctx.Taxes.Add(new Tax { TaxCode = "T5", TaxName = "應稅 5%" });
        ctx.SaveChanges();
        return ctx;
    }

    private static PurchaseOrder Po() => new()
    {
        PurchaseOrderId = "PO-1", SupplierCode = "S001",
        Lines =
        {
            new PurchaseOrderLine { LineNo = 1, ProductCode = "A-01", UomCode = "PCS", CurrencyCode = "USD", TaxCode = "T5" },
            new PurchaseOrderLine { LineNo = 2, ProductCode = "B-02", UomCode = "KG", StockUomCode = "PCS", CurrencyCode = "USD" },
            new PurchaseOrderLine { LineNo = 3, ProductCode = "NOPE", UomCode = "BOX" },
        }
    };

    [Fact]
    public async Task HeaderAndLineCodes_AreResolvedToNames()
    {
        using var ctx = Seed();
        var names = await new PrintNameService(ctx).ForAsync(Po());

        Assert.Equal("鋁板", names.Product("A-01"));
        Assert.Equal("螺絲", names.Product("B-02"));
        Assert.Equal("大成金屬", names.Company("S001"));
        Assert.Equal("S001 大成金屬", names.CompanyWith("S001"));
        Assert.Equal("PCS 個", names.UomWith("PCS"));
        Assert.Equal("USD 美元", names.CurrencyWith("USD"));
        Assert.Equal("T5 應稅 5%", names.TaxWith("T5"));
    }

    [Fact]
    public async Task UnknownOrSameAsCode_FallsBackToCode_NeverBlank()
    {
        using var ctx = Seed();
        var names = await new PrintNameService(ctx).ForAsync(Po());

        Assert.Equal("", names.Product("NOPE"));     // 品名欄留空，品號欄仍有代碼
        Assert.Equal("BOX", names.UomWith("BOX"));    // 主檔沒有 → 只印代碼
        Assert.Equal("KG", names.UomWith("KG"));      // 名稱與代碼相同 → 不重複印「KG KG」
        Assert.Equal("", names.CompanyWith(null));
    }

    [Fact]
    public async Task OnlyCodesOnTheDocument_AreLoaded()
    {
        using var ctx = Seed();
        var names = await new PrintNameService(ctx).ForAsync(Po());
        Assert.Equal("", names.Product("Z-99"));
    }

    [Fact]
    public async Task ExtraObjects_AreCollectedToo()
    {
        using var ctx = Seed();
        var lines = new List<PurchaseOrderLine> { new() { ProductCode = "Z-99" } };
        var names = await new PrintNameService(ctx).ForAsync(new PurchaseOrder(), lines);
        Assert.Equal("沒用到的品號", names.Product("Z-99"));
    }

    [Fact]
    public async Task Account_UsesEnglishNameOnEnglishUi_WhenAvailable()
    {
        using var ctx = Seed();
        ctx.ChartOfAccounts.Add(new ChartOfAccount { AccountCode = "1101", AccountName = "現金", AccountNameEn = "Cash" });
        ctx.SaveChanges();
        var doc = new { AccountCode = "1101" };

        var saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("Cash", (await new PrintNameService(ctx).ForAsync(doc)).Account("1101"));
            CultureInfo.CurrentUICulture = new CultureInfo("zh-TW");
            Assert.Equal("現金", (await new PrintNameService(ctx).ForAsync(doc)).Account("1101"));
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }
}
