using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.PUR;

/// <summary>
/// 委外價格分析（2026-09-23）。
///
/// <para><b>這一檔的固定裝置刻意照抄開發庫的真實形狀</b>，因為第一版就是在那組資料上
/// 露餡的：報價寫著本位幣代碼、收貨明細的 <c>CurrencyCode</c> 是 <c>null</c>（本幣單沒特別標註）、
/// 狀態混著 <c>Confirmed</c> 與 <c>Posted</c>。兩個缺陷都只有在這個組合下才看得見，
/// 用「每筆都填好幣別、狀態都是 Posted」的乾淨假資料測，兩支都會全綠。</para>
/// </summary>
public class OutsourcePriceToolTests : TestBase
{
    private const string PlantId  = "ADM";
    private const string Supplier = "F001";
    private const string Product  = "SA1104";
    private const string BaseCcy  = "NTD";

    private static readonly DateOnly Day = new(2026, 9, 4);

    private readonly Mock<IDateTimeService> _dt = new();

    public OutsourcePriceToolTests()
    {
        _context.CurrentPlantId = PlantId;
        _dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(Day);
        _dt.Setup(d => d.Now).Returns(Day.ToDateTime(TimeOnly.MinValue));

        _context.Companies.Add(new Company
        {
            CompanyCode = Supplier, CompanyName = "委外廠一", IsSupplier = true, IsActive = true,
        });

        // 廠商＋品號層級的報價（RoutingId／OperationSeq 皆空），幣別寫本位幣代碼。
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "OQ-1", SupplierCode = Supplier, ProductCode = Product,
            UnitPrice = 25m, CurrencyCode = BaseCcy, UomCode = "PCS", IsActive = true,
            EffectiveFrom = new DateOnly(2026, 9, 3),
        });

        // 四筆收貨：兩筆 Confirmed、兩筆 Posted，CurrencyCode 一律 null（本幣單）。
        AddReceipt("OR-1", DocStatus.Confirmed);
        AddReceipt("OR-2", DocStatus.Confirmed);
        AddReceipt("OR-3", DocStatus.Posted);
        AddReceipt("OR-4", DocStatus.Posted);

        // 草稿不該進報表（開發庫裡那筆 E2E 殘留就是草稿，單價 99999999）。
        AddReceipt("OR-DRAFT", DocStatus.Draft, unitCost: 99999999m);

        _context.SaveChanges();
    }

    private void AddReceipt(string id, DocStatus status, decimal unitCost = 25m)
    {
        var rec = new OutsourceReceipt
        {
            OutsourceReceiptId = id, OutsourcePurchaseOrderId = "OPO-1",
            SupplierCode = Supplier, ReceiptDate = Day, Status = status, PlantId = PlantId,
        };
        rec.Lines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = id, LineNo = 1, ProductCode = Product,
            ReceiptQty = 10m, UnitCost = unitCost,
            CurrencyCode = null,        // ← 本幣單沒有特別標註幣別
            ExchangeRate = 1m,
        });
        _context.OutsourceReceipts.Add(rec);
    }

    private OutsourcePriceTool BuildTool() => new(
        _context,
        new RelativePeriodResolver(_dt.Object, _context),
        new OutsourceQuotationResolver(_context),
        GetMockCurrencyService(baseCurrency: BaseCcy).Object);

    private OutsourcePriceRequest Request() => new()
    {
        PlantId        = PlantId,
        AbsolutePeriod = new DateRange(Day.AddDays(-30), Day),
    };

    /// <summary>
    /// <b>沒填幣別＝本位幣，不是「另一種幣別」。</b>
    ///
    /// <para>第一版拿幣別字串直接比對，於是報價寫 <c>NTD</c>、收貨是 <c>null</c> 的四筆
    /// 全部落進「幣別不同 → 無法比價」，差異顯示「—」。而它們的單價與報價一模一樣，
    /// 正確答案是差異 0。這個錯法特別難發現：畫面看起來很合理（確實有一欄說無法比價），
    /// 而手冊上還寫著「『無法比價』不等於『差異 0』」——結果實作把可比的弄成不可比。</para>
    /// </summary>
    [Fact]
    public async Task 沒填幣別的收貨視為本位幣而非不可比()
    {
        var vm = await BuildTool().ExecuteAsync(Request(), user: null);

        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r =>
        {
            Assert.True(r.IsComparable,
                $"{r.OutsourceReceiptId} 被判成無法比價；幣別＝'{r.CurrencyCode}'、報價幣別＝'{r.QuotedCurrencyCode}'。"
                + "沒填幣別的本幣單必須視同本位幣。");
            Assert.Equal(0m, r.PriceDiff);
        });
    }

    /// <summary>
    /// <b>委外收貨的口徑是 Confirmed 以上，不是只有 Posted。</b>
    ///
    /// <para>採購金額分析與委外對帳報表都取 <c>Confirmed || Posted</c>。這張表第一版只取
    /// <c>Posted</c>，於是同一批收貨在採購金額分析看得到、在這裡看不到——兩邊都不報錯，
    /// 只是數字對不起來。開發庫裡剛好兩種狀態各半，正好照出它。</para>
    /// </summary>
    [Fact]
    public async Task 已確認與已過帳都要納入且草稿不納入()
    {
        var vm = await BuildTool().ExecuteAsync(Request(), user: null);

        var ids = vm.Rows.Select(r => r.OutsourceReceiptId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "OR-1", "OR-2", "OR-3", "OR-4" }, ids);
        Assert.DoesNotContain("OR-DRAFT", ids);
    }

    /// <summary>
    /// 查無適用報價時，差異必須是 <c>null</c>（畫面顯示「—」）而不是 0——
    /// 「這個廠商沒報過這個品號」與「剛好照報價收」在報表上絕不能長得一樣。
    /// </summary>
    [Fact]
    public async Task 查無報價時差異為空而不是零()
    {
        _context.OutsourceQuotations.RemoveRange(_context.OutsourceQuotations);
        _context.SaveChanges();

        var vm = await BuildTool().ExecuteAsync(Request(), user: null);

        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r =>
        {
            Assert.False(r.IsComparable);
            Assert.Null(r.PriceDiff);
            Assert.Null(r.QuotedUnitPrice);
        });
    }

    /// <summary>
    /// 月趨勢的平均以**收貨量加權**。一筆 1 個、一筆 100 個時，加權平均必須靠近後者；
    /// 簡單平均會被零星小單帶著跑。
    /// </summary>
    [Fact]
    public async Task 月趨勢平均以收貨量加權()
    {
        _context.OutsourceReceipts.RemoveRange(_context.OutsourceReceipts);
        _context.SaveChanges();

        AddWeighted("OR-W1", qty: 1m,   unitCost: 100m);
        AddWeighted("OR-W2", qty: 100m, unitCost: 10m);
        _context.SaveChanges();

        var vm = await BuildTool().ExecuteAsync(Request(), user: null);

        var month = Assert.Single(vm.MonthRows);
        // 加權：(100×1 + 10×100) / 101 = 10.891…；簡單平均會是 55。
        Assert.Equal(10.8911m, month.AvgUnitPriceLocal, 3);
        Assert.NotEqual(55m, month.AvgUnitPriceLocal);
    }

    private void AddWeighted(string id, decimal qty, decimal unitCost)
    {
        var rec = new OutsourceReceipt
        {
            OutsourceReceiptId = id, OutsourcePurchaseOrderId = "OPO-1",
            SupplierCode = Supplier, ReceiptDate = Day, Status = DocStatus.Posted, PlantId = PlantId,
        };
        rec.Lines.Add(new OutsourceReceiptLine
        {
            OutsourceReceiptId = id, LineNo = 1, ProductCode = Product,
            ReceiptQty = qty, UnitCost = unitCost, CurrencyCode = null, ExchangeRate = 1m,
        });
        _context.OutsourceReceipts.Add(rec);
    }
}
