using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單「完工量」的單一定義：<b>最後一道工序</b>的合格完工量（該工序所有派工——含拆單——的 QtyDone 合計）。
    ///
    /// <para>原本三處（完工入庫服務、入庫確認面板、工單進度報表）各自寫成「Σ 該工單全部派工的 QtyDone」。
    /// 單一工序的工單看不出差別；有多道工序時每一道都會報工，於是 3 道工序、各做 100 的工單
    /// 算成完工 300（2026-10-05 回報：工單進度報表完工量是生產回報的加總）。完工入庫用的是同一個數字，
    /// 所以成品會入庫 300、單位成本被攤成三分之一、後沖用料也放大三倍——那一半沒有人回報，因為帳都平。</para>
    ///
    /// <para>最後一道工序以工單工序表（WorkOrderOperations）的最大序號為準，而不是「派工裡最大的序號」：
    /// 最後一道還沒派工時完工量就是 0，不能退而求其次拿前一道的量。</para>
    /// </summary>
    public static class WorkOrderOutput
    {
        public static async Task<Dictionary<string, decimal>> CompletedQtyAsync(
            ApplicationDbContext db, IReadOnlyCollection<string> workOrderIds, CancellationToken ct = default)
        {
            if (workOrderIds.Count == 0) return new();

            var lastSeq = await db.WorkOrderOperations.AsNoTracking()
                .Where(o => workOrderIds.Contains(o.WorkOrderId))
                .GroupBy(o => o.WorkOrderId)
                .Select(g => new { WorkOrderId = g.Key, Seq = g.Max(o => o.OperationSeq) })
                .ToDictionaryAsync(x => x.WorkOrderId, x => x.Seq, ct);

            var dispatches = await db.Dispatches.AsNoTracking()
                .Where(d => workOrderIds.Contains(d.WorkOrderId))
                .Select(d => new { d.WorkOrderId, d.OperationSeq, d.QtyDone })
                .ToListAsync(ct);

            return workOrderIds.Distinct().ToDictionary(id => id, id =>
            {
                var mine = dispatches.Where(d => d.WorkOrderId == id).ToList();
                // 沒有工序表（舊資料／純派工）時退回派工上最大的序號
                var seq = lastSeq.TryGetValue(id, out var s) ? s : (mine.Count > 0 ? mine.Max(d => d.OperationSeq) : 0);
                return mine.Where(d => d.OperationSeq == seq).Sum(d => d.QtyDone);
            });
        }

        public static async Task<decimal> CompletedQtyAsync(ApplicationDbContext db, string workOrderId, CancellationToken ct = default)
            => (await CompletedQtyAsync(db, new[] { workOrderId }, ct)).GetValueOrDefault(workOrderId);
    }
}
