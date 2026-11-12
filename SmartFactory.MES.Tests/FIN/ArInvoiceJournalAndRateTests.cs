using Microsoft.Extensions.Caching.Memory;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// AR 開票與退貨的**分錄科目**與**匯率取得方式**（manual-test-plan.md 3-6、3-7）。
///
/// 這裡刻意用**真的** <see cref="CurrencyService"/> 加上有買賣價差的匯率資料，
/// 而不是 mock 匯率：買賣兩邊都回同一個值的 mock 會讓「取錯 RateType」這種錯誤
/// 完全測不出來，而那正是本檔要守的東西。
///
/// 背景（2026-08-31 查到的缺陷，已於同一批修掉）：出貨存檔時
/// <c>GoodsShipmentController.ResolveLineRatesAsync</c> 已用**賣出匯率**取得後端權威匯率
/// 並寫進 <c>GoodsShipmentLine.ExchangeRate</c>；但 <c>GenerateFromShipmentAsync</c>
/// 原本把它丟掉、用 <c>GetRateAsync</c> 的**預設 Buy**（採購方向）重查一次。
/// 後果是每一張外幣 AR 都天生帶著一筆等於買賣價差的匯兌損益——收款端用 Sell、
/// 開票端用 Buy，兩端永遠對不起來，而且發票本幣金額與出貨單的 AmountLocal 也對不上。
/// </summary>
public class ArInvoiceJournalAndRateTests : TestBase
{
    private const string PlantId  = "P001";
    private const string CustCode = "CUST001";

    /// <summary>刻意讓買賣價差夠大，取錯邊一定看得出來。</summary>
    private const decimal BuyRate  = 30m;
    private const decimal SellRate = 32.5m;

    private static readonly DateOnly ShipDate = new DateOnly(2026, 3, 1);

    private readonly ARInvoiceService _svc;
    private readonly List<(string DocType, string DocId, IList<JournalLineRequest> Lines)> _journals = new();
    private int _arSeq;

    public ArInvoiceJournalAndRateTests()
    {
        _context.CurrentPlantId = PlantId;

        var sysParam = new Mock<ISystemParamService>();
        sysParam.Setup(x => x.GetAsync(ProjectZSystemParamKeys.BaseCurrency)).ReturnsAsync("TWD");
        var currencySvc = new CurrencyService(_context, sysParam.Object, new MemoryCache(new MemoryCacheOptions()));

        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("ARInvoice", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.FromResult($"ARI{++_arSeq:D6}"));

        var dt = new Mock<IDateTimeService>();
        dt.Setup(d => d.GetLocalDate(It.IsAny<string?>())).Returns(ShipDate);

        var journalSvc = new Mock<IJournalEntryService>();
        journalSvc.Setup(j => j.CreateAutoEntryAsync(
                      It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<string>(),
                      It.IsAny<IList<JournalLineRequest>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, DateOnly, string, IList<JournalLineRequest>, string?, CancellationToken>(
                      (docType, docId, _, _, lines, _, _) => _journals.Add((docType, docId, lines)))
                  .ReturnsAsync(new JournalEntry());

        _svc = new ARInvoiceService(_context, docNo.Object, dt.Object, currencySvc,
            periodSvc: null, journalSvc: journalSvc.Object);

        _context.Currencies.AddRange(
            new Currency { CurrencyCode = "TWD", CurrencyName = "新台幣", Symbol = "NT$", DecimalPlaces = 0, IsActive = true },
            new Currency { CurrencyCode = "USD", CurrencyName = "美元",   Symbol = "$",   DecimalPlaces = 2, IsActive = true });
        _context.CurrencyRates.Add(new CurrencyRate
        {
            CurrencyCode = "USD", RateDate = ShipDate.AddDays(-1), BuyRate = BuyRate, SellRate = SellRate,
        });
        _context.Companies.Add(new Company { CompanyCode = CustCode, CompanyName = "測試客戶", IsCustomer = true, IsActive = true });
        _context.SaveChanges();
    }

    /// <param name="lineRate">出貨行的匯率快照。1 代表舊資料未回填。</param>
    private void SeedShipment(string shipId, decimal shipQty, decimal unitPrice,
        decimal taxRate = 0m, string? currency = null, decimal lineRate = 1m)
    {
        var ship = new GoodsShipment
        {
            GoodsShipmentId = shipId, ShipDate = ShipDate, CreatedBy = "user1",
            CompanyCode = CustCode, Status = DocStatus.Posted,
            ApprovalStatus = DocumentApprovalStatus.Approved, PlantId = PlantId,
        };
        ship.Lines.Add(new GoodsShipmentLine
        {
            GoodsShipmentId = shipId, LineNo = 1, ProductCode = "FG001",
            ShipQty = shipQty, UomCode = "PC", StockQty = shipQty, StockUomCode = "PC",
            UnitPrice = unitPrice, TaxRate = taxRate,
            CurrencyCode = currency, ExchangeRate = lineRate,
        });
        _context.GoodsShipments.Add(ship);
        _context.SaveChanges();
    }

    private IList<JournalLineRequest> JournalOf(string docType)
        => Assert.Single(_journals.Where(j => j.DocType == docType)).Lines;

    private static decimal Debit(IList<JournalLineRequest> lines, string account)
        => lines.Where(l => l.AccountCode == account).Sum(l => l.Debit);

    private static decimal Credit(IList<JournalLineRequest> lines, string account)
        => lines.Where(l => l.AccountCode == account).Sum(l => l.Credit);

    private static void AssertBalanced(IList<JournalLineRequest> lines)
        => Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));

