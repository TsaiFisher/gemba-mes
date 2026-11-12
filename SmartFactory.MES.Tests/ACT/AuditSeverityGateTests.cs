using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using Moq;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// 關帳前稽核的嚴重度閘門（W4-F7）。
///
/// 起點是 2026-09-06 在 dev 庫實測：ADM 廠關帳被 25 筆異常擋下，其中 19 筆是
/// ERP_SYNC_LAG——AP/AR 發票逾 24 小時未拋轉至外部 ERP。而全庫沒有任何
/// controller／排程／背景服務呼叫過 <c>IErpSyncService.MarkSyncedAsync</c>
/// （唯一的參照是 FINModule 的 DI 註冊），發票的 ErpSyncStatus 生下來是 Pending
/// 就永遠是 Pending。把它掛成硬閘的實際效果不是「守住拋轉」，
/// 而是「關帳在任何部署上都做不到」，而且每開一張發票就多一筆、只增不減。
///
/// 這裡守的是分界線本身：**帳錯了才擋關帳；外部介面的狀態只提示**。
/// </summary>
public class AuditSeverityGateTests : TestBase
{
    private const string PlantId = "P001";

    private static AuditFailureDetail Finding(string auditType, string docId)
        => new() { AuditType = auditType, DocId = docId, PlantId = PlantId, ErrorDescription = $"{auditType} on {docId}" };

    private AccountingPeriodService BuildSvc(params AuditFailureDetail[] findings)
    {
        var result = new DataAuditResult();
        foreach (var f in findings) result.Failures.Add(f);

        var audit = new Mock<IDataAuditService>();
        audit.Setup(a => a.RunPreClosingAuditAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(result);
        return new AccountingPeriodService(_context, audit.Object);
    }

    private void SeedClosingPeriod()
    {
        _context.CurrentPlantId = PlantId;
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId = "202601", PlantId = PlantId, Year = 2026, Month = 1,
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 1, 31),
            Status = PeriodStatus.Closing,
        });
        _context.SaveChanges();
    }

    /// <summary>ERP 拋轉滯留不論幾筆都不該擋關帳——它講的是外部系統，不是本系統的帳。</summary>
    [Fact]
    public async Task ClosePeriod_WithOnlyErpSyncLagFindings_Succeeds()
    {
        SeedClosingPeriod();
        var svc = BuildSvc(
            Finding("ERP_SYNC_LAG", "API-001"),
            Finding("ERP_SYNC_LAG", "API-002"),
            Finding("ERP_SYNC_LAG", "ARI-001"));

        await svc.ClosePeriodAsync("202601", PlantId, "finance_mgr");

        var p = await svc.GetByIdAsync("202601", PlantId);
        Assert.Equal(PeriodStatus.Closed, p!.Status);
        Assert.Equal("finance_mgr", p.ClosedBy);
    }

    /// <summary>只要有一筆是帳本身的問題就必須擋——提示類不會把它稀釋掉。</summary>
    [Fact]
    public async Task ClosePeriod_WithOneBlockingFindingAmongAdvisories_IsStillRefused()
    {
        SeedClosingPeriod();
        var svc = BuildSvc(
            Finding("ERP_SYNC_LAG", "API-001"),
            Finding("FIFO_LAYER_QTY_MISMATCH", "PA2103"),
            Finding("ERP_SYNC_LAG", "API-002"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClosePeriodAsync("202601", PlantId, "finance_mgr"));

        // 筆數只能數擋關帳的那些：報 3 筆會讓操作者修完 1 筆之後，
        // 對著剩下兩筆怎麼修都不會消失的提示發呆。
        var head = PzText.Get("Act_PreCloseAuditFailedFmt", PlantId, "202601", 1, "\u0001", "", "");
        Assert.StartsWith(head[..head.IndexOf('\u0001')], ex.Message);   // 含「1 筆」
        Assert.Contains("FIFO_LAYER_QTY_MISMATCH", ex.Message);
        Assert.DoesNotContain("ERP_SYNC_LAG", ex.Message);

        var p = await svc.GetByIdAsync("202601", PlantId);
        Assert.Equal(PeriodStatus.Closing, p!.Status);
    }

    /// <summary>
    /// 「不擋關帳」不等於「不用看」：提示類必須照樣留在 Failures 裡，
    /// 否則資料稽核診斷畫面會整批消失，等於把問題藏起來而不是分級。
    /// </summary>
    [Fact]
    public async Task AdvisoryFindings_AreStillListed_JustNotBlocking()
    {
        var result = new DataAuditResult();
        result.Failures.Add(Finding("ERP_SYNC_LAG", "API-001"));
        result.Failures.Add(Finding("GS_NO_AR_INVOICE", "GS-001"));

        Assert.Equal(2, result.Failures.Count);
        Assert.Single(result.BlockingFailures);
        Assert.Equal("GS_NO_AR_INVOICE", result.BlockingFailures[0].AuditType);
        Assert.False(result.IsPassed);
    }

    /// <summary>沒有列在提示清單裡的一律當擋關帳——新增稽核忘了分類時，預設要往嚴的那邊倒。</summary>
    [Theory]
    [InlineData("PO_OVER_RECEIPT",         true)]
    [InlineData("INV_ROLLFORWARD",         true)]
    [InlineData("SOMETHING_BRAND_NEW",     true)]
    [InlineData("ERP_SYNC_LAG",            false)]
    public void IsBlocking_DefaultsToBlockingForUnknownTypes(string auditType, bool expected)
        => Assert.Equal(expected, AuditSeverityRules.IsBlocking(auditType));
}
