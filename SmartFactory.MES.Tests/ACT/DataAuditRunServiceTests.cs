using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// DataAuditRunService 持久化測試（business_data_flow_reconciliation.md §9 P-8）：
/// 執行 → 寫入 DataAuditRun/DataAuditFinding、逾保留期舊紀錄清除。
/// </summary>
public class DataAuditRunServiceTests : TestBase
{
    private const string Plant = "P001";
    private readonly DataAuditRunService _svc;

    public DataAuditRunServiceTests()
    {
        _svc = new DataAuditRunService(_context, new DataAuditService(_context));
    }

    [Fact]
    public async Task RunAndPersist_NoFailures_PersistsEmptyRun()
    {
        var run = await _svc.RunAndPersistAsync(Plant, "Manual", "user1");

        var saved = Assert.Single(_context.DataAuditRuns);
        Assert.Equal(run.RunId, saved.RunId);
        Assert.Equal(0, saved.FailureCount);
        Assert.Equal("Manual", saved.TriggerType);
        Assert.Equal("user1", saved.CreatedBy);
        Assert.Empty(_context.DataAuditFindings);
    }

    [Fact]
    public async Task RunAndPersist_WithFailures_PersistsFindings()
    {
        // 不平衡傳票 → JOURNAL_UNBALANCED 一筆
        _context.JournalEntries.Add(new JournalEntry
        {
            JournalId = "JE001", PeriodId = "202607", EntryDate = new DateOnly(2026, 7, 1),
            SourceDocType = "Manual", SourceDocId = "X", PlantId = Plant,
            Lines =
            {
                new JournalLine { JournalId = "JE001", LineNo = 1, AccountCode = "1130", AccountName = "銀行存款", Debit = 100m, Credit = 0m },
                new JournalLine { JournalId = "JE001", LineNo = 2, AccountCode = "2100", AccountName = "應付帳款", Debit = 0m, Credit = 60m },
            },
        });
        _context.SaveChanges();

        var run = await _svc.RunAndPersistAsync(Plant, "Scheduled", "SYSTEM");

        Assert.Equal(1, run.FailureCount);
        var finding = Assert.Single(_context.DataAuditFindings);
        Assert.Equal("JOURNAL_UNBALANCED", finding.AuditType);
        Assert.Equal("JE001", finding.DocId);
        Assert.Equal(run.RunId, finding.RunId);
        Assert.Equal(Plant, finding.PlantId);
    }

    /// <summary>
    /// 逐筆嚴重度覆寫必須跟著紀錄一起存下來（W4-F13 的收尾）。
    ///
    /// 不存的話，關帳閘門看的是當次執行的記憶體結果（放行），而稽核報表是從
    /// DataAuditFindings 讀回來、再用「型別 → 嚴重度」規則重算的，會顯示「擋關帳」。
    /// 同一筆異常在兩個地方給出相反的答案，操作者只能相信畫面上那個錯的。
    /// </summary>
    [Fact]
    public async Task RunAndPersist_PreservesPerFindingSeverityOverride()
    {
        // 出貨日早於本廠最早會計期間 → AuditShipmentInvoiceGapAsync 覆寫成「僅提示」
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = "202606", PlantId = Plant, Year = 2026, Month = 6,
            StartDate = new DateOnly(2026, 6, 1), EndDate = new DateOnly(2026, 6, 30),
        });
        _context.GoodsShipments.Add(new SmartFactory.MES.Modules.ORD.Models.GoodsShipment
        {
            GoodsShipmentId = "GS-PRE", CompanyCode = "C001", PlantId = Plant,
            Status = SmartFactory.MES.Modules.INV.Models.DocStatus.Confirmed,
            ShipDate = new DateOnly(2026, 5, 13),
        });
        _context.SaveChanges();

        await _svc.RunAndPersistAsync(Plant, "Manual", "user1");

        var finding = Assert.Single(_context.DataAuditFindings.Where(f => f.AuditType == "GS_NO_AR_INVOICE"));
        Assert.False(finding.BlockingOverride);        // 存下來的是「不擋」，不是 null
    }

    [Fact]
    public async Task RunAndPersist_StaleRuns_ArePruned()
    {
        _context.DataAuditRuns.Add(new DataAuditRun
        {
            RunAt = DateTime.UtcNow.AddDays(-(DataAuditRunService.RetentionDays + 1)),
            TriggerType = "Scheduled", CreatedBy = "SYSTEM",
            Findings = { new DataAuditFinding { AuditType = "OLD", DocId = "X" } },
        });
        _context.DataAuditRuns.Add(new DataAuditRun
        {
            RunAt = DateTime.UtcNow.AddDays(-1),
            TriggerType = "Scheduled", CreatedBy = "SYSTEM",
        });
        _context.SaveChanges();

        await _svc.RunAndPersistAsync(Plant, "Manual", "user1");

        // 逾期 1 筆被清除（連同其 findings），保留期內 1 筆 + 本次新增 1 筆
        Assert.Equal(2, _context.DataAuditRuns.Count());
        Assert.DoesNotContain(_context.DataAuditFindings, f => f.AuditType == "OLD");
    }
}
