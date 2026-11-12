using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Tests.Shared;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;

namespace SmartFactory.MES.Tests.ACT;

/// <summary>
/// MonthEndService 業務規則測試。
/// 覆蓋清單初始化（10 步驟）、冪等、步驟完成/跳過、
/// 步驟 8 自動觸發 StartClosing、步驟 10 自動觸發 ClosePeriod。
/// </summary>
public class MonthEndServiceTests : TestBase
{
    private const string PlantId  = "P001";
    private const string PeriodId = "202606";
    private const string UserId   = "finance01";

    private readonly MonthEndService         _monthEnd;
    private readonly AccountingPeriodService _periods;

    public MonthEndServiceTests()
    {
        _context.CurrentPlantId = PlantId;
        _periods  = new AccountingPeriodService(_context);
        _monthEnd = new MonthEndService(_context, _periods);

        SeedPeriod();
    }

    // ── CancelClosingByPeriodAsync（W4-F8）────────────────────────────────

    /// <summary>
    /// 期間可以不經月結清單就進 Closing——「會計期間」畫面的〈開始月結〉直接呼叫
    /// StartClosingAsync。走那條路進來時沒有清單，而 CompleteStep(8) 又會因為
    /// 「狀態為 Closing，無法開始月結」而拋錯，於是原本綁在「步驟 8 已完成」的
    /// 〈取消月結〉永遠不會出現：期間卡在 Closing，一般過帳全被擋，
    /// 而唯一的出口〈關帳〉要稽核全過——稽核異常又多半得靠過帳去修。
    /// 2026-09-06 在 P2 廠 202609 實測確認：只剩改資料庫一條路。
    /// </summary>
    [Fact]
    public async Task CancelClosingByPeriod_WithNoChecklistAtAll_StillReleasesThePeriod()
    {
        await _periods.StartClosingAsync(PeriodId, PlantId, UserId);
        Assert.Empty(_context.MonthEndChecklists);

        await _monthEnd.CancelClosingByPeriodAsync(PeriodId, PlantId, UserId);

        var p = await _periods.GetByIdAsync(PeriodId, PlantId);
        Assert.Equal(PeriodStatus.Open, p!.Status);
    }

    /// <summary>
    /// 從別的畫面取消月結時，清單若存在也必須跟著退回——清單是操作人員唯一看得到的
    /// 進度，顯示「已封鎖」而期間其實沒鎖，比卡住更難查。
    /// </summary>
    [Fact]
    public async Task CancelClosingByPeriod_WithChecklist_AlsoResetsStep8()
    {
        var checklist = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        for (var step = 1; step <= 7; step++)
            await _monthEnd.SkipStepAsync(checklist.ChecklistId, step, UserId, "不適用");
        await _monthEnd.CompleteStepAsync(checklist.ChecklistId, 8, UserId);

        var before = _context.ChecklistItems.Single(i => i.ChecklistId == checklist.ChecklistId && i.StepNo == 8);
        Assert.Equal(ChecklistItemStatus.Completed, before.Status);

        await _monthEnd.CancelClosingByPeriodAsync(PeriodId, PlantId, UserId);

        var after = _context.ChecklistItems.Single(i => i.ChecklistId == checklist.ChecklistId && i.StepNo == 8);
        Assert.Equal(ChecklistItemStatus.Pending, after.Status);
        Assert.Null(after.CompletedAt);
        Assert.Equal(PeriodStatus.Open, (await _periods.GetByIdAsync(PeriodId, PlantId))!.Status);
    }

    /// <summary>曾關過帳的期間取消月結要退回 Reopened，不能被抹成 Open（稽核軌跡）。</summary>
    [Fact]
    public async Task CancelClosingByPeriod_OnPreviouslyClosedPeriod_ReturnsToReopened()
    {
        await _periods.StartClosingAsync(PeriodId, PlantId, UserId);
        await _periods.ClosePeriodAsync(PeriodId, PlantId, UserId);
        await _periods.ReopenPeriodAsync(PeriodId, PlantId, UserId);
        await _periods.StartClosingAsync(PeriodId, PlantId, UserId);

        await _monthEnd.CancelClosingByPeriodAsync(PeriodId, PlantId, UserId);

        Assert.Equal(PeriodStatus.Reopened, (await _periods.GetByIdAsync(PeriodId, PlantId))!.Status);
    }

    // ── InitChecklistAsync ────────────────────────────────────────────────

    [Fact]
    public async Task InitChecklist_Creates10Steps_AllPending()
    {
        var checklist = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        Assert.Equal($"{PeriodId}-{PlantId}", checklist.ChecklistId);
        Assert.Equal(10, checklist.Items.Count);
        Assert.All(checklist.Items, i => Assert.Equal(ChecklistItemStatus.Pending, i.Status));
    }

