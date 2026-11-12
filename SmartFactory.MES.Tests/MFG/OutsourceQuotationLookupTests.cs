using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Controllers;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.MFG;

/// <summary>
/// 委外採購單建立時的報價 4-level 查詢（<c>OutsourceQuotation</c> 類別註解／pur-domain.md）。
///
/// 規格定義的特殊度順序是：
///   ① Supplier + Product + Routing + OperationSeq　② Supplier + Product + Routing
///   ③ Supplier + Product　　　　　　　　　　　　　　④ Supplier（<c>ProductCode == null</c> ＝ 廠商層級通用）
///
/// 這段查詢沒有任何測試，而它的產物是**單價快照**：報價 → 委外採購單 → 委外收貨明細
/// → 加工費暫估 → AP。查錯報價的後果不是錯誤訊息，是**一個看起來正常的金額**
/// 一路走到應付，且 `OUTSOURCE_NEG_BALANCE`、傳票平衡、分批收貨的成本斷言全部沉默
/// ——它們驗的是「這個單價有沒有被一致地帶下去」，不是「這個單價對不對」。
///
/// 因此本檔的斷言對象刻意是**選中哪一筆報價**（用可辨識的單價值當指紋），
/// 不是金額有沒有被搬運。
/// </summary>
public class OutsourceQuotationLookupTests : TestBase
{
    private const string Plant    = "P001";
    private const string Supplier = "V001";
    private const string WoId     = "WO2026OQ1";
    private const string Product  = "SA-001";
    private const string Routing  = "RT-001";
    private const int    Op       = 20;

    private readonly Mock<IUserContext> _user = new();
    private readonly Mock<IDocNumberService> _docNumber = new();

    public OutsourceQuotationLookupTests()
    {
        _docNumber.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"D{Guid.NewGuid():N}".Substring(0, 12));

