using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// AP 認列時點重構（方案 A）：AccrueForReceivedQtyAsync 依「進正常倉的實際量」逐次累加，
/// 一張進貨單維持一張 AP、金額累加，不需額外欄位（已認列量＝AP 明細現有 Qty）。
/// </summary>
public class APAccrualByReceivedQtyTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";

    private readonly APInvoiceService _svc;
    private readonly Mock<IDocNumberService> _mockDocNo = new();
    private readonly Mock<IDateTimeService>  _mockDt    = new();
    private readonly Mock<ICurrencyService>  _mockCurrency;
    private int _apSeq;
    private int _jeSeq;

    public APAccrualByReceivedQtyTests()
    {
        _context.CurrentPlantId = PlantId;
        _mockDocNo.Setup(d => d.NextAsync("APInvoice", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"API{++_apSeq:D6}"));
        _mockDocNo.Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"JE{++_jeSeq:D6}"));

        _mockCurrency = GetMockCurrencyService();
        _mockCurrency.Setup(c => c.GetRateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<RateType>()))
            .ReturnsAsync((decimal?)null);

        // 真實 JournalEntryService（InMemory）：期間服務回 null＝無鎖倉。
        var mockPeriods = new Mock<Modules.ACT.Services.IAccountingPeriodService>();
        mockPeriods.Setup(p => p.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        mockPeriods.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Modules.ACT.Models.AccountingPeriod?)null);
        var journalSvc = new Modules.ACT.Services.JournalEntryService(
            _context, _mockDocNo.Object, mockPeriods.Object);

        _svc = new APInvoiceService(_context, _mockDocNo.Object, _mockDt.Object, _mockCurrency.Object,
            periodSvc: null, journalSvc: journalSvc);

        _context.Companies.Add(new Company { CompanyCode = SuppCode, CompanyName = "測試供應商", IsSupplier = true });
        _context.SaveChanges();
    }

    private void SeedGr(string grId, decimal receiptQty, decimal unitPrice, decimal taxRate = 0m)
    {
        var gr = new GoodsReceipt
        {
            GoodsReceiptId = grId, ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = "user1", SupplierCode = SuppCode,
            Status = DocStatus.Confirmed, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
        };
        gr.Lines.Add(new GoodsReceiptLine
        {
            GoodsReceiptId = grId, LineNo = 1, ProductCode = "MAT001",
            ReceiptQty = receiptQty, UomCode = "PC", StockQty = receiptQty, StockUomCode = "PC",
            UnitPrice = unitPrice, TaxRate = taxRate,
        });
        _context.GoodsReceipts.Add(gr);
        _context.SaveChanges();
    }

    /// <summary>建一張已過帳／草稿的採購退貨，掛在 grId 的第 1 行上。</summary>
    private void SeedGoodsReturn(string grtId, string grId, decimal qty, DocStatus status)
    {
        var grt = new GoodsReturn
        {
            GoodsReturnId = grtId, ReturnDate = new DateOnly(2025, 3, 5),
            SupplierCode = SuppCode, CreatedBy = "user1",
            Status = status, ApprovalStatus = DocumentApprovalStatus.Approved,
            PlantId = PlantId,
        };
        grt.Lines.Add(new GoodsReturnLine
        {
            GoodsReturnId = grtId, LineNo = 1, ProductCode = "MAT001",
            ReturnQty = qty, UomCode = "PC", StockQty = qty, StockUomCode = "PC",
            GoodsReceiptId = grId, ReceiptLineNo = 1,
        });
        _context.GoodsReturns.Add(grt);
        // 退貨單存草稿當下就會把 ReturnedQty 加上去（過帳與否無關）
        var line = _context.GoodsReceiptLines.First(l => l.GoodsReceiptId == grId && l.LineNo == 1);
        line.ReturnedQty += qty;
        _context.SaveChanges();
    }

    /// <summary>
    /// 開票前退貨**已過帳**：開票量必須扣掉它。
    /// 退貨過帳當下發票還不存在，ReverseForReturnedQtyAsync 查無發票即回 NoInvoice，
    /// 此後再無第二次機會——這裡不扣，那一段就永遠掛在應付上。
    /// 2026-09-05 由 RETURN_SUBLEDGER_UNREVERSED 在實際資料上抓到（API-2607-00002：
    /// 退貨 04-23 過帳、發票 07-24 才開，多認列 15.75）。
    /// </summary>
    [Fact]
    public async Task Generate_WhenPostedReturnPrecedesInvoicing_BillsNetOfIt()
    {
        SeedGr("GR-RB1", receiptQty: 2m, unitPrice: 15m, taxRate: 0.05m);
        SeedGoodsReturn("GRT-RB1", "GR-RB1", 1m, DocStatus.Posted);

        var inv = await _svc.GenerateFromGoodsReceiptAsync("GR-RB1");

        Assert.Single(inv.Lines);
        Assert.Equal(1m,     inv.Lines.First().Qty);          // 2 − 1，不是 2
        Assert.Equal(15m,    inv.Lines.First().LineAmount);
        Assert.Equal(0.75m,  inv.Lines.First().TaxAmount);
        Assert.Equal(15.75m, inv.TotalAmount);                // 錯誤實作為 31.50
    }

    /// <summary>
    /// 開票前退貨**還是草稿**：一個都不能扣。
    ///
    /// `GoodsReceiptLine.ReturnedQty` 在退貨單存草稿當下就被加上去了，回沖卻要等到過帳。
    /// 若拿那個欄位來扣，這裡扣一次、之後草稿過帳回沖再扣一次＝**同一筆退貨扣兩次**，
    /// 應付被少認列一倍退貨金額，而總帳照樣平。
    /// </summary>
    [Fact]
    public async Task Generate_WhenDraftReturnPrecedesInvoicing_BillsFullQty()
    {
        SeedGr("GR-RB3", receiptQty: 2m, unitPrice: 15m, taxRate: 0.05m);
        SeedGoodsReturn("GRT-RB3", "GR-RB3", 1m, DocStatus.Draft);
        // 前提釘住：欄位確實已被草稿加過了（否則本測試等於沒測到差異）
        Assert.Equal(1m, _context.GoodsReceiptLines
            .First(l => l.GoodsReceiptId == "GR-RB3" && l.LineNo == 1).ReturnedQty);

        var inv = await _svc.GenerateFromGoodsReceiptAsync("GR-RB3");

        Assert.Equal(2m,    inv.Lines.First().Qty);           // 全額開，回沖留給草稿過帳時做
        Assert.Equal(31.5m, inv.TotalAmount);
    }

    /// <summary>整行都退光且已過帳，就不該出現在發票上（否則會留一行 0 元明細）。</summary>
    [Fact]
    public async Task Generate_WhenFullyReturnedBeforeInvoicing_SkipsLine()
    {
        SeedGr("GR-RB2", receiptQty: 2m, unitPrice: 15m);
        SeedGoodsReturn("GRT-RB2", "GR-RB2", 2m, DocStatus.Posted);

        var inv = await _svc.GenerateFromGoodsReceiptAsync("GR-RB2");

        Assert.Empty(inv.Lines);
        Assert.Equal(0m, inv.TotalAmount);
    }

    [Fact]
    public async Task Accrue_FirstBatch_CreatesInvoiceWithThatQtyOnly()
    {
        SeedGr("GR-A1", receiptQty: 100m, unitPrice: 10m);

        var inv = await _svc.AccrueForReceivedQtyAsync("GR-A1", 1, 60m);

        Assert.Single(inv.Lines);
        Assert.Equal(60m, inv.Lines.First().Qty);
        Assert.Equal(600m, inv.Lines.First().LineAmount);
        Assert.Equal(600m, inv.TotalAmount);   // 只認列已進倉的 60，非收貨的 100
    }

    [Fact]
    public async Task Accrue_SecondBatch_AccumulatesIntoSameInvoice()
    {
        SeedGr("GR-A2", receiptQty: 100m, unitPrice: 10m);

        var first  = await _svc.AccrueForReceivedQtyAsync("GR-A2", 1, 60m);
        var second = await _svc.AccrueForReceivedQtyAsync("GR-A2", 1, 38m);

        // 一張進貨單一張 AP：第二次累加進同一張，不新開發票。
        Assert.Equal(first.APInvoiceId, second.APInvoiceId);
        Assert.Equal(1, _context.APInvoices.Count(i => i.SourceDocId == "GR-A2"));
        Assert.Single(second.Lines);
        Assert.Equal(98m, second.Lines.First().Qty);     // 60 + 38，破損短收的 2 從未認列
        Assert.Equal(980m, second.TotalAmount);
    }

    [Fact]
    public async Task Accrue_WithTax_ComputesTaxOnAccumulatedQty()
    {
        SeedGr("GR-A3", receiptQty: 100m, unitPrice: 300m, taxRate: 0.05m);

        var inv = await _svc.AccrueForReceivedQtyAsync("GR-A3", 1, 100m);

        Assert.Equal(30000m, inv.Lines.First().LineAmount);
        Assert.Equal(1500m,  inv.Lines.First().TaxAmount);   // 30000 × 0.05
        Assert.Equal(31500m, inv.TotalAmount);
    }

    [Fact]
    public async Task Accrue_ExceedingReceiptQty_ClampsToRemaining()
    {
        SeedGr("GR-A4", receiptQty: 100m, unitPrice: 10m);
        await _svc.AccrueForReceivedQtyAsync("GR-A4", 1, 60m);

        // 累計 60 + 50 會超過收貨 100 → 截到剩餘 40（永不超額認列，也不擋住實體作業）。
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-A4", 1, 50m);
        Assert.Equal(100m, inv.Lines.First().Qty);
        Assert.Equal(1000m, inv.TotalAmount);

        // 已全額認列後再加 → 無可再加，直接回原發票、金額不變。
        var inv2 = await _svc.AccrueForReceivedQtyAsync("GR-A4", 1, 10m);
        Assert.Equal(100m, inv2.Lines.First().Qty);
    }

    /// <summary>
    /// 逐次認列前退貨**已過帳**：認列上限必須扣掉它（W4-F12）。
    ///
    /// 開票路徑（GenerateFromGoodsReceiptAsync）早就修了這個洞，但**完整模式走的是這一條**
    /// ——上架時逐次認列——而它的上限一直只看 ReceiptQty。退貨過帳在認列之前時，
    /// ReverseForReturnedQtyAsync 查無發票即回 NoInvoice 什麼都不做，之後沒有第二次機會，
    /// 於是那段退回量被永久多認列，且總帳照樣平、數量型稽核也看不到。
    /// 2026-09-07 在 dev 庫由 RETURN_SUBLEDGER_UNREVERSED 抓到實例（API-2607-00002：
    /// 進貨 04-14、退貨過帳 04-23、上架認列 07-24，多認列 15.75）。
    /// </summary>
    [Fact]
    public async Task Accrue_WhenPostedReturnPrecedesAccrual_AccruesNetOfIt()
    {
        SeedGr("GR-AR1", receiptQty: 2m, unitPrice: 15m, taxRate: 0.05m);
        SeedGoodsReturn("GRT-AR1", "GR-AR1", 1m, DocStatus.Posted);

        // 上架把整批 2 個都認列進來——上限必須是 2 − 1 = 1。
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-AR1", 1, 2m);

        Assert.Single(inv.Lines);
        Assert.Equal(1m,     inv.Lines.First().Qty);      // 錯誤實作為 2
        Assert.Equal(15m,    inv.Lines.First().LineAmount);
        Assert.Equal(15.75m, inv.TotalAmount);            // 錯誤實作為 31.50
    }

    /// <summary>
    /// 逐次認列前退貨**還是草稿**：一個都不能扣。
    /// 與開票路徑同一個理由——GoodsReceiptLine.ReturnedQty 在存草稿當下就加了，
    /// 回沖卻等到過帳；拿它當基準會把同一筆退貨扣兩次。
    /// </summary>
    [Fact]
    public async Task Accrue_WhenDraftReturnPrecedesAccrual_AccruesFullQty()
    {
        SeedGr("GR-AR2", receiptQty: 2m, unitPrice: 15m, taxRate: 0.05m);
        SeedGoodsReturn("GRT-AR2", "GR-AR2", 1m, DocStatus.Draft);
        Assert.Equal(1m, _context.GoodsReceiptLines
            .First(l => l.GoodsReceiptId == "GR-AR2" && l.LineNo == 1).ReturnedQty);

        var inv = await _svc.AccrueForReceivedQtyAsync("GR-AR2", 1, 2m);

        Assert.Equal(2m,    inv.Lines.First().Qty);
        Assert.Equal(31.5m, inv.TotalAmount);
    }

    /// <summary>
    /// 回沖之後的下一批認列不得把剛沖掉的量再加回來（W4-F12 的另一個方向）。
    ///
    /// 分批上架時很常見：先認列一部分 → 退貨過帳（回沖扣掉）→ 剩下的進倉再認列。
    /// 上限若只看 ReceiptQty，第二次認列會把回沖掉的額度當成「還沒認列」再補一次，
    /// 沖了等於沒沖，而且中間的分錄一借一貸各自都平，沒有任何檢查會叫。
    /// </summary>
    [Fact]
    public async Task Accrue_AfterReversal_DoesNotReAccrueReturnedQty()
    {
        SeedGr("GR-AR3", receiptQty: 10m, unitPrice: 10m);

        await _svc.AccrueForReceivedQtyAsync("GR-AR3", 1, 4m);          // 第一批進倉 4
        SeedGoodsReturn("GRT-AR3", "GR-AR3", 1m, DocStatus.Posted);
        await _svc.ReverseForReturnedQtyAsync("GR-AR3", 1, 1m);         // 退 1 → 認列剩 3

        // 刻意送超過剩餘額度的量，讓「上限算錯」在截斷處現形：
        // 上限只看 ReceiptQty 時會截到 10 − 3 ＝ 7 → 累計 10（等於退貨白退）；
        // 正確上限 10 − 1 − 3 ＝ 6 → 累計 9。
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-AR3", 1, 8m);

        // 應認列 ＝ 收貨 10 − 已退 1 ＝ 9，不是 10。
        Assert.Equal(9m,  inv.Lines.First().Qty);
        Assert.Equal(90m, inv.TotalAmount);
    }

    [Fact]
    public async Task Accrue_PostsGrIrToApJournal_ClearingNetAgainstPutAway()
    {
        // 認列產生分錄：借 2152 進貨暫估應付（淨額，沖平上架時的貸記）+ 借 1155 進項稅額
        // / 貸 2100 應付帳款（含稅）。全循環 2152 收斂、2100 與付款端一致。
        SeedGr("GR-J1", receiptQty: 100m, unitPrice: 300m, taxRate: 0.05m);

        var inv = await _svc.AccrueForReceivedQtyAsync("GR-J1", 1, 100m);

        var lines = _context.JournalEntries
            .Include(e => e.Lines)
            .Where(e => e.SourceDocType == "APInvoice" && e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines)
            .ToList();

        var dr2152 = lines.Where(l => l.AccountCode == "2152").Sum(l => l.Debit);
        var dr1155 = lines.Where(l => l.AccountCode == "1155").Sum(l => l.Debit);
        var cr2100 = lines.Where(l => l.AccountCode == "2100").Sum(l => l.Credit);

        Assert.Equal(30000m, dr2152);   // 淨額
        Assert.Equal(1500m,  dr1155);   // 稅額
        Assert.Equal(31500m, cr2100);   // 含稅
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));   // 借貸平衡
    }

    [Fact]
    public async Task Accrue_SecondBatch_PostsOnlyIncrementalJournal()
    {
        // 逐次認列：每次只入該次增量的分錄，不重複整批。
        SeedGr("GR-J2", receiptQty: 100m, unitPrice: 10m);   // 無稅

        await _svc.AccrueForReceivedQtyAsync("GR-J2", 1, 60m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-J2", 1, 38m);

        var entries = _context.JournalEntries
            .Include(e => e.Lines)
            .Where(e => e.SourceDocType == "APInvoice" && e.SourceDocId == inv.APInvoiceId)
            .ToList();
        var cr2100Total = entries
            .SelectMany(e => e.Lines)
            .Where(l => l.AccountCode == "2100")
            .Sum(l => l.Credit);

        Assert.Equal(2, entries.Count);      // 兩次認列＝兩張分錄
        Assert.Equal(980m, cr2100Total);     // 600 + 380，累計＝已認列 98 的金額
    }

    // ── 進貨退出：負向回沖原發票 ──────────────────────────────────────────

    [Fact]
    public async Task Reverse_ReducesAccruedQtyAndTotal()
    {
        SeedGr("GR-R1", receiptQty: 100m, unitPrice: 10m);
        await _svc.AccrueForReceivedQtyAsync("GR-R1", 1, 100m);   // 認列 100

        var rev = await _svc.ReverseForReturnedQtyAsync("GR-R1", 1, 40m);   // 退 40

        var inv = await LoadApAsync("GR-R1");

        Assert.NotNull(inv);
        Assert.Equal(60m, inv!.Lines.First().Qty);
        Assert.Equal(600m, inv.TotalAmount);
    }

    [Fact]
    public async Task Reverse_PostsReversingJournal_DrApCrGrIr()
    {
        SeedGr("GR-R2", receiptQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-R2", 1, 100m);

        await _svc.ReverseForReturnedQtyAsync("GR-R2", 1, 100m);   // 全退

        // 反向分錄：借 2100 應付(含稅) / 貸 2152 暫收(淨) + 貸 1155 進項稅額(稅)。
        var lines = _context.JournalEntries.Include(e => e.Lines)
            .Where(e => e.SourceDocType == "APInvoiceReturn" && e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines).ToList();
        Assert.Equal(31500m, lines.Where(l => l.AccountCode == "2100").Sum(l => l.Debit));   // 借應付含稅
        Assert.Equal(30000m, lines.Where(l => l.AccountCode == "2152").Sum(l => l.Credit));  // 貸暫收淨額
        Assert.Equal(1500m,  lines.Where(l => l.AccountCode == "1155").Sum(l => l.Credit));  // 貸進項稅額
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));                     // 借貸平衡
    }

    [Fact]
    public async Task Reverse_ExceedingAccrued_ClampsAndFloorsAtZero()
    {
        SeedGr("GR-R3", receiptQty: 100m, unitPrice: 10m);
        await _svc.AccrueForReceivedQtyAsync("GR-R3", 1, 60m);   // 只認列 60

        var rev = await _svc.ReverseForReturnedQtyAsync("GR-R3", 1, 90m);   // 退 90 > 已認 60

        var inv = await LoadApAsync("GR-R3");

        Assert.Equal(0m, inv!.Lines.First().Qty);       // 截到已認列量、下限 0
        Assert.Equal(0m, inv.TotalAmount);
    }

    [Fact]
    public async Task Reverse_WhenNoSourceInvoice_ReturnsNull()
    {
        SeedGr("GR-R4", receiptQty: 100m, unitPrice: 10m);
        // 未認列（貨全走待驗未放行）→ 無 AP 可回沖
        var rev = await _svc.ReverseForReturnedQtyAsync("GR-R4", 1, 10m);
        Assert.Equal(ReturnReversalOutcome.NoInvoice, rev.Outcome);
        Assert.False(rev.HasGap);
    }

    [Fact]
    public async Task Reverse_WhenInvoicePaid_NoOp()
    {
        SeedGr("GR-R5", receiptQty: 100m, unitPrice: 10m);
        var accrued = await _svc.AccrueForReceivedQtyAsync("GR-R5", 1, 100m);
        accrued.Status = APInvoiceStatus.Paid;
        _context.SaveChanges();

        var rev = await _svc.ReverseForReturnedQtyAsync("GR-R5", 1, 40m);   // 已付款：本方案不回沖

        var inv = await LoadApAsync("GR-R5");

        Assert.Equal(100m, inv!.Lines.First().Qty);   // 金額不變
        // 不回沖是刻意的，但缺口必須算出來——那 400 元廠商實際上欠公司
        Assert.Equal(ReturnReversalOutcome.SettledInvoice, rev.Outcome);
        Assert.Equal(400m, rev.UnreversedGrossLocal);
        Assert.True(rev.HasGap);
    }

    // ── 委外收貨 AP：先前完全不入總帳 ─────────────────────────────────────

    private void SeedOutsourceReceipt(string rcptId, decimal qty, decimal unitCost,
        decimal taxRate = 0m, decimal penalty = 0m)
    {
        var rec = new SmartFactory.MES.Modules.PUR.Models.OutsourceReceipt
        {
            OutsourceReceiptId = rcptId, OutsourcePurchaseOrderId = "OPO-1",
            SupplierCode = SuppCode, ReceiptDate = new DateOnly(2025, 3, 1),
            CreatedBy = "user1", ExchangeRate = 1m, PlantId = PlantId,
            Status = DocStatus.Confirmed,
        };
        rec.Lines.Add(new SmartFactory.MES.Modules.PUR.Models.OutsourceReceiptLine
        {
            OutsourceReceiptId = rcptId, LineNo = 1, ProductCode = "FG001",
            ReceiptQty = qty, StockQty = qty, UomCode = "PC", StockUomCode = "PC",
            UnitCost = unitCost, TaxRate = taxRate, ExcessScrapPenalty = penalty,
        });
        _context.OutsourceReceipts.Add(rec);
        _context.SaveChanges();
    }

    /// <summary>
    /// 委外的應付認列先前完全不產生分錄——加工費與進項稅在總帳上憑空消失。
    /// 口徑須與進貨 AP 一致：借 2152 + 借 1155 / 貸 2100（含稅）。
    /// </summary>
    [Fact]
    public async Task OutsourceReceipt_GeneratesApAccrualJournal()
    {
        SeedOutsourceReceipt("OR-J1", qty: 100m, unitCost: 50m, taxRate: 0.05m);

        var inv = await _svc.GenerateFromOutsourceReceiptAsync("OR-J1");

        var lines = _context.JournalEntries.Include(e => e.Lines)
            .Where(e => e.SourceDocType == "APInvoice" && e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines).ToList();

        Assert.Equal(5000m, lines.Where(l => l.AccountCode == "2152").Sum(l => l.Debit));
        Assert.Equal(250m,  lines.Where(l => l.AccountCode == "1155").Sum(l => l.Debit));
        Assert.Equal(5250m, lines.Where(l => l.AccountCode == "2100").Sum(l => l.Credit));
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    /// <summary>超耗扣款是發票負項，必須同步沖減入帳的應付淨額（不另開折讓）。</summary>
    [Fact]
    public async Task OutsourceReceipt_ScrapPenalty_ReducesAccruedPayable()
    {
        SeedOutsourceReceipt("OR-J2", qty: 100m, unitCost: 50m, penalty: 380m);

        var inv = await _svc.GenerateFromOutsourceReceiptAsync("OR-J2");

        var lines = _context.JournalEntries.Include(e => e.Lines)
            .Where(e => e.SourceDocType == "APInvoice" && e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines).ToList();

        Assert.Equal(4620m, lines.Where(l => l.AccountCode == "2100").Sum(l => l.Credit));   // 5000 − 380
        Assert.Equal(4620m, inv.TotalAmount);
    }

    /// <summary>冪等：同一張收貨重複產生發票，不得再入一次帳。</summary>
    [Fact]
    public async Task OutsourceReceipt_Idempotent_PostsJournalOnlyOnce()
    {
        SeedOutsourceReceipt("OR-J3", qty: 10m, unitCost: 100m);

        var inv = await _svc.GenerateFromOutsourceReceiptAsync("OR-J3");
        await _svc.GenerateFromOutsourceReceiptAsync("OR-J3");

        Assert.Equal(1, _context.JournalEntries
            .Count(e => e.SourceDocType == "APInvoice" && e.SourceDocId == inv.APInvoiceId));
    }

    // ── 作廢：認列分錄必須回沖，否則 2100 總帳與 AP 子帳脫節 ──────────────

    /// <summary>
    /// VoidAsync 先前只改 Status（雖已檢查期間鎖、註解也寫「作廢以當下日期入帳」），
    /// 實際沒有任何分錄 → 借2152/借1155/貸2100 全數殘留，而
    /// GetOutstandingBalanceAsync 已把 Void 排除在外，總帳與子帳從此對不起來。
    /// </summary>
    [Fact]
    public async Task Void_AfterAccrual_ReversesJournalToZero()
    {
        SeedGr("GR-V1", receiptQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-V1", 1, 100m);

        await _svc.VoidAsync(inv.APInvoiceId);

        var all = _context.JournalEntries.Include(e => e.Lines)
            .Where(e => e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines).ToList();

        foreach (var g in all.GroupBy(l => l.AccountCode))
            Assert.Equal(0m, g.Sum(l => l.Debit) - g.Sum(l => l.Credit));

        var reversal = _context.JournalEntries.Include(e => e.Lines)
            .Single(e => e.SourceDocType == "APInvoice-Void" && e.SourceDocId == inv.APInvoiceId);
        Assert.Equal(reversal.Lines.Sum(l => l.Debit), reversal.Lines.Sum(l => l.Credit));
        Assert.Equal(31500m, reversal.Lines.Where(l => l.AccountCode == "2100").Sum(l => l.Debit));
    }

    /// <summary>
    /// 已部分退貨回沖過的發票再作廢：只對調認列分錄會把退貨已沖掉的部分再沖一次，
    /// 必須連 APInvoiceReturn 一併對調，淨額才真的歸零。
    /// </summary>
    [Fact]
    public async Task Void_AfterPartialReturnReversal_StillNetsToZero()
    {
        SeedGr("GR-V2", receiptQty: 100m, unitPrice: 300m, taxRate: 0.05m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-V2", 1, 100m);
        await _svc.ReverseForReturnedQtyAsync("GR-V2", 1, 40m);   // 先退 40

        await _svc.VoidAsync(inv.APInvoiceId);

        var all = _context.JournalEntries.Include(e => e.Lines)
            .Where(e => e.SourceDocId == inv.APInvoiceId)
            .SelectMany(e => e.Lines).ToList();

        foreach (var g in all.GroupBy(l => l.AccountCode))
            Assert.Equal(0m, g.Sum(l => l.Debit) - g.Sum(l => l.Credit));
    }

    /// <summary>重複作廢不得產生第二筆反向分錄。</summary>
    [Fact]
    public async Task Void_Twice_DoesNotDoubleReverse()
    {
        SeedGr("GR-V3", receiptQty: 10m, unitPrice: 100m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-V3", 1, 10m);

        await _svc.VoidAsync(inv.APInvoiceId);
        await _svc.VoidAsync(inv.APInvoiceId);

        Assert.Equal(1, _context.JournalEntries
            .Count(e => e.SourceDocType == "APInvoice-Void" && e.SourceDocId == inv.APInvoiceId));
    }

    [Fact]
    public async Task Accrue_WhenInvoiceNotOpen_Throws()
    {
        SeedGr("GR-A5", receiptQty: 100m, unitPrice: 10m);
        var inv = await _svc.AccrueForReceivedQtyAsync("GR-A5", 1, 50m);

        // 模擬發票已進入付款狀態後，不得再追加認列。
        inv.Status = APInvoiceStatus.PartialPaid;
        _context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.AccrueForReceivedQtyAsync("GR-A5", 1, 20m));
    }

    /// <summary>
    /// 回沖後把發票從 DbContext 重讀出來。
    ///
    /// <c>ReverseForReturnedQtyAsync</c> 改回傳 <c>ReturnReversalResult</c>（帶「沖了多少／
    /// 沒沖多少」）而不再回傳發票物件——因為呼叫端真正需要知道的是缺口，而原本回傳發票、
    /// 四個呼叫點全部丟掉，「已收款發票遇上退貨完全沒回沖」才會一路無聲。
    /// 這些測試要看的是**發票被改成什麼樣**，所以改成回資料庫拿。
    /// </summary>
    private async Task<APInvoice> LoadApAsync(string goodsReceiptId)
        => await _context.APInvoices.IgnoreQueryFilters().Include(i => i.Lines)
                 .SingleAsync(i => i.SourceDocType == "GoodsReceipt" && i.SourceDocId == goodsReceiptId);
}
