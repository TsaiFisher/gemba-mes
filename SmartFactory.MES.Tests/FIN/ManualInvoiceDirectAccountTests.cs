using SmartFactory.MES.Tests.Shared;
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
/// 手動發票明細**直接指定科目**的路徑（manual-test-plan.md §11 #9）。
///
/// 為什麼會有這條路徑：原本科目**只能**由品號的產品類別解出，而
/// <c>ExpenseAccountFor</c> 對每一行強制要求品號。手動 AP 的典型用途是水電、運費、
/// 顧問費——**這些東西沒有品號**，要開一張水電費發票就得先在品號主檔建一個假品號，
/// 掛一個帶著 <c>CostMethod</c>／預設倉儲的產品類別（等於幫電費設成本方法），
/// 而那個假品號會出現在所有品號下拉、MRP 與庫存報表裡。
///
/// ⚠️ 2026-09-03：產品類別 fallback 已移除，指定科目成為**唯一來源**。
/// 與 <see cref="ManualInvoiceAccountMappingTests"/> 的分工因此變成：那一組釘與科目來源
/// 無關的三件事（同科目合併、外幣、作廢回沖），這一組釘科目解析本身。
/// </summary>
public class ManualInvoiceDirectAccountTests : TestBase
{
    private const string PlantId = "P001";
    private const string Party   = "C001";
    private const string ExpAcc     = "6201";        // 啟用中的費用科目
    private const string ExpInactive = "6299";       // 停用中的費用科目
    private const string RevAcc     = "4120";        // 啟用中的收入科目

    private readonly APInvoiceService _ap;
    private readonly ARInvoiceService _ar;
    private readonly Mock<IDocNumberService> _docNo = new();
    private readonly Mock<IDateTimeService>  _dt    = new();
    private int _seq;

    public ManualInvoiceDirectAccountTests()
    {
        _context.CurrentPlantId = PlantId;
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns((string t, string _, DateOnly? __, CancellationToken ___) =>
                Task.FromResult($"{t}{++_seq:D6}"));

        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

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

        Seed();
    }

    // ── 主線：沒有品號也開得出來 ──────────────────────────────────────────

    /// <summary>這一支就是整個 §11 #9 的目的：一張沒有任何品號的水電費發票。</summary>
    [Fact]
    public async Task 手動AP發票沒有品號但指定了費用科目時要正常過帳()
    {
        var inv = BuildAp(accountCode: ExpAcc, productCode: null, qty: 1m, price: 8000m, tax: 400m);

        var created = await _ap.CreateManualAsync(inv);

        var lines = await JournalLinesOfAsync("APInvoice", created.APInvoiceId);
        Assert.Equal(8000m, lines.Single(l => l.AccountCode == ExpAcc).Debit);
        Assert.Equal(400m,  lines.Single(l => l.AccountCode == "1155").Debit);
        Assert.Equal(8400m, lines.Single(l => l.AccountCode == "2100").Credit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task 手動AR發票沒有品號但指定了收入科目時要正常過帳()
    {
        var inv = BuildAr(accountCode: RevAcc, productCode: null, qty: 1m, price: 5000m, tax: 250m);

        var created = await _ar.CreateManualAsync(inv);

        var lines = await JournalLinesOfAsync("ARInvoice", created.ARInvoiceId);
        Assert.Equal(5000m, lines.Single(l => l.AccountCode == RevAcc).Credit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    /// <summary>
    /// 2153 暫估應付運費由進料費用分攤確認時貸記，費用發票到了要能「借 2153／貸 應付」沖平
    /// （2026-10-23 使用者裁決）。它是負債、不開放手動選用，但列在 ManualClearingAccountCodes 例外。
    /// </summary>
    [Fact]
    public async Task 手動AP發票可指定2153暫估應付運費沖銷分攤暫估()
    {
        var created = await _ap.CreateManualAsync(BuildAp(accountCode: "2153", productCode: null, qty: 1m, price: 3200m, tax: 160m));

        var lines = await JournalLinesOfAsync("APInvoice", created.APInvoiceId);
        Assert.Equal(3200m, lines.Single(l => l.AccountCode == "2153").Debit);
        Assert.Equal(3360m, lines.Single(l => l.AccountCode == "2100").Credit);
    }

    /// <summary>例外只開 2153：同樣是負債、由自動分錄維護的 2152 仍然擋下。</summary>
    [Fact]
    public async Task 其他自動分錄維護的負債科目仍不得指定()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _ap.CreateManualAsync(BuildAp(accountCode: "2152", productCode: null, qty: 1m, price: 100m)));
        Assert.Empty(_context.APInvoices);
    }

    // ── 守門：用途與啟用狀態 ──────────────────────────────────────────────

    /// <summary>
    /// 把**收入**科目填進 AP 發票要被擋下。不擋的話會產出一張「借 4100 銷貨收入」的傳票——
    /// 借貸依然平衡、稽核也不會叫，錯得完全沒有訊號，只有人去看損益表才會發現。
    /// </summary>
    [Fact]
    public async Task AP發票填到收入科目要被擋下且不留任何資料()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _ap.CreateManualAsync(BuildAp(accountCode: RevAcc, productCode: null, qty: 1m, price: 100m)));

        Assert.Contains(RevAcc, ex.Message);
        Assert.Empty(_context.APInvoices);
        Assert.Empty(_context.JournalEntries);
    }

