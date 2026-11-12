using SmartFactory.MES.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// 折讓單（W3-F3 Phase 2）的**分錄科目與借貸方向**。
///
/// 為什麼這一支非寫不可：AP 側的實作是照 AR 側逐段對稱寫出來的，而兩側的分錄
/// 恰好方向相反（減應收 vs 減應付）、科目也不同（4100/2155/1170 vs 2100/2152/1155）。
/// 抄錯一邊的結果是**傳票依然借貸平衡、稽核依然放行**——只有科目餘額是錯的，
/// 而那要等到有人對財報才會發現。貫穿測試驗的是 AR 那條路，AP 這條在真實流程裡
/// 需要「已付款的進貨發票 + 進貨退出」的完整佈景，成本高得多；
/// 分錄本身用單元測試逐腿釘住反而更準。
///
/// 每一支都逐腿斷言（科目、借、貸），不只驗「有產生分錄」。
/// </summary>
public class CreditAndDebitNoteTests : TestBase
{
    private const string PlantId = "P001";
    private const string Cust    = "CUS001";
    private const string Supp    = "SUP001";

    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService>  _mockDt    = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private readonly SmartFactory.MES.Modules.ACT.Services.JournalEntryService _journalSvc;
    private int _seq, _jeSeq;

    private static readonly DateOnly D = new(2026, 3, 10);

    public CreditAndDebitNoteTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns((string t, string _, DateOnly? __, CancellationToken ___) =>
                Task.FromResult(t == "JournalEntry" ? $"JE{++_jeSeq:D6}" : $"{t[..3]}{++_seq:D6}"));
        _mockDt.Setup(d => d.GetLocalDate(null)).Returns(new DateOnly(2026, 3, 20));
        _mockCurrency = GetMockCurrencyService();

        var mockPeriods = new Mock<SmartFactory.MES.Modules.ACT.Services.IAccountingPeriodService>();
        _journalSvc = new SmartFactory.MES.Modules.ACT.Services.JournalEntryService(_context, _mockDocNo.Object, mockPeriods.Object);