        _context.Set<Product>().Add(new Product
            { ProductCode = Product, ProductName = "半成品", StockUom = "PCS", IsActive = true });
        _context.WorkOrders.Add(new WorkOrder
        {
            WorkOrderId = WoId, ProductCode = Product, RoutingId = Routing,
            Quantity = 100m, PlantId = Plant, Status = WorkOrderStatus.Released,
            PlannedEndDate = new DateTime(2026, 10, 1),
        });
        _context.SaveChanges();
    }

    private OutsourcePurchaseOrderController BuildController()
    {
        _context.CurrentPlantId = Plant;
        _user.Setup(u => u.PlantId).Returns(Plant);
        _user.Setup(u => u.UserId).Returns("usr1");

        return new OutsourcePurchaseOrderController(
            GetMockLocalizer<ProjectZSharedResource>().Object,
            _docNumber.Object,
            new Mock<IUomService>().Object,
            new OutsourceQuotationResolver(_context))
        {
            ControllerContext = GetControllerContextWithServices(userContext: _user.Object),
        };
    }

    /// <summary>
    /// 單價即指紋：每一筆報價給一個不重複的價格，斷言時就看「拿到的是哪一個價」。
    /// </summary>
    private void SeedQuote(decimal unitPrice, string? productCode, string? routingId, int? operationSeq)
    {
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId  = Guid.NewGuid().ToString("N"),
            SupplierCode = Supplier,
            ProductCode  = productCode,
            RoutingId    = routingId,
            OperationSeq = operationSeq,
            UnitPrice    = unitPrice,
            CurrencyCode = "TWD",
            IsActive     = true,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            EffectiveTo   = null,
        });
        _context.SaveChanges();
    }

    private async Task<OutsourcePurchaseOrderLine> ConvertAndGetLineAsync()
    {
        await BuildController().Convert(new[] { $"|{WoId}|{Op}|0" }, Supplier, expectedDate: null);

        return await _context.OutsourcePurchaseOrderLines
            .IgnoreQueryFilters().AsNoTracking().SingleAsync();
    }

    // ── 特殊度排序：越精確的勝出 ────────────────────────────────────────

    [Fact]
    public async Task Convert_ProductSpecificQuote_BeatsSupplierLevelQuote()
    {
        SeedQuote(10m, productCode: null,    routingId: null, operationSeq: null);   // ④ 廠商層級
        SeedQuote(30m, productCode: Product, routingId: null, operationSeq: null);   // ③ 品號

        Assert.Equal(30m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    [Fact]
    public async Task Convert_RoutingSpecificQuote_BeatsProductOnlyQuote()
    {
        SeedQuote(30m, productCode: Product, routingId: null,    operationSeq: null);  // ③
        SeedQuote(50m, productCode: Product, routingId: Routing, operationSeq: null);  // ②

        Assert.Equal(50m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    [Fact]
    public async Task Convert_OperationSpecificQuote_IsTheMostSpecific()
    {
        SeedQuote(30m, productCode: Product, routingId: null,    operationSeq: null);  // ③
        SeedQuote(50m, productCode: Product, routingId: Routing, operationSeq: null);  // ②
        SeedQuote(70m, productCode: Product, routingId: Routing, operationSeq: Op);    // ①

        Assert.Equal(70m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    // ── 不適用的報價必須被剔除，而不是排在後面 ──────────────────────────
    //
    // 排序（OrderByDescending）與過濾（Where）在「有比較對象」時看起來等價，
    // 差別只在**清單裡只剩不適用的那一筆**時：排序照樣回傳它，過濾才會回傳「沒有」。
    // 而那正是現場最常見的狀態——廠商只報過某幾個品號的價。

    /// <summary>
    /// 🚨 別的品號的報價不得被採用。廠商層級通用報價（<c>ProductCode == null</c>）才是
    /// 規格認可的 fallback；「這家廠商報過的隨便一個品號的價」不是。
    /// 這一支紅的時候，症狀是委外單帶進一個**別的零件**的加工費。
    /// </summary>
    [Fact]
    public async Task Convert_QuoteOfAnotherProduct_IsNotUsed()
    {
        SeedQuote(999m, productCode: "SA-OTHER", routingId: null, operationSeq: null);

        Assert.Equal(0m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    /// <summary>
    /// 🚨 別的途程的報價不得被採用。原實作只看「RoutingId 有沒有填」而不比對值，
    /// 於是一筆別途程的報價（+2 分）會勝過正確的廠商層級通用報價（0 分）。
    /// </summary>
    [Fact]
    public async Task Convert_QuoteOfAnotherRouting_IsNotUsed()
    {
        SeedQuote(10m,  productCode: Product, routingId: null,        operationSeq: null);  // 正解
        SeedQuote(999m, productCode: Product, routingId: "RT-OTHER",  operationSeq: null);

        Assert.Equal(10m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    /// <summary>
    /// 別的工序的報價不得被採用。
    ///
    /// ⚠️ 不適用的那筆**刻意先 seed**：原實作只給「工序相符」加分，於是
    /// 「別的工序」與「不限工序」同分，`FirstOrDefault` 取的是清單順序第一筆。
    /// 正解排前面的話這一支會綠——綠在順序上，不是綠在邏輯上。
    /// </summary>
    [Fact]
    public async Task Convert_QuoteOfAnotherOperation_IsNotUsed()
    {
        SeedQuote(999m, productCode: Product, routingId: Routing, operationSeq: 99);
        SeedQuote(10m,  productCode: Product, routingId: Routing, operationSeq: null);   // 正解（不限工序）

        Assert.Equal(10m, (await ConvertAndGetLineAsync()).UnitPrice);
    }

    /// <summary>
    /// 完全沒有可用報價時單價為 0，且 <c>AllowedScrapRate</c> 必須是 null
    /// ——null ≠ 0，前者是「沒談過損耗條款 → 不扣款」（見 <c>OutsourceQuotation</c> 註解）。
    /// 把不適用的報價剔除之後，這條路徑才真的走得到。
    /// </summary>
    [Fact]
    public async Task Convert_NoApplicableQuote_LeavesPriceZeroAndScrapRateNull()
    {
        var line = await ConvertAndGetLineAsync();

        Assert.Equal(0m, line.UnitPrice);
        Assert.Null(line.AllowedScrapRate);
    }

    /// <summary>
    /// 過期與停用的報價不得被採用——這兩個過濾在資料庫端（<c>quotationLookup</c> 的
    /// Where），與上面的特殊度排序是兩段不同的程式，各自都會靜默失效。
    /// </summary>
    [Fact]
    public async Task Convert_ExpiredOrInactiveQuote_IsNotUsed()
    {
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "Q-EXPIRED", SupplierCode = Supplier, ProductCode = Product,
            UnitPrice = 888m, CurrencyCode = "TWD", IsActive = true,
            EffectiveFrom = new DateOnly(2020, 1, 1), EffectiveTo = new DateOnly(2020, 12, 31),
        });
        _context.OutsourceQuotations.Add(new OutsourceQuotation
        {
            QuotationId = "Q-INACTIVE", SupplierCode = Supplier, ProductCode = Product,
            UnitPrice = 777m, CurrencyCode = "TWD", IsActive = false,
            EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = null,
        });
        _context.SaveChanges();

        Assert.Equal(0m, (await ConvertAndGetLineAsync()).UnitPrice);
    }
}
