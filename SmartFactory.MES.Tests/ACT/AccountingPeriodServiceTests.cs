using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// AccountingPeriodService 業務規則測試。
/// 覆蓋期間查詢、過帳允許性驗證、狀態機轉換（Open→Closing→Closed→Reopened）
/// 與自動建立下一期間的邊界邏輯。
/// </summary>
public class AccountingPeriodServiceTests : TestBase
{
    private const string PlantId    = "P001";
    private const string OtherPlant = "P002";

    private readonly AccountingPeriodService _svc;

    public AccountingPeriodServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _svc = new AccountingPeriodService(_context);
    }

    // ── GetByDateAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetByDate_DateInRange_ReturnsPeriod()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _svc.GetByDateAsync(new DateOnly(2026, 1, 15), PlantId);

        Assert.NotNull(result);
        Assert.Equal("202601", result.PeriodId);
    }

    [Fact]
    public async Task GetByDate_DateOutsideRange_ReturnsNull()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _svc.GetByDateAsync(new DateOnly(2026, 2, 1), PlantId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByDate_ExactStartDate_ReturnsPeriod()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _svc.GetByDateAsync(new DateOnly(2026, 1, 1), PlantId);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetByDate_ExactEndDate_ReturnsPeriod()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = await _svc.GetByDateAsync(new DateOnly(2026, 1, 31), PlantId);

        Assert.NotNull(result);
    }

    // ── AssertCanPostAsync ────────────────────────────────────────────────

    [Fact]
    public async Task AssertCanPost_NoPeriodConfigured_DoesNotThrow()
    {
        // 寬鬆模式：無期間設定時允許任意過帳
        await _svc.AssertCanPostAsync(new DateOnly(2026, 6, 1), PlantId);
    }

    [Fact]
    public async Task AssertCanPost_OpenPeriod_DoesNotThrow()
    {
        SeedPeriod("202606", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            status: PeriodStatus.Open);

        await _svc.AssertCanPostAsync(new DateOnly(2026, 6, 15), PlantId);
    }

    [Fact]
    public async Task AssertCanPost_ClosedPeriod_Throws()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.AssertCanPostAsync(new DateOnly(2026, 1, 10), PlantId));

        PzAssert.Message("Act_PeriodClosedNoPostFmt", ex.Message);
    }

    [Fact]
    public async Task AssertCanPost_ClosingPeriod_Throws()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closing);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.AssertCanPostAsync(new DateOnly(2026, 1, 10), PlantId));

        PzAssert.Message("Act_PeriodClosingNoPostFmt", ex.Message);
    }

    // ── 狀態機轉換 ────────────────────────────────────────────────────────

    [Fact]
    public async Task StartClosing_OpenPeriod_BecomesClosing()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Open);

        await _svc.StartClosingAsync("202601", PlantId, "admin");

        var p = await _context.AccountingPeriods.FindAsync("202601", PlantId);
        Assert.Equal(PeriodStatus.Closing, p!.Status);
    }

    [Fact]
    public async Task StartClosing_AlreadyClosed_Throws()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.StartClosingAsync("202601", PlantId, "admin"));
    }

    [Fact]
    public async Task ClosePeriod_ClosingStatus_BecomesClosed_RecordsUser()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closing);

        await _svc.ClosePeriodAsync("202601", PlantId, "finance_mgr");

        var p = await _context.AccountingPeriods.FindAsync("202601", PlantId);
        Assert.Equal(PeriodStatus.Closed, p!.Status);
        Assert.Equal("finance_mgr",       p.ClosedBy);
        Assert.NotNull(p.ClosedAt);
    }

    [Fact]
    public async Task ClosePeriod_NotClosingStatus_Throws()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Open);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ClosePeriodAsync("202601", PlantId, "admin"));
    }

    [Fact]
    public async Task ReopenPeriod_ClosedStatus_BecomesReopened()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closed);

        await _svc.ReopenPeriodAsync("202601", PlantId, "admin");

        var p = await _context.AccountingPeriods.FindAsync("202601", PlantId);
        Assert.Equal(PeriodStatus.Reopened, p!.Status);
    }

    [Fact]
    public async Task ReopenPeriod_OpenStatus_Throws()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Open);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ReopenPeriodAsync("202601", PlantId, "admin"));
    }

    // ── CreateNextPeriodAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CreateNextPeriod_NoPrior_CreatesCurrentYearMonth()
    {
        var result = await _svc.CreateNextPeriodAsync(PlantId);

        var now = DateTime.UtcNow;
        Assert.Equal(now.Year,  result.Year);
        Assert.Equal(now.Month, result.Month);
        Assert.Equal(new DateOnly(now.Year, now.Month, 1), result.StartDate);
    }

    [Fact]
    public async Task CreateNextPeriod_ExistingPeriod_CreatesNextMonth()
    {
        SeedPeriod("202605", new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));

        var result = await _svc.CreateNextPeriodAsync(PlantId);

        Assert.Equal("202606",                    result.PeriodId);
        Assert.Equal(new DateOnly(2026, 6, 1),    result.StartDate);
        Assert.Equal(new DateOnly(2026, 6, 30),   result.EndDate);
    }

    [Fact]
    public async Task CreateNextPeriod_December_CreatesJanuaryNextYear()
    {
        // BVA：12 月後應自動進入下一年 1 月
        SeedPeriod("202612", new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));

        var result = await _svc.CreateNextPeriodAsync(PlantId);

        Assert.Equal("202701",                    result.PeriodId);
        Assert.Equal(2027,                        result.Year);
        Assert.Equal(1,                           result.Month);
        Assert.Equal(new DateOnly(2027, 1, 1),    result.StartDate);
        Assert.Equal(new DateOnly(2027, 1, 31),   result.EndDate);
    }

    // ── 廠別隔離（manual-test-plan §12.4 #2／#3）──────────────────────────
    //
    // 期間原本是全域單列：甲廠走完月結清單，乙廠就再也過不了帳，而乙廠自己的清單
    // 一步都沒動。月結清單本來就是每廠一份，期間卻是全域，兩者從一開始就對不起來。

    [Fact]
    public async Task AssertCanPost_OtherPlantClosed_DoesNotBlockThisPlant()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closed, plantId: OtherPlant);

        // 不拋例外即通過：別廠關帳與本廠無關
        await _svc.AssertCanPostAsync(new DateOnly(2026, 1, 10), PlantId);
    }

    [Fact]
    public async Task AssertCanPost_UsesGivenPlant_NotCurrentPlantOfTheContext()
    {
        // 操作者切在 PlantId，但要過帳的單據屬於 OtherPlant——跨廠調撥收貨就是這個情境。
        // 若實作偷懶用 CurrentPlantId（或依賴全域過濾器），這裡會查到本廠的 Open 而放行。
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closed, plantId: OtherPlant);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.AssertCanPostAsync(new DateOnly(2026, 1, 10), OtherPlant));
        Assert.Contains(OtherPlant, ex.Message);
    }

    [Fact]
    public async Task ClosePeriod_OnlyAffectsOwnPlant()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Closing);
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            status: PeriodStatus.Open, plantId: OtherPlant);

        await _svc.ClosePeriodAsync("202601", PlantId, "finance_mgr");

        Assert.Equal(PeriodStatus.Closed, await StatusOfAsync("202601", PlantId));
        Assert.Equal(PeriodStatus.Open,   await StatusOfAsync("202601", OtherPlant));
    }

    [Fact]
    public async Task GetById_OtherPlantsPeriod_ReturnsNull()
    {
        SeedPeriod("202601", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            plantId: OtherPlant);

        Assert.Null(await _svc.GetByIdAsync("202601", PlantId));
        Assert.NotNull(await _svc.GetByIdAsync("202601", OtherPlant));
    }

    /// <summary>各廠的月結進度可以不同，「下一期」要接的是本廠最後一期。</summary>
    [Fact]
    public async Task CreateNextPeriod_ContinuesOwnPlantsLastPeriod_NotTheCompanyWide()
    {
        SeedPeriod("202605", new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        SeedPeriod("202612", new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31),
            plantId: OtherPlant);   // 別廠已經走到 12 月

        var result = await _svc.CreateNextPeriodAsync(PlantId);

        Assert.Equal("202606", result.PeriodId);
        Assert.Equal(PlantId, result.PlantId);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    private async Task<PeriodStatus> StatusOfAsync(string periodId, string plantId)
        => (await _svc.GetByIdAsync(periodId, plantId))!.Status;


    private void SeedPeriod(string periodId, DateOnly start, DateOnly end,
        PeriodStatus status = PeriodStatus.Open, string? plantId = null)
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId  = periodId,
            PlantId   = plantId ?? PlantId,
            Year      = start.Year,
            Month     = start.Month,
            StartDate = start,
            EndDate   = end,
            Status    = status,
        });
        _context.SaveChanges();
    }
}
