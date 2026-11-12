using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.ORD.Services;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.ORD;

/// <summary>
/// 貿易條件（Incoterms）在採購單與銷貨訂單表頭（2026-09-24）。
///
/// <para><b>為什麼這一格值得測</b>：它不是一個參考欄位，它決定**關稅／運費／報關費
/// 該不該進我們的成本**——`EXW`／`FOB` 下是我們的（要經 `LandedCostAllocation`
/// 資本化），`DDP` 下一毛都不該進分攤單。而掛錯**不會有任何訊號**：
/// 單子照過、成本照算，只是那批料的單價永久多了或少了一截。</para>
///
/// <para><b>簽核摘要那幾支是重點。</b>共用投影 `CustomerDocHeader` 服務三張單
/// （銷貨訂單／出貨單／銷退單），但**只有訂單有貿易條件**——後兩張沿用訂單的條件、
/// 自己沒有這個欄位。漏掉這個區分的話，出貨單的摘要會出現一列空的或錯的貿易條件，
/// 而簽核人會照著它按核准。</para>
/// </summary>
public class TradeTermTests
{
    private const string Plant = "P1";

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.CurrentPlantId = Plant;
        return db;
    }

    /// <summary>把鍵原樣回傳，斷言才看得出用了哪個資源鍵。</summary>
    private sealed class KeyEchoLocalizer : IStringLocalizer<ProjectZSharedResource>
    {
        public LocalizedString this[string name] => new(name, name, false);
        public LocalizedString this[string name, params object[] a] => new(name, name, false);
        public IEnumerable<LocalizedString> GetAllStrings(bool p) => Array.Empty<LocalizedString>();
    }

    private static ICurrencyService Currency()
    {
        var m = new Mock<ICurrencyService>();
        m.Setup(c => c.GetBaseCurrencyAsync()).ReturnsAsync("TWD");
        return m.Object;
    }

    private static async Task SeedSalesOrderAsync(ApplicationDbContext db, string? tradeTerm)
    {
        db.SalesOrders.Add(new SalesOrder
        {
            SalesOrderId = "SO-1", CompanyCode = "C1", PlantId = Plant,
            TotalAmount = 500m, TradeTermCode = tradeTerm,
        });
        db.SalesOrderLines.Add(new SalesOrderLine
        {
            SalesOrderId = "SO-1", LineNo = 1, ProductCode = "A",
            OrderQty = 2, UomCode = "PCS", AmountLocal = 500m,
        });
        await db.SaveChangesAsync();
    }

    // ── 簽核摘要 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task 銷貨訂單的簽核摘要要帶出貿易條件()
    {
        using var db = NewDb();
        await SeedSalesOrderAsync(db, "CIF");

        var s = await new SalesOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
            .GetAsync(db, "SO-1");

        var row = Assert.Single(s!.Rows, r => r.Label == "TradeTerm");
        Assert.Equal("CIF", row.Value);
    }

    /// <summary>
    /// <b>沒約定就不該出現那一列</b>——國內交易本來就沒有 Incoterms。
    /// 硬印一列空白會讓簽核人以為系統漏帶了值。
    /// </summary>
    [Fact]
    public async Task 沒填貿易條件時摘要不出現那一列()
    {
        using var db = NewDb();
        await SeedSalesOrderAsync(db, null);

        var s = await new SalesOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
            .GetAsync(db, "SO-1");

        Assert.DoesNotContain(s!.Rows, r => r.Label == "TradeTerm");
    }

    /// <summary>
    /// <b>出貨單與銷退單沒有自己的貿易條件</b>，摘要不得出現那一列。
    ///
    /// <para>它們與銷貨訂單共用 `CustomerDocHeader` 投影，所以這一條守的是
    /// 「共用投影沒有把訂單的欄位滲到另外兩張單上」。兩張單的投影**明寫 null**
    /// （EF 的 expression tree 不接受選用參數），這條測試就是那個 null 的守門。</para>
    /// </summary>
    [Theory]
    [InlineData("GoodsShipment")]
    [InlineData("SalesReturn")]
    public async Task 出貨單與銷退單的摘要不出現貿易條件(string docType)
    {
        using var db = NewDb();
        var l = new KeyEchoLocalizer();

        if (docType == "GoodsShipment")
        {
            db.GoodsShipments.Add(new GoodsShipment
            {
                GoodsShipmentId = "GS-1", CompanyCode = "C1", PlantId = Plant, TotalAmount = 100m,
            });
            db.GoodsShipmentLines.Add(new GoodsShipmentLine
            {
                GoodsShipmentId = "GS-1", LineNo = 1, ProductCode = "A",
                ShipQty = 1, UomCode = "PCS", AmountLocal = 100m,
            });
            await db.SaveChangesAsync();

            var s = await new GoodsShipmentApprovalSummary(l, Currency()).GetAsync(db, "GS-1");
            Assert.DoesNotContain(s!.Rows, r => r.Label == "TradeTerm");
        }
        else
        {
            db.SalesReturns.Add(new SalesReturn
            {
                SalesReturnId = "SR-1", CompanyCode = "C1", PlantId = Plant, TotalAmount = 100m,
            });
            db.SalesReturnLines.Add(new SalesReturnLine
            {
                SalesReturnId = "SR-1", LineNo = 1, ProductCode = "A",
                ReturnQty = 1, UomCode = "PCS", AmountLocal = 100m,
            });
            await db.SaveChangesAsync();

            var s = await new SalesReturnApprovalSummary(l, Currency()).GetAsync(db, "SR-1");
            Assert.DoesNotContain(s!.Rows, r => r.Label == "TradeTerm");
        }
    }

    [Fact]
    public async Task 採購單的簽核摘要要帶出貿易條件()
    {
        using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-1", SupplierCode = "S1", PlantId = Plant,
            CreatedBy = "u1", TotalAmount = 300m, TradeTermCode = "FOB",
        });
        db.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = "PO-1", LineNo = 1, ProductCode = "A",
            OrderQty = 3, UomCode = "PCS", AmountLocal = 300m,
        });
        await db.SaveChangesAsync();

        var s = await new PurchaseOrderApprovalSummary(new KeyEchoLocalizer(), Currency())
            .GetAsync(db, "PO-1");

        var row = Assert.Single(s!.Rows, r => r.Label == "TradeTerm");
        Assert.Equal("FOB", row.Value);
    }

    // ── 持久化 ───────────────────────────────────────────────────────────

    /// <summary>
    /// <b>存得進去也讀得回來。</b>看似多餘，但表單欄位「填了存檔卻沒進資料庫」
    /// 正是本專案踩過的形狀（`MaterialSlip` 綁 entity 導致手動建單靜默失敗），
    /// 而症狀是**沒有任何錯誤訊息**、單子看起來存好了。
    /// </summary>
    [Fact]
    public async Task 兩張單的貿易條件都存得進去也讀得回來()
    {
        using var db = NewDb();
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            PurchaseOrderId = "PO-1", SupplierCode = "S1", PlantId = Plant,
            CreatedBy = "u1", TradeTermCode = "DDP",
        });
        await SeedSalesOrderAsync(db, "EXW");

        Assert.Equal("DDP", (await db.PurchaseOrders.AsNoTracking()
            .SingleAsync(p => p.PurchaseOrderId == "PO-1")).TradeTermCode);
        Assert.Equal("EXW", (await db.SalesOrders.AsNoTracking()
            .SingleAsync(o => o.SalesOrderId == "SO-1")).TradeTermCode);
    }
}
