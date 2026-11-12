using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.BAS;

/// <summary>
/// 品號可用單位（<see cref="UomService.GetProductUomsAsync"/>）與換算成庫存數量（<see cref="UomService.ToStockQtyAsync"/>）。
///
/// <para><b>為什麼要補（2026-10-12）</b>：單據明細的單位下拉原本列出全部單位，而庫存八張單據、內部訂單、
/// 委外收貨的存檔直接採信畫面的 StockQty／StockUomCode（畫面從來不送）——選了「箱」就把箱數當個數入帳。
/// 下拉改成只列可用單位、存檔改由伺服器換算；兩者必須用同一套規則，否則會列出一個存檔必定失敗的單位。</para>
/// </summary>
public class UomServiceProductUomsTests : TestBase
{
    private UomService Svc() => new(_context, new MemoryCache(new MemoryCacheOptions()));

    public UomServiceProductUomsTests()
    {
        _context.Products.Add(new Product { ProductCode = "P1", ProductName = "P1", StockUom = "PC", PurchaseUom = "BOX", SalesUom = "DZ" });
        _context.Products.Add(new Product { ProductCode = "P2", ProductName = "P2", StockUom = "PC", PurchaseUom = "CTN" });
        _context.Products.Add(new Product { ProductCode = "NOSTOCK", ProductName = "x", PurchaseUom = "BOX" });
        _context.ProductUomConversions.Add(new ProductUomConversion { ProductCode = "P1", FromUomCode = "BOX", ToUomCode = "PC", Numerator = 12 });
        _context.UomConversions.Add(new UomConversion { FromUomCode = "PC", ToUomCode = "DZ", Numerator = 1, Denominator = 12 });
        _context.UomConversions.Add(new UomConversion { FromUomCode = "KG", ToUomCode = "G", Numerator = 1000 });
        _context.SaveChanges();
    }

    [Fact]
    public async Task 可用單位_庫存單位加上與它查得到換算的單位_依庫存採購銷售排前面()
    {
        var o = await Svc().GetProductUomsAsync("P1");

        Assert.NotNull(o);
        Assert.Equal(new[] { "PC", "BOX", "DZ" }, o!.Uoms);
        Assert.Equal("PC", o.StockUom);
    }

    [Fact]
    public async Task 品號特定換算只算該品號_別的品號看不到()
    {
        var o = await Svc().GetProductUomsAsync("P2");

        // P1 的 BOX→PC 不適用 P2；P2 的採購單位 CTN 沒有換算，列出來就是一張存檔必定失敗的單
        Assert.Equal(new[] { "PC", "DZ" }, o!.Uoms);
        Assert.DoesNotContain("CTN", o.Uoms);
        Assert.DoesNotContain("KG", o.Uoms);
    }

    [Fact]
    public async Task 列出的每個單位都換算得成功_與存檔同一套規則()
    {
        var svc = Svc();
        foreach (var code in new[] { "P1", "P2" })
            foreach (var u in (await svc.GetProductUomsAsync(code))!.Uoms)
                await svc.ToStockQtyAsync(code, u, 1m);   // 不得擲 UomConversionNotFoundException
    }

    [Fact]
    public async Task 品號不存在回null()
        => Assert.Null(await Svc().GetProductUomsAsync("NOPE"));

    [Fact]
    public async Task 換算成庫存數量_庫存單位取主檔()
    {
        var (uom, qty) = await Svc().ToStockQtyAsync("P1", "BOX", 2m);

        Assert.Equal("PC", uom);
        Assert.Equal(24m, qty);
    }

    [Fact]
    public async Task 單位與庫存單位相同或沒填_不換算()
    {
        Assert.Equal(("PC", 5m), await Svc().ToStockQtyAsync("P1", "PC", 5m));
        Assert.Equal(("PC", 5m), await Svc().ToStockQtyAsync("P1", null, 5m));
    }

    [Fact]
    public async Task 查不到換算_擲出專屬例外而不是照原數量入帳()
        => await Assert.ThrowsAsync<UomConversionNotFoundException>(() => Svc().ToStockQtyAsync("P2", "CTN", 1m));

    [Fact]
    public async Task 品號沒有庫存單位_以明細單位為準_相容舊資料()
        => Assert.Equal(("BOX", 3m), await Svc().ToStockQtyAsync("NOSTOCK", "BOX", 3m));
}
