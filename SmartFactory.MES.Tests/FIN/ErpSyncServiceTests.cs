using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.FIN.Models;
using SmartFactory.MES.Modules.FIN.Services;

namespace SmartFactory.MES.Tests.FIN;

/// <summary>
/// §5.4 ERP 憑證拋轉與金額校對資料流 — EP/BVA 測試。
/// 對應規格書 §5.4 全部 5 個案例（2 EP Valid + 1 EP Invalid + 2 BVA）。
/// </summary>
public class ErpSyncServiceTests : TestBase
{
    private const string PlantId  = "P001";
    private const string SuppCode = "SUP001";

    private readonly ErpSyncService _syncSvc;

    public ErpSyncServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _syncSvc = new ErpSyncService(_context);
    }

    // ── EP Valid ─────────────────────────────────────────────────────────────

    /// <summary>
    /// EP Valid: Pending → Synced（單次拋轉成功，取得 ERP 傳票號）。
    /// §5.4 EP Valid 第 1 案例。
    /// </summary>
    [Fact]
    public async Task MarkSynced_PendingToSynced_VoucherNoRecorded()
    {
        var inv = SeedInvoice("API001", totalAmount: 5000m);

        await _syncSvc.MarkSyncedAsync("API001", "ERP-VOU-001");

        var updated = await _context.APInvoices.FindAsync("API001");
        Assert.Equal(ErpSyncStatus.Synced,    updated!.ErpSyncStatus);
        Assert.Equal("ERP-VOU-001",           updated.ErpVoucherNo);
        Assert.NotNull(updated.ErpSyncTime);
        Assert.Null(updated.ErpErrorMessage);
    }

    /// <summary>
    /// EP Valid: Pending → Failed → Synced（重試後成功同步）。
    /// §5.4 EP Valid 第 2 案例。
    /// </summary>
    [Fact]
    public async Task MarkSynced_AfterFailed_SyncedWithRetry()
    {
        var inv = SeedInvoice("API002", totalAmount: 3000m);

        // 第一次失敗
        await _syncSvc.MarkFailedAsync("API002", "網路逾時");
        var after1st = await _context.APInvoices.FindAsync("API002");
        Assert.Equal(ErpSyncStatus.Failed, after1st!.ErpSyncStatus);
        Assert.Equal(1, after1st.ErpRetryCount);

        // 重試成功
        await _syncSvc.MarkSyncedAsync("API002", "ERP-VOU-002");
        var synced = await _context.APInvoices.FindAsync("API002");
        Assert.Equal(ErpSyncStatus.Synced, synced!.ErpSyncStatus);
        Assert.Equal("ERP-VOU-002",        synced.ErpVoucherNo);
    }

    // ── EP Invalid ───────────────────────────────────────────────────────────

    /// <summary>
    /// EP Invalid: ErpRetryCount 達最大重試次數後繼續失敗 → 狀態鎖定 Failed，
    /// ErpRetryCount > MaxErpRetryCount，IsLocked = true。
    /// §5.4 EP Invalid 案例。
    /// </summary>
    [Fact]
    public async Task MarkFailed_ExceedsMaxRetry_LockedFailed()
    {
        SeedInvoice("API003", totalAmount: 2000m);

        // 模擬 MaxErpRetryCount 次失敗
        for (int i = 0; i < ErpSyncService.MaxErpRetryCount; i++)
            await _syncSvc.MarkFailedAsync("API003", $"第 {i + 1} 次失敗");

        var locked = await _context.APInvoices.FindAsync("API003");
        Assert.Equal(ErpSyncStatus.Failed,             locked!.ErpSyncStatus);
        Assert.Equal(ErpSyncService.MaxErpRetryCount,  locked.ErpRetryCount);
        Assert.True(_syncSvc.IsLocked(locked), "達最大重試次數後應判定為鎖定狀態");
    }

    // ── BVA ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// BVA 進位尾差臨界值: MES 金額 = ERP 金額 ± 0.01（在容忍範圍內）→ 允許同步。
    /// §5.4 BVA 第 1 案例。
    /// </summary>
    [Theory]
    [InlineData(5000.00, 5000.01)]   // ERP 多 0.01（進位）
    [InlineData(5000.00, 4999.99)]   // ERP 少 0.01（捨去）
    public async Task MarkSynced_AmountDiffWithinTolerance_Synced(
        decimal mesAmount, decimal erpAmount)
    {
        var invoiceId = $"API-TOL-{erpAmount}";
        SeedInvoice(invoiceId, totalAmount: mesAmount);

        await _syncSvc.MarkSyncedAsync(invoiceId, "ERP-VOU-TOL", erpVoucherAmount: erpAmount);

        var updated = await _context.APInvoices.FindAsync(invoiceId);
        Assert.Equal(ErpSyncStatus.Synced, updated!.ErpSyncStatus);
    }

    /// <summary>
    /// BVA 進位尾差臨界值: MES 金額 = ERP 金額 ± 0.02（超出容忍）→ 拋出例外，不同步。
    /// </summary>
    [Fact]
    public async Task MarkSynced_AmountDiffExceedsTolerance_ThrowsException()
    {
        SeedInvoice("API005", totalAmount: 5000m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _syncSvc.MarkSyncedAsync("API005", "ERP-VOU-005", erpVoucherAmount: 5000.02m));

        PzAssert.Message("Fin_ErpAmountMismatchFmt", ex.Message);
        var inv = await _context.APInvoices.FindAsync("API005");
        Assert.Equal(ErpSyncStatus.Pending, inv!.ErpSyncStatus);   // 不應被改為 Synced
    }

    /// <summary>§9 P-7：audit_amount_tolerance 系統參數覆寫尾差容忍——差異 0.02 在參數 0.05 下允許同步。</summary>
    [Fact]
    public async Task MarkSynced_AmountToleranceParamOverride_Applied()
    {
        var mockParams = new Moq.Mock<SmartFactory.MES.Services.ISystemParamService>();
        mockParams.Setup(p => p.GetAsync(SmartFactory.MES.Models.ProjectZSystemParamKeys.AuditAmountTolerance))
            .ReturnsAsync("0.05");
        var svc = new ErpSyncService(_context, mockParams.Object);
        SeedInvoice("API105", totalAmount: 5000m);

        await svc.MarkSyncedAsync("API105", "ERP-VOU-105", erpVoucherAmount: 5000.02m);

        Assert.Equal(ErpSyncStatus.Synced, (await _context.APInvoices.FindAsync("API105"))!.ErpSyncStatus);
    }

    /// <summary>
    /// BVA 重試次數臨界點: ErpRetryCount = MaxErpRetryCount（第 3 次恰好達上限）。
    /// 第 MaxErpRetryCount 次失敗後 IsLocked = true；
    /// 若此時仍能重試成功（MarkSynced），仍可轉 Synced。
    /// §5.4 BVA 第 2 案例。
    /// </summary>
    [Fact]
    public async Task MarkFailed_AtExactMaxRetry_IsLockedTrue_CanStillSyncManually()
    {
        SeedInvoice("API006", totalAmount: 1000m);

        // 精確達到上限
        for (int i = 0; i < ErpSyncService.MaxErpRetryCount; i++)
            await _syncSvc.MarkFailedAsync("API006", "連線失敗");

        var locked = await _context.APInvoices.FindAsync("API006");
        Assert.Equal(ErpSyncService.MaxErpRetryCount, locked!.ErpRetryCount);
        Assert.True(_syncSvc.IsLocked(locked));

        // 人工介入仍可強制 MarkSynced
        await _syncSvc.MarkSyncedAsync("API006", "ERP-MANUAL");
        var synced = await _context.APInvoices.FindAsync("API006");
        Assert.Equal(ErpSyncStatus.Synced, synced!.ErpSyncStatus);
    }

    // ── 延遲稽核查詢 ─────────────────────────────────────────────────────────

    /// <summary>
    /// GetPendingOverdueAsync: 超過 24 小時未同步的 Pending 單據應被查出（§2.2, §3.5 等效）。
    /// </summary>
    [Fact]
    public async Task GetPendingOverdue_ReturnsInvoicesNotSyncedWithin24Hours()
    {
        // 超過 24 小時的 Pending 單據
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "API-OLD",
            SupplierCode = SuppCode,
            InvoiceDate  = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            DueDate      = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(28)),
            TotalAmount  = 1000m, AmountLocal = 1000m,
            Status       = APInvoiceStatus.Open,
            ErpSyncStatus = ErpSyncStatus.Pending,
            PlantId      = PlantId,
            CreatedAt    = DateTime.UtcNow.AddHours(-30),   // 30 小時前
        });
        // 24 小時內的新單據（不應被查出）
        _context.APInvoices.Add(new APInvoice
        {
            APInvoiceId  = "API-NEW",
            SupplierCode = SuppCode,
            InvoiceDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate      = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            TotalAmount  = 500m, AmountLocal = 500m,
            Status       = APInvoiceStatus.Open,
            ErpSyncStatus = ErpSyncStatus.Pending,
            PlantId      = PlantId,
            CreatedAt    = DateTime.UtcNow.AddHours(-2),    // 2 小時前
        });
        _context.SaveChanges();

        var overdue = await _syncSvc.GetPendingOverdueAsync(overdueHours: 24);

        Assert.Single(overdue);
        Assert.Equal("API-OLD", overdue[0].APInvoiceId);
    }

    // ── §9 P-3（D3 最小範圍）：ARInvoice 泛型拋轉 ────────────────────────────

    /// <summary>泛型路徑：MarkSyncedAsync&lt;ARInvoice&gt; Pending → Synced（含尾差比對）。</summary>
    [Fact]
    public async Task MarkSyncedGeneric_ArInvoice_PendingToSynced()
    {
        SeedArInvoice("ARI001", totalAmount: 8000m);

        await _syncSvc.MarkSyncedAsync<ARInvoice>("ARI001", "ERP-VOU-AR1", erpVoucherAmount: 8000.01m);

        var updated = await _context.ARInvoices.FindAsync("ARI001");
        Assert.Equal(ErpSyncStatus.Synced, updated!.ErpSyncStatus);
        Assert.Equal("ERP-VOU-AR1",        updated.ErpVoucherNo);
        Assert.NotNull(updated.ErpSyncTime);
    }

    [Fact]
    public async Task MarkSyncedGeneric_ArInvoice_AmountExceedsTolerance_Throws()
    {
        SeedArInvoice("ARI002", totalAmount: 8000m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _syncSvc.MarkSyncedAsync<ARInvoice>("ARI002", "ERP-VOU-AR2", erpVoucherAmount: 8000.02m));

        Assert.Equal(ErpSyncStatus.Pending, (await _context.ARInvoices.FindAsync("ARI002"))!.ErpSyncStatus);
    }

    [Fact]
    public async Task GetPendingOverdueAr_ReturnsOnlyOverduePending()
    {
        SeedArInvoice("AR-OLD", totalAmount: 1000m, createdAt: DateTime.UtcNow.AddHours(-30));
        SeedArInvoice("AR-NEW", totalAmount: 500m,  createdAt: DateTime.UtcNow.AddHours(-2));

        var overdue = await _syncSvc.GetPendingOverdueArAsync(overdueHours: 24);

        Assert.Single(overdue);
        Assert.Equal("AR-OLD", overdue[0].ARInvoiceId);
    }

    // ── 輔助方法 ─────────────────────────────────────────────────────────────

    private ARInvoice SeedArInvoice(string invoiceId, decimal totalAmount, DateTime? createdAt = null)
    {
        var inv = new ARInvoice
        {
            ARInvoiceId  = invoiceId,
            CustomerCode = "CUS001",
            InvoiceDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate      = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            TotalAmount  = totalAmount,
            AmountLocal  = totalAmount,
            Status       = ARInvoiceStatus.Open,
            ErpSyncStatus = ErpSyncStatus.Pending,
            PlantId      = PlantId,
            CreatedAt    = createdAt ?? DateTime.UtcNow,
        };
        _context.ARInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }

    private APInvoice SeedInvoice(string invoiceId, decimal totalAmount)
    {
        var inv = new APInvoice
        {
            APInvoiceId   = invoiceId,
            SupplierCode  = SuppCode,
            InvoiceDate   = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate       = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            TotalAmount   = totalAmount,
            AmountLocal   = totalAmount,
            Status        = APInvoiceStatus.Open,
            ErpSyncStatus = ErpSyncStatus.Pending,
            PlantId       = PlantId,
        };
        _context.APInvoices.Add(inv);
        _context.SaveChanges();
        return inv;
    }
}
