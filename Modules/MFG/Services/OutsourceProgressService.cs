using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 委外收回的「合格入庫量」回寫委外派工與工單（mfg-domain.md §19.3a，2026-10-12）。
    ///
    /// <para>原本委外收貨只入庫＋開應付，不碰派工與工單：委外派工永遠不會累計、不會結案，
    /// 整品委外工單收完貨還停在「生產中」，有人照流程按完工入庫就會重複入庫。</para>
    ///
    /// <para><b>什麼時候算</b>：貨<b>進到一般倉</b>的那一刻（＝品檢合格入庫，使用者 10/12 裁決）——
    /// 簡化模式確認收貨直接過帳到一般倉；非簡化模式免驗品上架到一般倉、或品檢合格自動由待驗倉放行。
    /// 待驗倉裡的量不算（還沒驗）。數量用庫存單位（StockQty），與派工量同一單位。</para>
    ///
    /// <para><b>結案</b>：派工累計 ≥ 派工量即結案；工單的派工都結束時——整品委外（WorkOrder.IsOutsource）直接到
    /// 「入庫完成」（成品已由委外收貨入庫、成本是加工費＋送外料，不得再走完工入庫）；其他工單照一般規則轉「完工」。</para>
    /// </summary>
    public interface IOutsourceProgressService
    {
        /// <summary>委外收貨單某一行有 <paramref name="stockQty"/> 進了一般倉。找不到對應的委外派工就不動作。</summary>
        Task ApplyGoodQtyAsync(string outsourceReceiptId, int lineNo, decimal stockQty, CancellationToken ct = default);
    }

    public class OutsourceProgressService : IOutsourceProgressService
    {
        private readonly ApplicationDbContext _db;
        public OutsourceProgressService(ApplicationDbContext db) => _db = db;

        public async Task ApplyGoodQtyAsync(string outsourceReceiptId, int lineNo, decimal stockQty, CancellationToken ct = default)
        {
            if (stockQty <= 0) return;

            // 收貨行 → 委外採購單行 → 來源工單＋工序（IgnoreQueryFilters：系統動作，廠別以單據本身為準）
            var src = await (
                from rl in _db.OutsourceReceiptLines.IgnoreQueryFilters()
                join r in _db.OutsourceReceipts.IgnoreQueryFilters() on rl.OutsourceReceiptId equals r.OutsourceReceiptId
                join ol in _db.OutsourcePurchaseOrderLines.IgnoreQueryFilters()
                    on new { r.OutsourcePurchaseOrderId, LineNo = rl.OpoLineNo ?? -1 }
                    equals new { ol.OutsourcePurchaseOrderId, ol.LineNo }
                where rl.OutsourceReceiptId == outsourceReceiptId && rl.LineNo == lineNo
                select new { ol.SourceWorkOrderId, ol.OperationSeq }).FirstOrDefaultAsync(ct);
            if (src == null || string.IsNullOrEmpty(src.SourceWorkOrderId)) return;

            // 同工序可能被拆成多張派工：依序先補還沒滿的，最後一張吃下超收
            var dispatches = await _db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.WorkOrderId == src.SourceWorkOrderId && d.IsOutsource
                         && (src.OperationSeq == 0 || d.OperationSeq == src.OperationSeq)
                         && d.Status != DispatchStatus.Cancelled)
                .OrderBy(d => d.DispatchId).ToListAsync(ct);
            if (dispatches.Count == 0) return;

            var now = DateTime.UtcNow;
            var left = stockQty;
            var open = dispatches.Where(d => d.Status != DispatchStatus.Closed).ToList();
            var targets = open.Count > 0 ? open : new List<Dispatch> { dispatches[^1] };
            for (int i = 0; i < targets.Count && left > 0; i++)
            {
                var d = targets[i];
                var room = i == targets.Count - 1 ? left : Math.Max(0, d.Quantity - d.QtyDone);
                var take = Math.Min(room, left);
                if (take <= 0) continue;
                d.QtyDone  += take;
                d.UpdatedAt = now;
                left       -= take;
                if (d.QtyDone >= d.Quantity && d.Status != DispatchStatus.Closed) d.Status = DispatchStatus.Closed;
                else if (d.Status is DispatchStatus.Pending or DispatchStatus.Scheduled) d.Status = DispatchStatus.InProgress;
            }

            var wo = await _db.WorkOrders.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.WorkOrderId == src.SourceWorkOrderId, ct);
            if (wo == null) return;
            if (wo.Status == WorkOrderStatus.Released) { wo.Status = WorkOrderStatus.InProgress; wo.UpdatedAt = now; }

            // 讀已追蹤實體：上面剛改的 Closed 還沒存檔（同 DispatchController.CheckWorkOrderCompletionAsync 的坑）
            var all = await _db.Dispatches.IgnoreQueryFilters().Where(d => d.WorkOrderId == wo.WorkOrderId).ToListAsync(ct);
            if (DispatchHold.IsWorkOrderDone(all) && wo.Status == WorkOrderStatus.InProgress)
            {
                wo.Status    = wo.IsOutsource ? WorkOrderStatus.Closed : WorkOrderStatus.Completed;
                wo.UpdatedAt = now;
            }
        }
    }
}
