using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.MFG.Services;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class MonthEndService : IMonthEndService
    {
        private readonly ApplicationDbContext _db;
        private readonly IAccountingPeriodService _periods;
        private readonly IWorkOrderCostFinalizationService? _woCostFinalizer;
        private readonly IPeriodEndAdjustmentService? _adjustments;

        public MonthEndService(
            ApplicationDbContext db, IAccountingPeriodService periods,
            IWorkOrderCostFinalizationService? woCostFinalizer = null,
            IPeriodEndAdjustmentService? adjustments = null)
        {
            _db      = db;
            _periods = periods;
            _woCostFinalizer = woCostFinalizer;
            _adjustments = adjustments;
        }

        private static readonly (int StepNo, string StepName)[] Steps =
        [
            (1,  "確認所有進貨/出貨已過帳"),
            (2,  "盤點差異調整完成"),
            (3,  "完成 WIP 評估快照"),
            (4,  "計算工單實際成本"),
            (5,  "更新 AVCO 均價"),
            (6,  "應收/應付期末匯率重評（IAS 21）"),
            (7,  "IAS 2 NRV 存貨減損評估"),
            (8,  "封鎖庫存異動（PeriodStatus = Closing）"),
            (9,  "產生期末調整分錄"),
            (10, "關帳（PeriodStatus = Closed）"),
        ];

        public async Task<MonthEndChecklist> InitChecklistAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            var checklistId = $"{periodId}-{plantId}";
            var existing = await _db.MonthEndChecklists
                .Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.ChecklistId == checklistId, ct);
            if (existing != null) return existing;

            var checklist = new MonthEndChecklist
            {
                ChecklistId = checklistId,
                PeriodId    = periodId,
                PlantId     = plantId,
                CreatedAt   = DateTime.UtcNow,
                UpdatedAt   = DateTime.UtcNow,
            };

            foreach (var (stepNo, stepName) in Steps)
            {
                checklist.Items.Add(new ChecklistItem
                {
                    ChecklistId = checklistId,
                    StepNo      = stepNo,
                    StepName    = stepName,
                    Status      = ChecklistItemStatus.Pending,
                });
            }

            _db.MonthEndChecklists.Add(checklist);
            await _db.SaveChangesAsync(ct);
            return checklist;
        }

        public async Task CompleteStepAsync(
            string checklistId, int stepNo, string userId, string? notes = null,
            CancellationToken ct = default)
        {
            var item = await _db.ChecklistItems
                .FirstOrDefaultAsync(i => i.ChecklistId == checklistId && i.StepNo == stepNo, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_StepNotFoundFmt", stepNo));

            await AssertPreviousStepsDoneAsync(checklistId, stepNo, ct);

            // 步驟 9（產生期末調整分錄）→ 外幣重評／NRV／ECL 入總帳。原本這一步只是打勾：
            // 步驟 6、7 算出來的結果只寫評估表，關帳關得了、總帳卻沒有這些調整。
            // 必須在改步驟狀態**之前**做：分錄服務在同一個 DbContext 上 SaveChanges，
            // 先標完成的話，入帳中途失敗時「已完成」會跟著前面成功的分錄一起存進去。
            // 中途失敗留下的部分分錄不怕重做——入帳服務只補差額。
            //
            // 步驟 10 關帳前若步驟 9 已完成，再補一次差額：NRV／ECL 各有自己的畫面可以重跑評估，
            // 步驟 9 之後重跑的話評估表變了、總帳沒跟上，一關帳就永遠對不起來。
            // 步驟 9 被「略過」就尊重略過，不在這裡偷偷入帳。
            var postAdjustments = stepNo == 9
                || (stepNo == 10 && await _db.ChecklistItems.AnyAsync(
                       i => i.ChecklistId == checklistId && i.StepNo == 9 && i.Status == ChecklistItemStatus.Completed, ct));
            if (postAdjustments && _adjustments != null)
            {
                var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Act_ChecklistNotFound"));
                var r = await _adjustments.PostAsync(checklist.PeriodId, checklist.PlantId!, ct);
                var summary = string.Join("; ", r.Notes);
                var combined = string.IsNullOrEmpty(notes) ? summary : $"{notes}（{summary}）";
                notes = combined.Length > 500 ? combined[..500] : combined;   // 欄位上限 500
            }

            item.Status      = ChecklistItemStatus.Completed;
            item.Notes       = notes;
            item.CompletedAt = DateTime.UtcNow;
            item.CompletedBy = userId;

            // 步驟 4（計算工單實際成本）→ 將該期 Preliminary 工單成本鎖定為 Final（§16.3）
            if (stepNo == 4 && _woCostFinalizer != null)
            {
                var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Act_ChecklistNotFound"));
                var finalized = await _woCostFinalizer.FinalizeAsync(checklist.PeriodId, checklist.PlantId, ct);
                var lockedNote = MonthEndNote.Token("Note_WoFinalized", finalized);
                item.Notes = string.IsNullOrEmpty(notes) ? lockedNote : $"{notes}（{lockedNote}）";
            }

            // 步驟 8（封鎖）→ 自動觸發 StartClosing
            if (stepNo == 8)
            {
                var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Act_ChecklistNotFound"));
                await _periods.StartClosingAsync(checklist.PeriodId, checklist.PlantId!, userId, ct);
            }

            // 步驟 10（關帳）→ 自動觸發 ClosePeriod
            if (stepNo == 10)
            {
                var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct)
                    ?? throw new InvalidOperationException(PzText.Get("Act_ChecklistNotFound"));
                await _periods.ClosePeriodAsync(checklist.PeriodId, checklist.PlantId!, userId, ct);
                checklist.IsCompleted  = true;
                checklist.CompletedAt  = DateTime.UtcNow;
                checklist.CompletedBy  = userId;
                checklist.UpdatedAt    = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
        }

        public async Task CancelClosingAsync(
            string checklistId, string userId, CancellationToken ct = default)
        {
            var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_ChecklistNotFound"));
            await CancelClosingByPeriodAsync(checklist.PeriodId, checklist.PlantId!, userId, ct);
        }

        public async Task CancelClosingByPeriodAsync(
            string periodId, string plantId, string userId, CancellationToken ct = default)
        {
            await _periods.CancelClosingAsync(periodId, plantId, userId, ct);

            // 清單是選配的：從「會計期間」畫面直接開始月結時根本沒有清單（W4-F8）。
            // 清單 Id 的組法與 InitChecklistAsync 一致。
            var checklistId = $"{periodId}-{plantId}";

            // 步驟 8 必須跟著退回未完成，否則畫面顯示「已封鎖」而期間其實沒鎖——
            // 清單是操作人員唯一看得到的進度，不能與期間狀態各說各話。
            // 步驟 9（期末調整分錄）刻意不動：那些分錄若已經開出去就是既成事實。
            var step8 = await _db.ChecklistItems
                .FirstOrDefaultAsync(i => i.ChecklistId == checklistId && i.StepNo == 8, ct);
            if (step8 != null)
            {
                step8.Status      = ChecklistItemStatus.Pending;
                step8.Notes       = null;
                step8.CompletedAt = null;
                step8.CompletedBy = null;
            }

            var checklist = await _db.MonthEndChecklists.FindAsync(new object[] { checklistId }, ct);
            if (checklist != null) checklist.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
        }

        public async Task SkipStepAsync(
            string checklistId, int stepNo, string userId, string reason,
            CancellationToken ct = default)
        {
            var item = await _db.ChecklistItems
                .FirstOrDefaultAsync(i => i.ChecklistId == checklistId && i.StepNo == stepNo, ct)
                ?? throw new InvalidOperationException(PzText.Get("Act_StepNotFoundFmt", stepNo));

            await AssertPreviousStepsDoneAsync(checklistId, stepNo, ct);

            item.Status      = ChecklistItemStatus.Skipped;
            item.Notes       = MonthEndNote.Token("Note_Skipped", reason);
            item.CompletedAt = DateTime.UtcNow;
            item.CompletedBy = userId;
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// 月結步驟必須依序執行：第 N 步之前的每一步都要是 Completed 或 Skipped。
        ///
        /// 沒有這道守門的話，可以先打勾步驟 8（封鎖庫存異動，會把期間推進 Closing）
        /// 再回頭做步驟 4（計算工單實際成本，會鎖定成本並產生分錄）——順序顛倒的分錄
        /// 落在已封鎖的期間裡，而清單本身看起來一切正常。這裡擋在寫入之前，
        /// 因為步驟 4／8／10 各自帶副作用（鎖成本、StartClosing、ClosePeriod），
        /// 一旦寫下去就不是改個狀態欄位能還原的。
        ///
        /// 「Skipped 也算過」是刻意的：跳過本來就是「這一步本期不適用」的正式出口，
        /// 要求它先變成 Completed 等於逼人假報。
        /// </summary>
        private async Task AssertPreviousStepsDoneAsync(
            string checklistId, int stepNo, CancellationToken ct)
        {
            var blocking = await _db.ChecklistItems
                .Where(i => i.ChecklistId == checklistId
                         && i.StepNo < stepNo
                         && i.Status != ChecklistItemStatus.Completed
                         && i.Status != ChecklistItemStatus.Skipped)
                .OrderBy(i => i.StepNo)
                .Select(i => i.StepNo)
                .ToListAsync(ct);

            if (blocking.Count == 0) return;

            throw new InvalidOperationException(
                PzText.Get("Act_StepsMustBeSequentialFmt", stepNo, string.Join(PzText.Get("Common_ListSeparator"), blocking)));
        }

        public async Task<MonthEndChecklist?> GetChecklistAsync(
            string periodId, string plantId, CancellationToken ct = default)
        {
            var checklistId = $"{periodId}-{plantId}";
            return await _db.MonthEndChecklists
                .Include(c => c.Items.OrderBy(i => i.StepNo))
                .FirstOrDefaultAsync(c => c.ChecklistId == checklistId, ct);
        }
    }
}