        _context.Companies.Add(new Company { CompanyCode = Cust, CompanyName = "測試客戶",  IsCustomer = true });
        _context.Companies.Add(new Company { CompanyCode = Supp, CompanyName = "測試供應商", IsSupplier = true });
        _context.SaveChanges();
    }

    private ARCreditNoteService ArSvc() => new(
        _context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
        periodSvc: null, journalSvc: _journalSvc);

    private APDebitNoteService ApSvc() => new(
        _context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
        periodSvc: null, journalSvc: _journalSvc);

    /// <summary>取某張傳票的每一腿，方便逐腿斷言。</summary>
    private List<(string Account, decimal Debit, decimal Credit)> LegsOf(string sourceDocId)
        => _context.JournalLines.IgnoreQueryFilters()
            .Where(l => _context.JournalEntries.IgnoreQueryFilters()
                .Any(j => j.JournalId == l.JournalId && j.SourceDocId == sourceDocId))
            .AsEnumerable()
            .Select(l => (l.AccountCode, l.Debit, l.Credit))
            .OrderBy(x => x.AccountCode)
            .ToList();

    // ── AR：銷貨折讓 ────────────────────────────────────────────────────

    [Fact]
    public async Task ArCreditNote_Confirm_PostsRevenueAndVatDebit_AgainstReceivable()
    {
        var (noteId, invoiceId) = await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);

        await ArSvc().ConfirmAsync(noteId);

        var legs = LegsOf(noteId);
        Assert.Equal(3, legs.Count);
        // 借 銷貨收入 1,000／借 銷項稅額 50／貸 應收帳款 1,050
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.SalesRevenueCode      && l.Debit == 1000m && l.Credit == 0m);
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.OutputVatCode         && l.Debit == 50m   && l.Credit == 0m);
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.AccountsReceivableCode && l.Debit == 0m   && l.Credit == 1050m);
        Assert.Equal(legs.Sum(l => l.Debit), legs.Sum(l => l.Credit));

        var inv = await _context.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == invoiceId);
        Assert.Equal(1050m, inv.CreditedAmount);
        Assert.Equal(2100m, inv.TotalAmount);   // 憑證金額一毛未動
    }

    /// <summary>
    /// 草稿開出後發票才被作廢（收款都沖掉就能作廢）：再確認等於對一張不存在的應收記折讓
    /// （2026-10-22 ReceivableLedgerModelTests 隨機序列發現）。不留分錄、不動發票。
    /// </summary>
    [Fact]
    public async Task ArCreditNote_Confirm_AfterInvoiceVoided_IsRejected()
    {
        var (noteId, invoiceId) = await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        var inv = await _context.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == invoiceId);
        inv.Status = ARInvoiceStatus.Void;
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ArSvc().ConfirmAsync(noteId));
        Assert.Contains(invoiceId, ex.Message);
        Assert.Empty(LegsOf(noteId));
        Assert.Equal(0m, (await _context.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == invoiceId)).CreditedAmount);
    }

    [Fact]
    public async Task ApDebitNote_Confirm_AfterInvoiceVoided_IsRejected()
    {
        var (noteId, invoiceId) = await SeedApNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        var inv = await _context.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == invoiceId);
        inv.Status = APInvoiceStatus.Void;
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApSvc().ConfirmAsync(noteId));
        Assert.Contains(invoiceId, ex.Message);
        Assert.Empty(LegsOf(noteId));
        Assert.Equal(0m, (await _context.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == invoiceId)).DebitedAmount);
    }

    [Fact]
    public async Task ArCreditNote_Void_ReversesEveryLeg_AndGivesTheCreditBack()
    {
        var (noteId, invoiceId) = await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        await ArSvc().ConfirmAsync(noteId);
        await ArSvc().VoidAsync(noteId, "測試");

        // 兩張傳票（原 + 回沖）合計每個科目淨額為 0
        var byAccount = LegsOf(noteId).GroupBy(l => l.Account)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Debit) - g.Sum(x => x.Credit));
        Assert.Equal(6, LegsOf(noteId).Count);
        Assert.All(byAccount.Values, v => Assert.Equal(0m, v));

        var inv = await _context.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == invoiceId);
        Assert.Equal(0m, inv.CreditedAmount);
        Assert.Equal(CreditNoteStatus.Void,
            (await _context.ARCreditNotes.IgnoreQueryFilters().SingleAsync(n => n.ARCreditNoteId == noteId)).Status);
    }

    /// <summary>折讓量不得超過發票上開過的量——上限守的是數量，不是未結餘額。</summary>
    [Fact]
    public async Task ArCreditNote_CannotCreditMoreQtyThanInvoiced()
    {
        var (noteId, _) = await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        // 把折讓量偷偷改成超過發票開立量（21 > 20）
        var line = await _context.ARCreditNoteLines.IgnoreQueryFilters().SingleAsync(l => l.ARCreditNoteId == noteId);
        line.Qty = 21m;
        await _context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ArSvc().ConfirmAsync(noteId));
        PzAssert.Message("Fin_NoteQtyExceedsInvoiceFmt", ex.Message);
        Assert.Empty(LegsOf(noteId));   // 擋下之後不得留下半張傳票
    }

    /// <summary>發票已全額收款時，折讓讓未結餘額變成負的——那是對的，公司欠客戶。</summary>
    [Fact]
    public async Task ArCreditNote_OnFullySettledInvoice_LeavesNegativeOpenBalance()
    {
        var (noteId, invoiceId) = await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m, fullyReceived: true);

        await ArSvc().ConfirmAsync(noteId);

        var inv = await _context.ARInvoices.IgnoreQueryFilters().SingleAsync(i => i.ARInvoiceId == invoiceId);
        Assert.Equal(-1050m, inv.TotalAmount - inv.ReceivedAmount - inv.CreditedAmount);
    }

    // ── AP：進貨折讓（方向相反）─────────────────────────────────────────

    [Fact]
    public async Task ApDebitNote_Confirm_DebitsPayable_AgainstGrIrAndInputVat()
    {
        var (noteId, invoiceId) = await SeedApNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);

        await ApSvc().ConfirmAsync(noteId);

        var legs = LegsOf(noteId);
        Assert.Equal(3, legs.Count);
        // 借 應付帳款 1,050／貸 進貨暫估應付 1,000／貸 進項稅額 50
        // ⚠️ 貸方是 2152 而不是存貨：退貨過帳當下已經貸過存貨、借過 2152 了，
        //    這裡再借一次存貨會讓存貨被沖兩次。
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.AccountsPayableCode && l.Debit == 1050m && l.Credit == 0m);
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.GrIrAPClearingCode  && l.Debit == 0m    && l.Credit == 1000m);
        Assert.Contains(legs, l => l.Account == SmartFactory.MES.Modules.ACT.StandardAccounts.InputVatCode        && l.Debit == 0m    && l.Credit == 50m);
        Assert.Equal(legs.Sum(l => l.Debit), legs.Sum(l => l.Credit));

        var inv = await _context.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == invoiceId);
        Assert.Equal(1050m, inv.DebitedAmount);
        Assert.Equal(2100m, inv.TotalAmount);
    }

    [Fact]
    public async Task ApDebitNote_Void_ReversesEveryLeg()
    {
        var (noteId, invoiceId) = await SeedApNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        await ApSvc().ConfirmAsync(noteId);
        await ApSvc().VoidAsync(noteId, "測試");

        var byAccount = LegsOf(noteId).GroupBy(l => l.Account)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Debit) - g.Sum(x => x.Credit));
        Assert.Equal(6, LegsOf(noteId).Count);
        Assert.All(byAccount.Values, v => Assert.Equal(0m, v));

        var inv = await _context.APInvoices.IgnoreQueryFilters().SingleAsync(i => i.APInvoiceId == invoiceId);
        Assert.Equal(0m, inv.DebitedAmount);
    }

    /// <summary>同一張退貨單重複產生只會有一張折讓單。四個退貨入口各自呼叫，這條非成立不可。</summary>
    [Fact]
    public async Task CreateDraft_IsIdempotent_AcrossRepeatedCalls()
    {
        await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m);
        var again = await ArSvc().CreateDraftFromSalesReturnAsync("SR-001");
        Assert.NotNull(again);
        Assert.Single(await _context.ARCreditNotes.IgnoreQueryFilters().ToListAsync());
    }

    /// <summary>來源發票仍是 Open 時不建折讓單——那條路 ReverseForReturnedQtyAsync 當場就改小了發票，再折一次會沖兩次。</summary>
    [Fact]
    public async Task CreateDraft_SkipsOpenInvoices()
    {
        await SeedArNoteAsync(qty: 10m, price: 100m, taxRate: 0.05m, invoiceStatus: ARInvoiceStatus.Open, buildNote: false);
        Assert.Null(await ArSvc().CreateDraftFromSalesReturnAsync("SR-001"));
        Assert.Empty(await _context.ARCreditNotes.IgnoreQueryFilters().ToListAsync());
    }

    // ── 佈景 ────────────────────────────────────────────────────────────

    private async Task<(string NoteId, string InvoiceId)> SeedArNoteAsync(
        decimal qty, decimal price, decimal taxRate,
        bool fullyReceived = false,
        ARInvoiceStatus invoiceStatus = ARInvoiceStatus.PartialReceived,
        bool buildNote = true)
    {
        const string gs = "GS-001", sr = "SR-001", inv = "ARI-001";
        var invoiceQty = qty * 2m;   // 出貨 20、退 10

        _context.GoodsShipments.Add(new GoodsShipment
        {
            GoodsShipmentId = gs, ShipDate = D, CompanyCode = Cust, PlantId = PlantId,
            Status = DocStatus.Posted, CreatedBy = "u",
            Lines = { new GoodsShipmentLine { GoodsShipmentId = gs, LineNo = 1, ProductCode = "P1", ShipQty = invoiceQty } },
        });
        _context.ARInvoices.Add(new ARInvoice
        {
            ARInvoiceId = inv, CustomerCode = Cust, InvoiceDate = D, DueDate = D.AddDays(30),
            PlantId = PlantId, ExchangeRate = 1m, Status = invoiceStatus,
            SourceDocType = "GoodsShipment", SourceDocId = gs,
            TaxAmount = invoiceQty * price * taxRate,
            TotalAmount = invoiceQty * price * (1m + taxRate),
            AmountLocal = invoiceQty * price * (1m + taxRate),
            ReceivedAmount = fullyReceived ? invoiceQty * price * (1m + taxRate) : 0m,
            Lines =
            {
                new ARInvoiceLine
                {
                    ARInvoiceId = inv, LineNo = 1, ProductCode = "P1", SourceLineNo = 1,
                    Qty = invoiceQty, UnitPrice = price, LineAmount = invoiceQty * price,
                    TaxRate = taxRate, TaxAmount = invoiceQty * price * taxRate,
                },
            },
        });
        _context.SalesReturns.Add(new SalesReturn
        {
            SalesReturnId = sr, ReturnDate = D, CompanyCode = Cust, PlantId = PlantId,
            GoodsShipmentId = gs, Status = DocStatus.Posted, CreatedBy = "u",
            Lines =
            {
                new SalesReturnLine
                {
                    SalesReturnId = sr, LineNo = 1, GoodsShipmentId = gs, ShipmentLineNo = 1,
                    ProductCode = "P1", ReturnQty = qty, StockQty = qty, UnitPrice = price,
                    ExchangeRate = 1m, TaxRate = taxRate,
                },
            },
        });
        await _context.SaveChangesAsync();

        if (!buildNote) return ("", inv);
        var note = await ArSvc().CreateDraftFromSalesReturnAsync(sr);
        Assert.NotNull(note);
        return (note!.ARCreditNoteId, inv);
    }

    private async Task<(string NoteId, string InvoiceId)> SeedApNoteAsync(
        decimal qty, decimal price, decimal taxRate)
    {
        const string gr = "GR-001", grt = "GRT-001", inv = "API-001";
        var invoiceQty = qty * 2m;

        _context.GoodsReceipts.Add(new GoodsReceipt
        {
            GoodsReceiptId = gr, ReceiptDate = D, SupplierCode = Supp, PlantId = PlantId,
            Status = DocStatus.Posted, CreatedBy = "u",
            Lines = { new GoodsReceiptLine { GoodsReceiptId = gr, LineNo = 1, ProductCode = "P1", ReceiptQty = invoiceQty, UnitPrice = price } },
        });
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId = inv, SupplierCode = Supp, InvoiceDate = D, DueDate = D.AddDays(30),
            PlantId = PlantId, ExchangeRate = 1m, Status = APInvoiceStatus.PartialPaid,
            SourceDocType = "GoodsReceipt", SourceDocId = gr,
            TaxAmount = invoiceQty * price * taxRate,
            TotalAmount = invoiceQty * price * (1m + taxRate),
            AmountLocal = invoiceQty * price * (1m + taxRate),
            Lines =
            {
                new APInvoiceLine
                {
                    APInvoiceId = inv, LineNo = 1, ProductCode = "P1", SourceLineNo = 1,
                    Qty = invoiceQty, UnitPrice = price, LineAmount = invoiceQty * price,
                    TaxRate = taxRate, TaxAmount = invoiceQty * price * taxRate,
                },
            },
        });
        _context.GoodsReturns.Add(new GoodsReturn
        {
            GoodsReturnId = grt, ReturnDate = D, SupplierCode = Supp, PlantId = PlantId,
            Status = DocStatus.Posted, CreatedBy = "u",
            Lines =
            {
                new GoodsReturnLine
                {
                    GoodsReturnId = grt, LineNo = 1, GoodsReceiptId = gr, ReceiptLineNo = 1,
                    ProductCode = "P1", ReturnQty = qty, StockQty = qty,
                },
            },
        });
        await _context.SaveChangesAsync();

        var note = await ApSvc().CreateDraftFromGoodsReturnAsync(grt);
        Assert.NotNull(note);
        return (note!.APDebitNoteId, inv);
    }
}
