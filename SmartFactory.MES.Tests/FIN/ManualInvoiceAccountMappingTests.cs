using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// 手動 AP／AR 發票的總帳認列（manual-test-plan §12.4 #1、§11 #9）。
///
/// 原本 <c>CreateManualAsync</c> 完全沒有分錄：手動發票在子帳看得到、總帳看不到，
/// 而作廢時因為找不到原分錄而「自然無事發生」——一致，但兩邊永遠對不起來。
///
/// ⚠️ 2026-09-03 起科目的**唯一來源是明細指定的 <c>ChartOfAccount</c>**。
/// 原本「沒指定就退回品號的產品類別」那條 fallback 已連同 <c>ProductCategory</c> 的
/// 科目欄位一起移除——手動建單畫面不送品號、認列型發票走自己的分錄產生器，
/// 那條路徑沒有任何呼叫端。本檔因此只留下**與科目來源無關**的三件事：
/// 同科目合併、外幣換算、作廢回沖。科目解析本身由
/// <see cref="ManualInvoiceDirectAccountTests"/> 負責。
/// </summary>
public class ManualInvoiceAccountMappingTests : TestBase
{
    private const string PlantId  = "P001";
    private const string Party    = "C001";
    private const string ExpAcc = "5310";           // 啟用中的費用科目

    private readonly APInvoiceService _ap;
    private readonly ARInvoiceService _ar;
    private readonly Mock<IDocNumberService> _docNo = new();
    private readonly Mock<IDateTimeService>  _dt    = new();
    private int _seq;

    public ManualInvoiceAccountMappingTests()
    {
        _context.CurrentPlantId = PlantId;
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns((string t, string _, DateOnly? __, CancellationToken ___) =>
                Task.FromResult($"{t}{++_seq:D6}"));

        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        // 期間服務回 null＝無鎖倉，本組要驗的是科目對應不是鎖倉。
        var periods = new Mock<Modules.ACT.Services.IAccountingPeriodService>();
        periods.Setup(p => p.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        periods.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        var journal = new Modules.ACT.Services.JournalEntryService(
            _context, _docNo.Object, periods.Object);

        _ap = new APInvoiceService(_context, _docNo.Object, _dt.Object, currency.Object,
            periodSvc: null, journalSvc: journal);
        _ar = new ARInvoiceService(_context, _docNo.Object, _dt.Object, currency.Object,
            periodSvc: null, journalSvc: journal);

        SeedMasterData();
    }

    // ── AP ────────────────────────────────────────────────────────────────





    /// <summary>同科目的多行併成一條傳票行，十行發票不該產出十條同科目的分錄。</summary>
    [Fact]
    public async Task ManualAp_SameAccountLines_AreAggregated()
    {
        var inv = BuildAp(ExpAcc, qty: 1m, price: 100m);
        inv.Lines.Add(new APInvoiceLine
        {
            LineNo = 2, AccountCode = ExpAcc, Qty = 1m, UnitPrice = 50m, LineAmount = 50m,
        });

        var created = await _ap.CreateManualAsync(inv);

        var lines = await JournalLinesOfAsync("APInvoice", created.APInvoiceId);
        Assert.Equal(150m, Assert.Single(lines, l => l.AccountCode == ExpAcc).Debit);
    }

    /// <summary>外幣：分錄以本幣記，且應付方取各借方腿合計，保證傳票自身平衡。</summary>
    [Fact]
    public async Task ManualAp_ForeignCurrency_PostsInLocalAndBalances()
    {
        var inv = BuildAp(ExpAcc, qty: 1m, price: 100m, tax: 5m);
        inv.CurrencyCode = "USD";
        inv.ExchangeRate = 30m;

        var created = await _ap.CreateManualAsync(inv);

        var lines = await JournalLinesOfAsync("APInvoice", created.APInvoiceId);
        Assert.Equal(3000m, lines.Single(l => l.AccountCode == ExpAcc).Debit);
        Assert.Equal(150m,  lines.Single(l => l.AccountCode == "1155").Debit);
        Assert.Equal(3150m, lines.Single(l => l.AccountCode == "2100").Credit);
    }

    /// <summary>作廢要把手動發票的分錄一併回沖，否則總帳與子帳又脫節一次。</summary>
    [Fact]
    public async Task ManualAp_Void_ReversesTheJournal()
    {
        var inv = await _ap.CreateManualAsync(BuildAp(ExpAcc, qty: 1m, price: 100m));

        await _ap.VoidAsync(inv.APInvoiceId);

        var all = await _context.JournalEntries.IgnoreQueryFilters()
            .Include(j => j.Lines)
            .Where(j => j.SourceDocId == inv.APInvoiceId)
            .SelectMany(j => j.Lines)
            .ToListAsync();
        Assert.Equal(all.Sum(l => l.Debit), all.Sum(l => l.Credit));
        // 認列＋回沖之後各科目淨額歸零
        Assert.All(all.GroupBy(l => l.AccountCode),
            g => Assert.Equal(0m, g.Sum(l => l.Debit) - g.Sum(l => l.Credit)));
    }

    // ── AR ────────────────────────────────────────────────────────────────



    // ── 輔助 ──────────────────────────────────────────────────────────────

    private async Task<List<Modules.ACT.Models.JournalLine>> JournalLinesOfAsync(
        string sourceDocType, string sourceDocId)
        => await _context.JournalEntries.IgnoreQueryFilters()
            .Include(j => j.Lines)
            .Where(j => j.SourceDocType == sourceDocType && j.SourceDocId == sourceDocId)
            .SelectMany(j => j.Lines)
            .ToListAsync();

    private static APInvoice BuildAp(string? accountCode, decimal qty, decimal price, decimal tax = 0m)
    {
        var inv = new APInvoice
        {
            SupplierCode = Party, PlantId = PlantId, ExchangeRate = 1m,
            InvoiceDate = new DateOnly(2026, 6, 10),
            TaxAmount = tax, TotalAmount = qty * price + tax, AmountLocal = qty * price + tax,
        };
        inv.Lines.Add(new APInvoiceLine
        {
            LineNo = 1, AccountCode = accountCode, Qty = qty, UnitPrice = price,
            LineAmount = qty * price, TaxAmount = tax,
        });
        return inv;
    }

    private static ARInvoice BuildAr(string? accountCode, decimal qty, decimal price, decimal tax = 0m)
    {
        var inv = new ARInvoice
        {
            CustomerCode = Party, PlantId = PlantId, ExchangeRate = 1m,
            InvoiceDate = new DateOnly(2026, 6, 10),
            TaxAmount = tax, TotalAmount = qty * price + tax, AmountLocal = qty * price + tax,
        };
        inv.Lines.Add(new ARInvoiceLine
        {
            LineNo = 1, AccountCode = accountCode, Qty = qty, UnitPrice = price,
            LineAmount = qty * price, TaxAmount = tax,
        });
        return inv;
    }

    private void SeedMasterData()
    {
        _context.Companies.Add(new Company
        {
            CompanyCode = Party, CompanyName = "測試對象", IsSupplier = true, IsCustomer = true,
        });
        _context.Set<ChartOfAccount>().Add(new ChartOfAccount
        {
            AccountCode = ExpAcc, AccountName = "雜項費用", AccountType = AccountType.OperatingExpense, AllowManualEntry = true,
        });
        _context.SaveChanges();
    }
}