    /// <summary>停用中的科目同樣擋下——讀取端不濾的話，停用就只是個視覺效果。</summary>
    [Fact]
    public async Task 停用中的科目不得被指定()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _ap.CreateManualAsync(BuildAp(accountCode: ExpInactive, productCode: null, qty: 1m, price: 100m)));

        Assert.Contains(ExpInactive, ex.Message);
        Assert.Empty(_context.APInvoices);
    }

    [Fact]
    public async Task 查無此科目時要指名道姓地擋下()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _ap.CreateManualAsync(BuildAp(accountCode: "9999", productCode: null, qty: 1m, price: 100m)));

        Assert.Contains("9999", ex.Message);
    }

    /// <summary>
    /// 沒指定科目就是無法判斷——2026-09-03 移除產品類別 fallback 之後，這是唯一的失敗路徑。
    /// 訊息要指出去哪裡建科目，不能只說「沒有科目」。
    /// </summary>
    [Fact]
    public async Task 沒有指定科目時要擋下並指出去哪裡建科目()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _ap.CreateManualAsync(BuildAp(accountCode: null, productCode: null, qty: 1m, price: 100m)));

        PzAssert.Message("Fin_APLineNoAccountFmt", ex.Message);   // 訊息須指出去「發票科目維護」建科目
        Assert.Empty(_context.APInvoices);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private void Seed()
    {
        _context.Companies.Add(new Company
        {
            CompanyCode = Party, CompanyName = "測試對象", IsSupplier = true, IsCustomer = true,
        });
        _context.Set<ChartOfAccount>().AddRange(
            new ChartOfAccount { AccountCode = ExpAcc, AccountName = "水電瓦斯費", AccountType = AccountType.OperatingExpense, AllowManualEntry = true },
            new ChartOfAccount { AccountCode = ExpInactive, AccountName = "已停用費用", AccountType = AccountType.OperatingExpense, AllowManualEntry = true, IsActive = false },
            new ChartOfAccount { AccountCode = RevAcc, AccountName = "服務收入", AccountType = AccountType.Revenue, AllowManualEntry = true },
            new ChartOfAccount { AccountCode = "2153", AccountName = "暫估應付運費", AccountType = AccountType.Liability, AllowManualEntry = false, IsSystem = true },
            new ChartOfAccount { AccountCode = "2152", AccountName = "進貨暫估應付", AccountType = AccountType.Liability, AllowManualEntry = false, IsSystem = true });
        _context.SaveChanges();
    }

    private async Task<List<Modules.ACT.Models.JournalLine>> JournalLinesOfAsync(string docType, string docId)
        => await _context.JournalEntries.AsNoTracking()
            .Where(j => j.SourceDocType == docType && j.SourceDocId == docId)
            .SelectMany(j => j.Lines)
            .ToListAsync();

    private static APInvoice BuildAp(string? accountCode, string? productCode, decimal qty, decimal price, decimal tax = 0m)
    {
        var inv = new APInvoice
        {
            SupplierCode = Party, PlantId = PlantId, ExchangeRate = 1m,
            InvoiceDate = new DateOnly(2026, 6, 10),
            TaxAmount = tax, TotalAmount = qty * price + tax, AmountLocal = qty * price + tax,
        };
        inv.Lines.Add(new APInvoiceLine
        {
            LineNo = 1, AccountCode = accountCode, ProductCode = productCode,
            Qty = qty, UnitPrice = price, LineAmount = qty * price, TaxAmount = tax,
        });
        return inv;
    }

    private static ARInvoice BuildAr(string? accountCode, string? productCode, decimal qty, decimal price, decimal tax = 0m)
    {
        var inv = new ARInvoice
        {
            CustomerCode = Party, PlantId = PlantId, ExchangeRate = 1m,
            InvoiceDate = new DateOnly(2026, 6, 10),
            TaxAmount = tax, TotalAmount = qty * price + tax, AmountLocal = qty * price + tax,
        };
        inv.Lines.Add(new ARInvoiceLine
        {
            LineNo = 1, AccountCode = accountCode, ProductCode = productCode,
            Qty = qty, UnitPrice = price, LineAmount = qty * price, TaxAmount = tax,
        });
        return inv;
    }
}