    // ── 開票分錄 ────────────────────────────────────────────────────────────

    /// <summary>3-3／3-5：借 應收帳款（含稅）／貸 銷貨收入（淨額）＋貸 銷項稅額。</summary>
    [Fact]
    public async Task Generate_JournalDebitsReceivable_CreditsRevenueAndOutputVat()
    {
        SeedShipment("GS-J1", shipQty: 100m, unitPrice: 300m, taxRate: 0.05m);

        await _svc.GenerateFromShipmentAsync("GS-J1");

        var lines = JournalOf("ARInvoice");
        Assert.Equal(31500m, Debit(lines,  StandardAccounts.AccountsReceivableCode));  // 含稅
        Assert.Equal(30000m, Credit(lines, StandardAccounts.SalesRevenueCode));        // 淨額
        Assert.Equal(1500m,  Credit(lines, StandardAccounts.OutputVatCode));           // 稅
        Assert.Equal(0m,     Credit(lines, StandardAccounts.AccountsReceivableCode));
        AssertBalanced(lines);
    }

    /// <summary>免稅品：不得憑空生出一行金額 0 的銷項稅額。</summary>
    [Fact]
    public async Task Generate_NoTax_OmitsOutputVatLine()
    {
        SeedShipment("GS-J2", shipQty: 10m, unitPrice: 100m);

        await _svc.GenerateFromShipmentAsync("GS-J2");

        var lines = JournalOf("ARInvoice");
        Assert.DoesNotContain(lines, l => l.AccountCode == StandardAccounts.OutputVatCode);
        Assert.Equal(1000m, Debit(lines,  StandardAccounts.AccountsReceivableCode));
        Assert.Equal(1000m, Credit(lines, StandardAccounts.SalesRevenueCode));
        AssertBalanced(lines);
    }

    // ── 匯率取得方式（本檔重點）────────────────────────────────────────────

    /// <summary>
    /// 3-7：外幣開票必須**沿用出貨行的匯率快照**（出貨端已用 Sell 取得），
    /// 不得自己用預設的 Buy 重查。買 30／賣 32.5，取錯邊差 8%。
    /// </summary>
    [Fact]
    public async Task Generate_ForeignCurrency_UsesShipmentLineRateSnapshot_NotBuyRate()
    {
        SeedShipment("GS-FX1", shipQty: 10m, unitPrice: 100m, currency: "USD", lineRate: SellRate);

        var inv = await _svc.GenerateFromShipmentAsync("GS-FX1");

        Assert.Equal(SellRate, inv.ExchangeRate);
        Assert.Equal(1000m,    inv.TotalAmount);      // 交易幣
        Assert.Equal(32500m,   inv.AmountLocal);      // 1000 × 32.5，非 30000
        Assert.Equal(32500m,   Debit(JournalOf("ARInvoice"), StandardAccounts.AccountsReceivableCode));
    }