    [Fact]
    public async Task InitChecklist_Steps_SequentialFrom1To10()
    {
        var checklist = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        var stepNos = checklist.Items.Select(i => i.StepNo).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, 10).ToList(), stepNos);
    }

    [Fact]
    public async Task InitChecklist_Idempotent_ReturnsSameChecklist()
    {
        var first  = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        var second = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        Assert.Equal(first.ChecklistId, second.ChecklistId);
        Assert.Equal(1, _context.MonthEndChecklists.Count(c => c.PeriodId == PeriodId));
    }

    // ── CompleteStepAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CompleteStep_NormalStep_StatusBecomeCompleted()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 1, UserId, "確認完成");

        var item = _context.ChecklistItems
            .First(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 1);
        Assert.Equal(ChecklistItemStatus.Completed, item.Status);
        Assert.Equal("確認完成", item.Notes);
        Assert.Equal(UserId,    item.CompletedBy);
        Assert.NotNull(item.CompletedAt);
    }

    [Fact]
    public async Task CompleteStep8_TriggersStartClosing_PeriodBecomesClosing()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        await CompleteThroughAsync(cl.ChecklistId, 7);

        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 8, UserId);

        var period = await _context.AccountingPeriods.FindAsync(PeriodId, PlantId);
        Assert.Equal(PeriodStatus.Closing, period!.Status);
    }

    [Fact]
    public async Task CompleteStep10_TriggerClosePeriod_PeriodBecomesClosed()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        await CompleteThroughAsync(cl.ChecklistId, 7);

        // 步驟 8 必須先執行（讓 period 進入 Closing），才能執行步驟 10
        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 8, UserId);
        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 9,  UserId);
        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 10, UserId);

        var period = await _context.AccountingPeriods.FindAsync(PeriodId, PlantId);
        Assert.Equal(PeriodStatus.Closed, period!.Status);
    }

    [Fact]
    public async Task CompleteStep10_MarksChecklistCompleted()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        await CompleteThroughAsync(cl.ChecklistId, 9);

        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 10, UserId);

        var updated = await _context.MonthEndChecklists.FindAsync(cl.ChecklistId);
        Assert.True(updated!.IsCompleted);
        Assert.NotNull(updated.CompletedAt);
        Assert.Equal(UserId, updated.CompletedBy);
    }

    [Fact]
    public async Task CompleteStep_InvalidStepNo_Throws()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 99, UserId));
    }

    // ── SkipStepAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SkipStep_SetsSkippedStatus_AndReason()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        await CompleteThroughAsync(cl.ChecklistId, 1);

        await _monthEnd.SkipStepAsync(cl.ChecklistId, stepNo: 2, UserId, "本期無盤點差異");

        var item = _context.ChecklistItems
            .First(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 2);
        Assert.Equal(ChecklistItemStatus.Skipped, item.Status);
        Assert.Contains("本期無盤點差異", item.Notes);
    }

    // ── 步驟順序守門 ──────────────────────────────────────────────────────
    //
    // 這一組的存在理由：步驟 4／8／10 各自帶副作用（鎖定工單成本、StartClosing、ClosePeriod）。
    // 沒有守門時可以先打勾 8 把期間推進 Closing，再回頭做 4 去產生分錄——
    // 分錄落在已封鎖的期間裡，而清單畫面看起來一切正常。

    [Fact]
    public async Task CompleteStep_SkippingAhead_Throws_AndHasNoSideEffect()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 8, UserId));
        PzAssert.Message("Act_StepsMustBeSequentialFmt", ex.Message);

        // 守門必須擋在寫入之前：狀態沒動，期間也沒有被推進 Closing。
        var item = _context.ChecklistItems.First(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 8);
        Assert.Equal(ChecklistItemStatus.Pending, item.Status);
        var period = await _context.AccountingPeriods.FindAsync(PeriodId, PlantId);
        Assert.Equal(PeriodStatus.Open, period!.Status);
    }

    [Fact]
    public async Task SkipStep_SkippingAhead_Throws()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _monthEnd.SkipStepAsync(cl.ChecklistId, stepNo: 5, UserId, "不適用"));
    }

    /// <summary>跳過的步驟算「已處理」——它是「本期不適用」的正式出口，不該逼人假報成完成。</summary>
    [Fact]
    public async Task CompleteStep_PreviousStepSkipped_CountsAsDone()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);

        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 1, UserId);
        await _monthEnd.SkipStepAsync(cl.ChecklistId, stepNo: 2, UserId, "本期無盤點差異");
        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 3, UserId);

        var item = _context.ChecklistItems.First(i => i.ChecklistId == cl.ChecklistId && i.StepNo == 3);
        Assert.Equal(ChecklistItemStatus.Completed, item.Status);
    }

    /// <summary>錯誤訊息要把擋住的步驟號列出來，否則操作人員只知道被擋不知道回頭做哪一步。</summary>
    [Fact]
    public async Task CompleteStep_SkippingAhead_MessageNamesBlockingSteps()
    {
        var cl = await _monthEnd.InitChecklistAsync(PeriodId, PlantId);
        await _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 1, UserId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _monthEnd.CompleteStepAsync(cl.ChecklistId, stepNo: 4, UserId));

        // 只列未完成的 2、3，已完成的 1 不該列進去
        Assert.Equal(PzText.Get("Act_StepsMustBeSequentialFmt", 4, string.Join(PzText.Get("Common_ListSeparator"), 2, 3)), ex.Message);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────────

    /// <summary>把步驟 1..through 依序完成，讓測試能把焦點放在後面那一步。</summary>
    private async Task CompleteThroughAsync(string checklistId, int through)
    {
        for (var n = 1; n <= through; n++)
            await _monthEnd.CompleteStepAsync(checklistId, n, UserId);
    }

    private void SeedPeriod()
    {
        _context.AccountingPeriods.Add(new AccountingPeriod
        {
            PeriodId  = PeriodId,
            PlantId   = PlantId,
            Year      = 2026,
            Month     = 6,
            StartDate = new DateOnly(2026, 6, 1),
            EndDate   = new DateOnly(2026, 6, 30),
            Status    = PeriodStatus.Open,
        });
        _context.SaveChanges();
    }
}
