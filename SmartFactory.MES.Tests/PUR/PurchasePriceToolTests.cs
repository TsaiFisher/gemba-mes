using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 採購價格比較的查價口徑（2026-09-24）。
///
/// <para><b>為什麼這一檔到今天才有</b>：`VendorPriceLists` 在兩個庫都是 0 筆，
/// 所以「拿議定價去比」那段程式**從來沒有東西可比**——報表跑得出來、筆數也對，
/// 標準價欄一路空白，而空白看起來就像「這個組合沒報價」這個完全合理的狀態。
/// 補了議定價種子（`scripts/seed-vendor-pricelist.sql`）之後缺陷立刻現形。</para>
///
/// <para><b>固定裝置照抄開發庫的真實形狀</b>：`F001`／`MAF1103` 同時有
/// `NTD 187.45` 與 `USD 10.00` 兩筆現行有效的議定價（同一個廠商用兩種幣別報同一個品號，
/// 在進口料上很常見）。用「一個組合只有一種幣別」的乾淨假資料測，這裡每一支都會假綠。</para>
///
/// <para>委外那一側的對應物是 <see cref="OutsourcePriceToolTests"/>，
/// 兩支的第一個缺陷是同一個形狀（幣別），這不是巧合：報表在「比兩個數字」的時候，
/// 最容易掉的就是「這兩個數字是不是同一個單位」。</para>
/// </summary>
public class PurchasePriceToolTests : TestBase
{
    private const string PlantId  = "ADM";
    private const string Supplier = "F001";
    private const string Product  = "MAF1103";
    private const string BaseCcy  = "NTD";

    private static readonly DateOnly Day = new(2026, 9, 24);

    private readonly Mock<IDateTimeService> _dt = new();

    public PurchasePriceToolTests()
    {
        _context.CurrentPlantId = PlantId;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Day);
        _dt.Setup(d => d.Now).Returns(Day.ToDateTime(TimeOnly.MinValue));

        _context.Companies.Add(new Company
        {
            CompanyCode = Supplier, CompanyName = "供應商一", IsSupplier = true, IsActive = true,
        });

        // 同一組廠商×品號的兩筆現行有效議定價，幣別不同——開發庫的真實形狀。
        AddPrice(1, BaseCcy, 187.45m, new DateOnly(2026, 6, 1));
        AddPrice(2, "USD",   10.00m,  new DateOnly(2026, 6, 1));