    /// <summary>
    /// 舊資料出貨行沒有回填匯率（停在預設值 1）時才退回查表，
    /// 而查表也必須指定 **Sell**——退回查表不是退回用採購方向。
    /// </summary>
    [Fact]
    public async Task Generate_ForeignCurrency_WithoutSnapshot_FallsBackToSellRate()
    {
        SeedShipment("GS-FX2", shipQty: 10m, unitPrice: 100m, currency: "USD", lineRate: 1m);

        var inv = await _svc.GenerateFromShipmentAsync("GS-FX2");

        Assert.Equal(SellRate, inv.ExchangeRate);
        Assert.NotEqual(BuyRate, inv.ExchangeRate);
        Assert.Equal(32500m, inv.AmountLocal);
    }

    // ── 退貨反向分錄 ────────────────────────────────────────────────────────

    /// <summary>3-6：退貨分錄與開票**完全相反**——借 銷貨收入＋借 銷項稅額／貸 應收帳款。</summary>
    [Fact]
    public async Task Reverse_PostsMirrorJournal_DebitsRevenueAndVat_CreditsReceivable()
    {
        SeedShipment("GS-R1", shipQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        await _svc.GenerateFromShipmentAsync("GS-R1");

        await _svc.ReverseForReturnedQtyAsync("GS-R1", 1, 40m);   // 退 40%

        var rev = JournalOf("ARInvoiceReturn");
        Assert.Equal(12000m, Debit(rev,  StandardAccounts.SalesRevenueCode));        // 40 × 300
        Assert.Equal(600m,   Debit(rev,  StandardAccounts.OutputVatCode));           // 12000 × 5%
        Assert.Equal(12600m, Credit(rev, StandardAccounts.AccountsReceivableCode));
        Assert.Equal(0m,     Debit(rev,  StandardAccounts.AccountsReceivableCode));
        AssertBalanced(rev);
    }

    /// <summary>
    /// 3-6：全額退回後，開票與退貨兩張分錄逐科目相加必須歸零。
    /// 「只沖一半」是 §7.1 4-3 點名的壞法，這裡用淨額釘住。
    ///
    /// 注意本服務只負責 AR 與營收兩腿；**庫存回補與 COGS 沖回不在這裡**
    /// （走 INV 過帳，見 InventoryPostingService），故 §6.3 的三方向沖回
    /// 仍需搭配庫存側的測試才算完整。
    /// </summary>
    [Fact]
    public async Task Reverse_FullReturn_NetsRevenueVatAndReceivableToZero()
    {
        SeedShipment("GS-R2", shipQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        await _svc.GenerateFromShipmentAsync("GS-R2");

        await _svc.ReverseForReturnedQtyAsync("GS-R2", 1, 100m);

        var gen = JournalOf("ARInvoice");
        var rev = JournalOf("ARInvoiceReturn");

        foreach (var account in new[]
        {
            StandardAccounts.AccountsReceivableCode,
            StandardAccounts.SalesRevenueCode,
            StandardAccounts.OutputVatCode,
        })
        {
            var net = Debit(gen, account) - Credit(gen, account)
                    + Debit(rev, account) - Credit(rev, account);
            Assert.Equal(0m, net);
        }

        var inv = await _context.ARInvoices.FirstAsync(i => i.SourceDocId == "GS-R2");
        Assert.Equal(0m, inv.TotalAmount);
    }

    /// <summary>外幣退貨的反向分錄，本幣金額必須用**發票當時**的匯率，不是退貨當下再查一次。</summary>
    [Fact]
    public async Task Reverse_ForeignCurrency_UsesInvoiceRate_NotAFreshLookup()
    {
        SeedShipment("GS-R3", shipQty: 10m, unitPrice: 100m, currency: "USD", lineRate: SellRate);
        await _svc.GenerateFromShipmentAsync("GS-R3");

        await _svc.ReverseForReturnedQtyAsync("GS-R3", 1, 10m);   // 全退

        var rev = JournalOf("ARInvoiceReturn");
        Assert.Equal(32500m, Debit(rev,  StandardAccounts.SalesRevenueCode));      // 1000 × 32.5
        Assert.Equal(32500m, Credit(rev, StandardAccounts.AccountsReceivableCode));
        AssertBalanced(rev);
    }
}
