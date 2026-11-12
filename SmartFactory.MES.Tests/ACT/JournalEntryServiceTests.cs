using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// JournalEntryService 業務規則測試。
/// 覆蓋自動/手動分錄建立、期間對應、多行序號、外幣 AmountLocal 計算。
/// </summary>
public class JournalEntryServiceTests : TestBase
{
    private const string PlantId = "P001";

    private readonly JournalEntryService         _journalSvc;
    private readonly AccountingPeriodService     _periodSvc;
    private readonly Mock<IDocNumberService>     _mockDocNo = new();
    private int _journalSeq;

    public JournalEntryServiceTests()
    {
        _context.CurrentPlantId = PlantId;

        _mockDocNo
            .Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(),
                It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult($"JRN{++_journalSeq:D6}"));

        _periodSvc  = new AccountingPeriodService(_context);
        _journalSvc = new JournalEntryService(_context, _mockDocNo.Object, _periodSvc);
    }

    // ── CreateAutoEntryAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAutoEntry_DateInPeriod_UsesPeriodId()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var entry = await _journalSvc.CreateAutoEntryAsync(
            "GoodsReceipt", "GR001",
            new DateOnly(2026, 1, 15), PlantId,
            BuildLines(debit: 1000m, credit: 1000m));

        Assert.Equal("202601", entry.PeriodId);
        Assert.Equal(JournalEntryType.Automatic, entry.Type);
        Assert.Equal("GoodsReceipt", entry.SourceDocType);
        Assert.Equal("GR001",        entry.SourceDocId);
    }

    [Fact]
    public async Task CreateAutoEntry_DateOutsidePeriod_FallsBackToYYYYMM()
    {
        // 無期間設定時，PeriodId 由日期推算：YYYYMM
        var entry = await _journalSvc.CreateAutoEntryAsync(
            "APPayment", "PMT001",
            new DateOnly(2026, 3, 10), PlantId,
            BuildLines(debit: 500m, credit: 500m));

        Assert.Equal("202603", entry.PeriodId);
    }

    [Fact]
    public async Task CreateAutoEntry_MultiLine_LineNoSequential()
    {
        var lines = new List<JournalLineRequest>
        {
            new("4100", "銷貨收入",   0m,    1000m),
            new("1200", "應收帳款",   1000m, 0m),
            new("2105", "營業稅",     0m,    50m),
            new("1200", "應收帳款",   50m,   0m),
        };

        var entry = await _journalSvc.CreateAutoEntryAsync(
            "ARInvoice", "ARI001",
            new DateOnly(2026, 6, 1), PlantId, lines);

        var lineNos = entry.Lines.Select(l => l.LineNo).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, lineNos);
    }

    [Fact]
    public async Task CreateAutoEntry_DebitCreditBalance_Persisted()
    {
        // 借貸必須平衡（規則由呼叫端保證；service 如實記錄）
        var entry = await _journalSvc.CreateAutoEntryAsync(
            "GoodsReceipt", "GR002",
            new DateOnly(2026, 6, 1), PlantId,
            BuildLines(debit: 5000m, credit: 5000m));

        Assert.Equal(5000m, entry.Lines.Sum(l => l.Debit));
        Assert.Equal(5000m, entry.Lines.Sum(l => l.Credit));
    }

    // ── CreateManualEntryAsync ────────────────────────────────────────────

    [Fact]
    public async Task CreateManualEntry_ForeignCurrency_ComputesAmountLocal()
    {
        // AmountLocal = Amount × ExchangeRate（若 AmountLocal 未傳入）
        // 配上貸方行才是合法的複式分錄（寫入端已有借貸平衡守門）。
        var lines = new List<JournalLineRequest>
        {
            new("1200", "應收帳款", Debit: 1000m, Credit: 0m,
                CurrencyCode: "USD", Amount: 1000m, ExchangeRate: 32m, AmountLocal: 0m),
            new("4100", "銷貨收入", Debit: 0m, Credit: 1000m,
                CurrencyCode: "USD", Amount: 1000m, ExchangeRate: 32m, AmountLocal: 0m),
        };

        var entry = await _journalSvc.CreateManualEntryAsync(
            "202606", new DateOnly(2026, 6, 1), PlantId, lines);

        Assert.Equal(32000m, entry.Lines.First().AmountLocal);  // 1000 × 32
    }

    [Fact]
    public async Task CreateManualEntry_ExplicitAmountLocal_PreservesValue()
    {
        // 若呼叫端明確傳入 AmountLocal（如含匯率差），應保留不覆蓋
        var lines = new List<JournalLineRequest>
        {
            new("1200", "應收帳款", Debit: 1000m, Credit: 0m,
                CurrencyCode: "USD", Amount: 1000m, ExchangeRate: 32m, AmountLocal: 31950m),
            new("4100", "銷貨收入", Debit: 0m, Credit: 1000m,
                CurrencyCode: "USD", Amount: 1000m, ExchangeRate: 32m, AmountLocal: 31950m),
        };

        var entry = await _journalSvc.CreateManualEntryAsync(
            "202606", new DateOnly(2026, 6, 1), PlantId, lines);

        Assert.Equal(31950m, entry.Lines.First().AmountLocal);  // 明確值不被覆蓋
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    // ── 借貸平衡守門（寫入端）────────────────────────────────────────────

    /// <summary>
    /// 不平衡的分錄必須在入口就被擋下。先前只有 DataAuditService §3.4 事後稽核，
    /// 不平衡的分錄照樣落地並影響財報，要等下一次稽核才會被發現。
    /// </summary>
    [Fact]
    public async Task CreateAutoEntry_Unbalanced_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _journalSvc.CreateAutoEntryAsync(
                "GoodsReceipt", "GR-BAD", new DateOnly(2026, 4, 1), PlantId,
                BuildLines(debit: 1000m, credit: 900m)));

        PzAssert.Message("Act_JournalUnbalancedFmt", ex.Message);
        Assert.Empty(_context.JournalEntries);   // 不得留下半張分錄
    }

    [Fact]
    public async Task CreateManualEntry_Unbalanced_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _journalSvc.CreateManualEntryAsync(
                "202604", new DateOnly(2026, 4, 1), PlantId,
                BuildLines(debit: 100m, credit: 0m)));
    }

    [Fact]
    public async Task CreateAdjustmentEntry_Unbalanced_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _journalSvc.CreateAdjustmentEntryAsync(
                "FxRevaluation", null, new DateOnly(2026, 4, 1), PlantId,
                BuildLines(debit: 0m, credit: 250m)));
    }

    [Fact]
    public async Task CreateAutoEntry_NoLines_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _journalSvc.CreateAutoEntryAsync(
                "GoodsReceipt", "GR-EMPTY", new DateOnly(2026, 4, 1), PlantId,
                new List<JournalLineRequest>()));
    }

    /// <summary>多行、借貸各自跨行加總後平衡＝合法（守門不得誤擋一借多貸）。</summary>
    [Fact]
    public async Task CreateAutoEntry_MultiLineBalanced_Succeeds()
    {
        var entry = await _journalSvc.CreateAutoEntryAsync(
            "APInvoice", "API-001", new DateOnly(2026, 4, 1), PlantId,
            new List<JournalLineRequest>
            {
                new("2152", "進貨暫估應付", 30000m, 0m),
                new("1155", "進項稅額",      1500m, 0m),
                new("2100", "應付帳款",          0m, 31500m),
            });

        Assert.Equal(3, entry.Lines.Count);
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }

    private static IList<JournalLineRequest> BuildLines(decimal debit, decimal credit)
        => new List<JournalLineRequest>
        {
            new("1100", "現金",     debit,  0m),
            new("4100", "銷貨收入", 0m,     credit),
        };

    private void SeedPeriod(string periodId, DateOnly start, DateOnly end)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId  = periodId,
            PlantId   = PlantId,
            Year      = start.Year,
            Month     = start.Month,
            StartDate = start,
            EndDate   = end,
            Status    = PeriodStatus.Open,
        });
        _context.SaveChanges();
    }
}