        _context.SaveChanges();
    }

    private void AddPrice(int id, string ccy, decimal price, DateOnly from, DateOnly? to = null)
        => _context.VendorPriceLists.Add(new VendorPriceList
        {
            Id = id, SupplierCode = Supplier, ProductCode = Product,
            CurrencyCode = ccy, UnitPrice = price, ValidFrom = from, ValidTo = to, IsActive = true,
        });

    private void AddReceipt(string id, string? lineCcy, decimal unitPrice, string product = Product)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = id, ReceiptDate = Day.AddDays(-1), CreatedBy = "user1",
            SupplierCode = Supplier, Status = DocStatus.Posted, PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = id, LineNo = 1, ProductCode = product,
            ReceiptQty = 10m, StockQty = 10m, UomCode = "PC", StockUomCode = "PC",
            UnitPrice = unitPrice, CurrencyCode = lineCcy!,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    private PurchasePriceTool BuildTool() => new(
        _context,
        new RelativePeriodResolver(_dt.Object, _context),
        _dt.Object,
        GetMockCurrencyService(baseCurrency: BaseCcy).Object);

    private PurchasePriceRequest Request() => new()
    {
        PlantId        = PlantId,
        AbsolutePeriod = new DateRange(Day.AddDays(-30), Day),
    };

    /// <summary>
    /// <b>同一組廠商×品號有兩種幣別的議定價時，各自比各自的幣別。</b>
    ///
    /// <para>原本 `priceMap` 的鍵是 `(SupplierCode, ProductCode)`——幣別被丟掉——
    /// 再用未排序的 `.First()` 任取一筆。於是一筆 187.45 NTD 的進貨可能被拿去對
    /// `USD 10.00`，差異率算出 +1774%，而畫面上那一列每一格都填得好好的：
    /// 幣別欄印的是進貨的 NTD，標準價欄印的是 10，兩者放在一起看起來只像是「買貴了」。</para>
    ///
    /// <para>更糟的是它**不穩定**：`.First()` 取哪一筆由資料庫回傳順序決定，
    /// 同一份報表重跑可能得到不同的差異率，而沒有任何地方會說它換了一筆報價。</para>
    /// </summary>
    [Fact]
    public async Task 同組廠商品號有兩種幣別時各自比各自的幣別()
    {
        AddReceipt("GR-NTD", BaseCcy, 187.45m);
        AddReceipt("GR-USD", "USD",   10.00m);

        var rows = await BuildTool().ExecuteAsync(Request(), user: null);

        var ntd = Assert.Single(rows, r => r.GoodsReceiptId == "GR-NTD");
        var usd = Assert.Single(rows, r => r.GoodsReceiptId == "GR-USD");

        Assert.Equal(187.45m, ntd.StandardUnitPrice);
        Assert.Equal(10.00m,  usd.StandardUnitPrice);
    }

    /// <summary>
    /// <b>沒填幣別的進貨＝本位幣，不是「另一種幣別」。</b>
    ///
    /// <para>本幣單據習慣不標幣別（開發庫的委外收貨就整批是 `null`）。
    /// 不正規化的話這批會在 `priceMap` 裡找不到鍵，標準價落空——
    /// 而空白與「這個組合真的沒報價」在畫面上完全無法區分。
    /// 這是 `OutsourcePriceTool` 2026-09-23 修過的同一個形狀。</para>
    /// </summary>
    [Fact]
    public async Task 沒填幣別的進貨視為本位幣而非比不到價()
    {
        AddReceipt("GR-BLANK", null,  187.45m);
        AddReceipt("GR-EMPTY", "  ",  187.45m);

        var rows = await BuildTool().ExecuteAsync(Request(), user: null);

        Assert.All(rows, r => Assert.Equal(187.45m, r.StandardUnitPrice));
    }

    /// <summary>
    /// <b>現行有效的議定價不只一筆時，取最晚生效的那一筆。</b>
    ///
    /// <para>唯一索引是 `(SupplierCode, ProductCode, CurrencyCode, ValidFrom)`，
    /// 所以同一種幣別可以有兩筆同時有效的報價（調價時留舊的當歷史很常見）。
    /// 未排序的 `.First()` 在這裡也是運氣；`VendorPriceList` 自己的文件寫的是
    /// 「最接近有效的議定單價」，那就是最晚生效的那一筆。</para>
    /// </summary>
    [Fact]
    public async Task 同幣別有多筆現行有效報價時取最晚生效的()
    {
        AddPrice(3, BaseCcy, 200.00m, new DateOnly(2026, 9, 1));   // 較晚生效的調價
        _context.SaveChanges();

        AddReceipt("GR-NTD", BaseCcy, 187.45m);

        var rows = await BuildTool().ExecuteAsync(Request(), user: null);

        Assert.Equal(200.00m, Assert.Single(rows).StandardUnitPrice);
    }

    /// <summary>
    /// <b>沒有議定價的組合，標準價必須是 null 而不是 0。</b>
    ///
    /// <para>這條在種子腳本刻意留約 1/8 的組合不給議定價，就是為了讓它有資料走得到。
    /// `0` 與 `null` 在這裡是兩件事：`0` 會讓差異率變成除以零或 −100%，
    /// 而正確的呈現是「—」（無從比較）。</para>
    /// </summary>
    [Fact]
    public async Task 沒有議定價的組合標準價為null()
    {
        AddReceipt("GR-OTHER", BaseCcy, 50m, product: "NO-PRICE-PRODUCT");

        var rows = await BuildTool().ExecuteAsync(Request(), user: null);

        Assert.Null(Assert.Single(rows).StandardUnitPrice);
    }
}
