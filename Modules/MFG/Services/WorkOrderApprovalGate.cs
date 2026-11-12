using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>開工時工單層的擋下原因。</summary>
    public enum WorkOrderStartBlock
    {
        None,
        /// <summary>工單不是已投產／生產中（例：撤回或駁回後退回已規劃）。</summary>
        NotReleased,
        /// <summary>工單送審中或被退回。</summary>
        NotApproved,
    }

    /// <summary>
    /// 投產前審核（mfg-domain.md §18.2）：工單送審中或被退回時不得排程、不得開工；
    /// 工單不在已投產／生產中時也不得開工。
    ///
    /// 投產服務先寫 Status=Released 再送審，而排程與開工原本只看 Status，送審中的工單照樣被排、
    /// 照樣能開工（2026-10-20 探針實證）。核准狀態只擋**明確未核准**的兩種：Draft 是預設值，
    /// §18.2 之前投產的舊工單與無簽核設定的資料都是 Draft，一律當成可生產。
    /// 開工原本也完全不看工單狀態——撤回到已規劃的工單，先前已排程的派工照樣能開工（同日實證）。
    /// 簡化模式的自動投產直接核准，不受影響。桌機開工、手機開工、排程三處共用本判定。
    /// </summary>
    public static class WorkOrderApprovalGate
    {
        public static bool BlocksProduction(DocumentApprovalStatus approval) =>
            approval is DocumentApprovalStatus.PendingApproval or DocumentApprovalStatus.Rejected;

        public static async Task<WorkOrderStartBlock> CheckStartAsync(
            ApplicationDbContext db, string workOrderId, CancellationToken ct = default)
        {
            var wo = await db.WorkOrders.AsNoTracking()
                .Where(w => w.WorkOrderId == workOrderId)
                .Select(w => new { w.Status, w.ApprovalStatus })
                .FirstOrDefaultAsync(ct);
            if (wo == null || wo.Status is not (WorkOrderStatus.Released or WorkOrderStatus.InProgress))
                return WorkOrderStartBlock.NotReleased;
            return BlocksProduction(wo.ApprovalStatus) ? WorkOrderStartBlock.NotApproved : WorkOrderStartBlock.None;
        }

        /// <summary>
        /// 工單退回已規劃（撤回／駁回／退回修改）時，已排程未開工的派工退回待排程，
        /// 不再佔著甘特圖與機台產能；做法同機台停用（<see cref="DispatchHold.ApplyMachineDownAsync"/>）。
        /// 準備中以後的狀態留給開工閘擋。不呼叫 SaveChanges，由呼叫端提交。
        /// </summary>
        public static async Task<int> UnscheduleAsync(
            ApplicationDbContext db, string workOrderId, DateTime now, CancellationToken ct = default)
        {
            var scheduled = await db.Set<Dispatch>()
                .Where(d => d.WorkOrderId == workOrderId && d.Status == DispatchStatus.Scheduled)
                .ToListAsync(ct);
            foreach (var d in scheduled)
            {
                d.Status    = DispatchStatus.Pending;
                d.UpdatedAt = now;
            }
            return scheduled.Count;
        }
    }
}
