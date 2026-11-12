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
/// 手動發票的簽核（manual-test-plan.md §11 #9 的第二段）。
///
/// 為什麼一定要有：手動發票是**全系統唯一能憑空產生應付／應收的路徑**——沒有採購單、
/// 沒有進貨單、沒有三方比對，只有一個人打字。而 <c>CreateManualAsync</c> 原本把
/// <c>ApprovalStatus</c> 寫死 <c>Approved</c>，等於這條路徑沒有簽核，
/// 採購單／工單／出貨單卻全都有。
///
/// 兩件事必須同時成立，少一件都比沒做更糟：
///   1. 未核准**不進總帳**——先掛帳再簽核，簽核就只是表面流程；
///   2. 核准時**要補分錄**，且回寫要有 contributor——少了 contributor 會靜默停在待審。
/// </summary>
public class ManualInvoiceApprovalTests : TestBase
{
    private const string PlantId = "P001";
    private const string Party   = "C001";
    private const string ExpAcc  = "6201";

    private readonly Mock<IDocNumberService> _docNo = new();
    private readonly Mock<IDateTimeService>  _dt    = new();
    private readonly Mock<IApprovalService>  _approval = new();
    private readonly Modules.ACT.Services.JournalEntryService _journal;
    private int _seq;

    public ManualInvoiceApprovalTests()
    {
        _context.CurrentPlantId = PlantId;
        _docNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns((string t, string _, DateOnly? __, CancellationToken ___) =>
                Task.FromResult($"{t}{++_seq:D6}"));

        var periods = new Mock<Modules.ACT.Services.IAccountingPeriodService>();
        periods.Setup(p => p.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        periods.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        _journal = new Modules.ACT.Services.JournalEntryService(_context, _docNo.Object, periods.Object);

        Seed();
    }

    /// <summary>
    /// 服務本身不再注入 <c>IApprovalService</c>——注進去會造成
    /// `ApprovalService → contributors → APInvoiceService → ApprovalService` 的循環相依，
    /// `ValidateOnBuild` 會讓**整個 app 起不來**。狀態改由呼叫端解析後傳入，
    /// 所以這裡也用同一種方式：mock 出設定要回的狀態，再像 Controller 那樣傳進去。
    /// </summary>
    private APInvoiceService ApService()
    {
        var currency = GetMockCurrencyService();
        currency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);
        return new APInvoiceService(_context, _docNo.Object, _dt.Object, currency.Object,
            periodSvc: null, journalSvc: _journal);
    }

    /// <summary>模擬 Controller 的那一步：先問簽核設定，再把結果傳給服務。</summary>
    private async Task<DocumentApprovalStatus> ResolveAsync(DocumentApprovalStatus configured)
    {
        _approval.Setup(a => a.ResolveSaveStatusAsync("APInvoice", It.IsAny<string?>()))
            .ReturnsAsync(configured);
        return await _approval.Object.ResolveSaveStatusAsync("APInvoice", PlantId);
    }

    // ── 未核准不進總帳 ────────────────────────────────────────────────────

