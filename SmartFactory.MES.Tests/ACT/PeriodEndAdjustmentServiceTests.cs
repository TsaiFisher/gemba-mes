using SmartFactory.MES.Modules.ACT;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 月結步驟 9：外幣重評／NRV／ECL 入總帳。原本這三支只寫評估表、步驟 9 只是打勾——
/// 關帳關得了，總帳卻沒有期末評價。
/// </summary>
public class PeriodEndAdjustmentServiceTests : TestBase
{
    private const string PlantId = "P001";
    private const string Jun     = "202606";
    private const string Jul     = "202607";

    private readonly PeriodEndAdjustmentService _svc;
    private readonly AccountingPeriodService    _periods;
    private int _seq;

    public PeriodEndAdjustmentServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        var docNo = new Mock<IDocNumberService>();
        docNo.Setup(d => d.NextAsync("JournalEntry", It.IsAny<string>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
             .Returns(() => Task.FromResult($"JRN{++_seq:D6}"));
        _periods = new AccountingPeriodService(_context);
        _svc = new PeriodEndAdjustmentService(_context, _periods, new JournalEntryService(_context, docNo.Object, _periods));

        SeedPeriod(Jun, 6, PeriodStatus.Closing);
        SeedPeriod(Jul, 7, PeriodStatus.Open);
    }

    private void SeedPeriod(string id, int month, PeriodStatus status)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = id, PlantId = PlantId, Year = 2026, Month = month,
            StartDate = new DateOnly(2026, month, 1),
            EndDate   = new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)),
            Status    = status,
        });
        _context.SaveChanges();
    }

    private void SeedFx(string docId, RevaluationDocType type, decimal gain)
    {
        _context.FxRevaluationEntries.Add(new FxRevaluationEntry
        {
            PeriodId = Jun, DocType = type, DocId = docId, CurrencyCode = "USD", ExchangeGainLoss = gain, PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    private void SeedNrv(string period, decimal loss)
    {
        _context.InventoryNrvAssessments.Add(new InventoryNrvAssessment
        {
            PeriodId = period, ProductCode = "P-" + period, ImpairmentLoss = loss, PlantId = PlantId,
        });
        _context.SaveChanges();
    }

    /// <summary>某張分錄逐科目「借−貸」，依科目排序，例如 "1170:120|4190:-120"。</summary>
    private string Net(string journalId) => string.Join("|", _context.JournalLines
        .Where(l => l.JournalId == journalId).ToList()
        .GroupBy(l => l.AccountCode).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => $"{g.Key}:{g.Sum(l => l.Debit - l.Credit):0.####}"));

    [Fact]
    public async Task 外幣重評_利益借控制科目_損失貸控制科目_次期初迴轉()
    {
        SeedFx("AR-1", RevaluationDocType.ARInvoice, 120m);    // 應收變大＝利益
        SeedFx("AP-1", RevaluationDocType.APInvoice, -300m);   // 應付變大＝損失

        var r = await _svc.PostAsync(Jun, PlantId);

        Assert.Equal("1170:120|2100:-300|4190:-120|5199:300", Net(r.FxJournalId!));
        var entry = _context.JournalEntries.Single(j => j.JournalId == r.FxJournalId);
        Assert.Equal((Jun, new DateOnly(2026, 6, 30), JournalEntryType.Adjustment), (entry.PeriodId, entry.EntryDate, entry.Type));

        Assert.Equal("1170:-120|2100:300|4190:120|5199:-300", Net(r.FxReversalJournalId!));
        var reversal = _context.JournalEntries.Single(j => j.JournalId == r.FxReversalJournalId);
        Assert.Equal((Jul, new DateOnly(2026, 7, 1)), (reversal.PeriodId, reversal.EntryDate));

        // 逐張發票拆行，SourceRef＝發票號
        Assert.Equal(new[] { "AP-1", "AR-1" }, _context.JournalLines.Where(l => l.JournalId == r.FxJournalId)
            .Select(l => l.SourceRef!).Distinct().OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task 外幣重評_重跑只補差額_沒有差額不開分錄()
    {
        SeedFx("AR-1", RevaluationDocType.ARInvoice, 120m);
        await _svc.PostAsync(Jun, PlantId);

        var again = await _svc.PostAsync(Jun, PlantId);
        Assert.Null(again.FxJournalId);
        Assert.Equal(2, _context.JournalEntries.Count());

        // 重評結果改成 100（例如改了期末匯率重跑步驟 6）→ 只補 −20
        _context.FxRevaluationEntries.Single().ExchangeGainLoss = 100m;
        _context.SaveChanges();
        var delta = await _svc.PostAsync(Jun, PlantId);
        Assert.Equal("1170:-20|5199:20", Net(delta.FxJournalId!));
        Assert.Equal("1170:20|5199:-20", Net(delta.FxReversalJournalId!));
    }

    /// <summary>
    /// 期末那張存檔了、迴轉那張才失敗（例如下一期已關帳）：重跑時期末已無差額，
    /// 迴轉仍要補回來——否則下一期收付款會把同一段匯差再認一次。
    /// </summary>
    [Fact]
    public async Task 外幣重評_迴轉缺漏時_重跑補開迴轉且不重複期末分錄()
    {
        SeedFx("AR-1", RevaluationDocType.ARInvoice, 120m);
        _context.AccountingPeriods.Single(p => p.PeriodId == Jul).Status = PeriodStatus.Closed;
        _context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.PostAsync(Jun, PlantId));
        Assert.Single(_context.JournalEntries);   // 期末那張已存檔

        _context.AccountingPeriods.Single(p => p.PeriodId == Jul).Status = PeriodStatus.Reopened;
        _context.SaveChanges();
        var r = await _svc.PostAsync(Jun, PlantId);

        Assert.Null(r.FxJournalId);
        Assert.Equal("1170:-120|4190:120", Net(r.FxReversalJournalId!));
        Assert.Equal(2, _context.JournalEntries.Count());
    }

    [Fact]
    public async Task 月結步驟10_步驟9已完成時關帳前再補一次差額_略過則不入帳()
    {
        foreach (var (step9, expectedCalls) in new[] { (ChecklistItemStatus.Completed, 1), (ChecklistItemStatus.Skipped, 0) })
        {
            var adj = new Mock<IPeriodEndAdjustmentService>();
            adj.Setup(a => a.PostAsync(Jun, PlantId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PeriodEndAdjustmentResult(null, null, "J9", null, new[] { "NRV 存貨跌價 J9（增提 10）" }));
            var monthEnd = new MonthEndService(_context, _periods, adjustments: adj.Object);
            var cl = await monthEnd.InitChecklistAsync(Jun, PlantId);
            foreach (var i in _context.ChecklistItems.Where(i => i.ChecklistId == cl.ChecklistId))
                i.Status = i.StepNo == 9 ? step9 : i.StepNo < 10 ? ChecklistItemStatus.Completed : ChecklistItemStatus.Pending;
            var p = _context.AccountingPeriods.Single(x => x.PeriodId == Jun);
            p.Status = PeriodStatus.Closing;
            _context.SaveChanges();

            await monthEnd.CompleteStepAsync(cl.ChecklistId, 10, "u1");

            adj.Verify(a => a.PostAsync(Jun, PlantId, It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
            _context.ChecklistItems.RemoveRange(_context.ChecklistItems.Where(i => i.ChecklistId == cl.ChecklistId));
            _context.MonthEndChecklists.Remove(_context.MonthEndChecklists.Single(c => c.ChecklistId == cl.ChecklistId));
            _context.SaveChanges();
        }
    }

    [Fact]
    public async Task NRV_備抵調到等於評估額_下期評估變小就迴轉()
    {
        SeedNrv(Jun, 216m);
        var jun = await _svc.PostAsync(Jun, PlantId);
        Assert.Equal("1419:-216|5240:216", Net(jun.NrvJournalId!));

        // 6 月關帳，7 月評估只剩 100 → 迴轉 116（備抵是累計餘額，不是每期重提）
        _context.AccountingPeriods.Single(p => p.PeriodId == Jun).Status = PeriodStatus.Closed;
        SeedNrv(Jul, 100m);
        var jul = await _svc.PostAsync(Jul, PlantId);
        Assert.Equal("1419:116|5240:-116", Net(jul.NrvJournalId!));

        Assert.Null((await _svc.PostAsync(Jul, PlantId)).NrvJournalId);   // 重跑無差額
    }

    [Fact]
    public async Task ECL_增提借預期信用減損損失_貸備抵損失()
    {
        _context.EclAllowances.AddRange(
            new EclAllowance { PeriodId = Jun, AgingBucket = "Current", EclAmount = 30m, PlantId = PlantId },
            new EclAllowance { PeriodId = Jun, AgingBucket = "90+",     EclAmount = 70m, PlantId = PlantId });
        _context.SaveChanges();

        var r = await _svc.PostAsync(Jun, PlantId);

        Assert.Equal("1179:-100|6250:100", Net(r.EclJournalId!));
    }

    /// <summary>沒跑評估不等於評估結果為 0——否則前期提列的備抵會被整筆迴轉。</summary>
    [Fact]
    public async Task 本期沒有評估資料_該項略過且不動既有備抵()
    {
        SeedNrv(Jun, 216m);
        await _svc.PostAsync(Jun, PlantId);
        _context.AccountingPeriods.Single(p => p.PeriodId == Jun).Status = PeriodStatus.Closed;

        var jul = await _svc.PostAsync(Jul, PlantId);

        Assert.Null(jul.NrvJournalId);
        Assert.Null(jul.EclJournalId);
        Assert.Null(jul.FxJournalId);
        Assert.Contains(MonthEndNote.Token("Note_NrvNone"), jul.Notes);
        Assert.Single(_context.JournalEntries);
    }

    [Fact]
    public async Task 已關帳期間_拒絕()
    {
        _context.AccountingPeriods.Single(p => p.PeriodId == Jun).Status = PeriodStatus.Closed;
        _context.SaveChanges();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.PostAsync(Jun, PlantId));
    }

    [Fact]
    public async Task 月結步驟9_完成時入帳並把結果寫進備註()
    {
        var adj = new Mock<IPeriodEndAdjustmentService>();
        adj.Setup(a => a.PostAsync(Jun, PlantId, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new PeriodEndAdjustmentResult("J1", "J2", null, null, new[] { "外幣重評 J1", "NRV 存貨跌價：本期沒有評估結果，未入帳" }));
        var monthEnd = new MonthEndService(_context, _periods, adjustments: adj.Object);
        var cl = await monthEnd.InitChecklistAsync(Jun, PlantId);
        foreach (var i in _context.ChecklistItems.Where(i => i.ChecklistId == cl.ChecklistId && i.StepNo < 9))
            i.Status = ChecklistItemStatus.Completed;
        _context.SaveChanges();

        await monthEnd.CompleteStepAsync(cl.ChecklistId, 9, "u1");

        adj.Verify(a => a.PostAsync(Jun, PlantId, It.IsAny<CancellationToken>()), Times.Once);
        var step9 = _context.ChecklistItems.Single(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 9);
        Assert.Equal(ChecklistItemStatus.Completed, step9.Status);
        Assert.Equal("外幣重評 J1; NRV 存貨跌價：本期沒有評估結果，未入帳", step9.Notes);
    }

    /// <summary>入帳服務與清單共用 DbContext：先標完成再入帳的話，中途失敗時「已完成」會被前段的存檔一起帶進去。</summary>
    [Fact]
    public async Task 月結步驟9_入帳失敗時步驟不算完成()
    {
        var adj = new Mock<IPeriodEndAdjustmentService>();
        // 模擬第一條腿的分錄已在同一個 DbContext 上存檔、第二條腿才失敗
        adj.Setup(a => a.PostAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback(() => _context.SaveChanges())
           .ThrowsAsync(new InvalidOperationException("boom"));
        var monthEnd = new MonthEndService(_context, _periods, adjustments: adj.Object);
        var cl = await monthEnd.InitChecklistAsync(Jun, PlantId);
        foreach (var i in _context.ChecklistItems.Where(i => i.ChecklistId == cl.ChecklistId && i.StepNo < 9))
            i.Status = ChecklistItemStatus.Completed;
        _context.SaveChanges();

        await Assert.ThrowsAsync<InvalidOperationException>(() => monthEnd.CompleteStepAsync(cl.ChecklistId, 9, "u1"));

        _context.ChangeTracker.Clear();
        Assert.Equal(ChecklistItemStatus.Pending,
            _context.ChecklistItems.Single(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 9).Status);
    }
}