    /// <summary>
    /// 這一支是整段簽核的重點。單據建得出來、子帳看得到，但**總帳一行都不能有**——
    /// 反過來的話（先掛帳再簽核）簽核就只是個表面流程，而且退件時還得回沖。
    /// </summary>
    [Fact]
    public async Task 有簽核設定時手動AP發票要停在草稿且不得產生任何分錄()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Draft);

        var inv = await ap.CreateManualAsync(BuildAp(qty: 1m, price: 8000m, tax: 400m), status);

        Assert.Equal(DocumentApprovalStatus.Draft, inv.ApprovalStatus);
        Assert.Single(_context.APInvoices);                       // 子帳有
        Assert.Empty(await JournalLinesOfAsync(inv.APInvoiceId));  // 總帳沒有
    }

    /// <summary>沒有簽核設定時維持原行為——這是既有部署的路徑，不能因為加了簽核而改變。</summary>
    [Fact]
    public async Task 沒有簽核設定時仍然建立即核准並立刻入總帳()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Approved);

        var inv = await ap.CreateManualAsync(BuildAp(qty: 1m, price: 8000m, tax: 400m), status);

        Assert.Equal(DocumentApprovalStatus.Approved, inv.ApprovalStatus);
        var lines = await JournalLinesOfAsync(inv.APInvoiceId);
        Assert.Equal(8000m, lines.Single(l => l.AccountCode == ExpAcc).Debit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    /// <summary>科目解不出來時，**未核准的發票也要當場擋下**——留到核准當下才爆，
    /// 等於把一個簽核者修不了的錯誤丟給他。</summary>
    [Fact]
    public async Task 草稿狀態下科目解不出來一樣要當場擋下()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Draft);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ap.CreateManualAsync(BuildAp(qty: 1m, price: 100m, accountCode: "9999"), status));

        Assert.Empty(_context.APInvoices);
    }

    // ── 核准回寫 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 核准回寫要把狀態改成已核准並補上分錄()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Draft);
        var inv = await ap.CreateManualAsync(BuildAp(qty: 1m, price: 8000m, tax: 400m), status);

        var sync = new APInvoiceApprovalSync(ap);
        await sync.SyncAsync(_context, inv.APInvoiceId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        var reloaded = await _context.APInvoices.AsNoTracking()
            .FirstAsync(i => i.APInvoiceId == inv.APInvoiceId);
        Assert.Equal(DocumentApprovalStatus.Approved, reloaded.ApprovalStatus);

        var lines = await JournalLinesOfAsync(inv.APInvoiceId);
        Assert.Equal(8000m, lines.Single(l => l.AccountCode == ExpAcc).Debit);
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    /// <summary>
    /// 冪等。退回再重送、代理人重複核准、同一筆申請被再同步一次，都會再走一次回寫——
    /// 不冪等的話同一張發票掛兩次應付，而**兩張傳票各自都平衡**，總帳不會有任何訊號。
    /// </summary>
    [Fact]
    public async Task 重複核准回寫不得產生第二張傳票()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Draft);
        var inv = await ap.CreateManualAsync(BuildAp(qty: 1m, price: 8000m), status);
        var sync = new APInvoiceApprovalSync(ap);

        await sync.SyncAsync(_context, inv.APInvoiceId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();
        await sync.SyncAsync(_context, inv.APInvoiceId, ApprovalStatus.Approved);
        await _context.SaveChangesAsync();

        var entries = await _context.JournalEntries.AsNoTracking()
            .Where(j => j.SourceDocType == "APInvoice" && j.SourceDocId == inv.APInvoiceId)
            .CountAsync();
        Assert.Equal(1, entries);
    }

    /// <summary>退件不得補分錄——只改狀態。</summary>
    [Fact]
    public async Task 退件時不得產生分錄()
    {
        var ap = ApService();
        var status = await ResolveAsync(DocumentApprovalStatus.Draft);
        var inv = await ap.CreateManualAsync(BuildAp(qty: 1m, price: 8000m), status);

        var sync = new APInvoiceApprovalSync(ap);
        await sync.SyncAsync(_context, inv.APInvoiceId, ApprovalStatus.Rejected);
        await _context.SaveChangesAsync();

        var reloaded = await _context.APInvoices.AsNoTracking()
            .FirstAsync(i => i.APInvoiceId == inv.APInvoiceId);
        Assert.Equal(DocumentApprovalStatus.Rejected, reloaded.ApprovalStatus);
        Assert.Empty(await JournalLinesOfAsync(inv.APInvoiceId));
    }

    /// <summary>
    /// 認列型發票（由進貨單產生）不經簽核，分錄在收貨當下就開好了——
    /// 回寫路徑不得碰它，否則會在既有分錄之外再加一張。
    /// </summary>
    [Fact]
    public async Task 認列型發票不得被補開分錄()
    {
        var ap = ApService();
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = "API-FROM-GR", SupplierCode = Party, PlantId = PlantId,
            SourceDocType = "GoodsReceipt", SourceDocId = "PE-0001", ExchangeRate = 1m,
            InvoiceDate = new DateOnly(2026, 6, 10),
        });
        await _context.SaveChangesAsync();

        await ap.PostManualJournalAsync("API-FROM-GR");

        Assert.Empty(await JournalLinesOfAsync("API-FROM-GR"));
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private void Seed()
    {
        _context.Companies.Add(new Company
        {
            CompanyCode = Party, CompanyName = "測試對象", IsSupplier = true, IsCustomer = true,
        });
        _context.Set<ChartOfAccount>().Add(new ChartOfAccount
        {
            AccountCode = ExpAcc, AccountName = "水電瓦斯費", AccountType = AccountType.OperatingExpense, AllowManualEntry = true,
        });
        _context.SaveChanges();
    }

    private async Task<List<Modules.ACT.Models.JournalLine>> JournalLinesOfAsync(string docId)
        => await _context.JournalEntries.AsNoTracking()
            .Where(j => j.SourceDocType == "APInvoice" && j.SourceDocId == docId)
            .SelectMany(j => j.Lines)
            .ToListAsync();

    private static APInvoice BuildAp(decimal qty, decimal price, decimal tax = 0m, string? accountCode = ExpAcc)
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
}
