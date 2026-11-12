using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 報工／完工入庫的容器規則（container-tracking.md §8 L1）。桌機與手機報工共用，避免各寫一份而漂移。
    /// 容器欄可以是「容器種類」（Container 主檔），也可以是箱號（箱子個體，§9.4 生產中容器）：
    /// 掃到箱號就裝箱——箱子綁到這張工單＋工序，報工容器紀錄記下箱號，種類取箱子的種類。
    /// </summary>
    public static class ReportContainers
    {
        /// <summary>工序設定的完工容器（RoutingOperation.ContainerOutCode）；工單無途程或該工序未設回 null。</summary>
        public static async Task<string?> OperationOutContainerAsync(
            ApplicationDbContext db, string workOrderId, int operationSeq, CancellationToken ct = default)
        {
            var routingId = await db.WorkOrders.AsNoTracking()
                .Where(w => w.WorkOrderId == workOrderId)
                .Select(w => w.RoutingId)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(routingId)) return null;

            return await db.RoutingOperations.AsNoTracking()
                .Where(o => o.RoutingId == routingId && o.OperationSeq == operationSeq)
                .Select(o => o.ContainerOutCode)
                .FirstOrDefaultAsync(ct);
        }

        /// <summary>
        /// 最後一道工序的完工容器＝成品裝箱用的容器。「最後一道」取工單工序（不是途程），
        /// 與完工量的定義（<see cref="WorkOrderOutput"/>）同源。
        /// </summary>
        public static async Task<string?> LastOperationOutContainerAsync(
            ApplicationDbContext db, string workOrderId, CancellationToken ct = default)
        {
            var lastSeq = await db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == workOrderId)
                .Select(o => (int?)o.OperationSeq)
                .MaxAsync(ct);
            return lastSeq == null ? null : await OperationOutContainerAsync(db, workOrderId, lastSeq.Value, ct);
        }

        public static Task<bool> IsActiveAsync(ApplicationDbContext db, string containerCode, CancellationToken ct = default)
            => db.Containers.AsNoTracking().AnyAsync(c => c.ContainerCode == containerCode && c.IsActive, ct);

        /// <summary>
        /// 報工帶的是有效容器種類時，記一筆報工容器（容器→實際數量）。
        /// 手機掃到的可能是箱標籤上的批號而非容器種類代碼——那種照舊只存在 ProductionReport.ContainerCode，不硬擋，
        /// 斷線補送的報工也才不會因此卡在佇列裡。
        /// </summary>
        public static async Task AttachAsync(ApplicationDbContext db, ProductionReport report,
            string? containerUnitId = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(report.ContainerCode) || report.QtyOk <= 0) return;
            // 裝了箱子就一定記：在製數量從這筆推導，種類事後被停用也不能讓箱子看起來是空的
            if (containerUnitId == null && !await IsActiveAsync(db, report.ContainerCode, ct)) return;

            report.Containers.Add(new ProductionReportContainer
            {
                ReportId      = report.ReportId,
                ContainerSeq  = 1,
                ContainerCode = report.ContainerCode,
                ActualQty     = report.QtyOk,
                ContainerUnitId = containerUnitId
            });
        }

        /// <summary>
        /// 容器欄是箱號時裝箱（不存檔，與報工同交易）。不是箱號、或沒有合格數可裝，回 (null, null)，
        /// 呼叫端照原本的容器種類處理；箱子不能裝（停用、裝著別的東西）回錯誤訊息，箱子不動。
        /// <paramref name="now"/> 必須等於報工紀錄的 CreatedAt（在製數量從綁定時間起算）。
        /// </summary>
        public static async Task<(ContainerUnit? Unit, string? Error)> LoadBoxAsync(
            ApplicationDbContext db, string? code, Dispatch dispatch, decimal qtyOk, string? userId, DateTime now,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(code) || qtyOk <= 0) return (null, null);
            var units = new ContainerUnitService(db);
            var unit  = await units.FindAsync(code, ct);
            if (unit == null) return (null, null);

            var product = await db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.WorkOrderId == dispatch.WorkOrderId)
                .Select(w => w.ProductCode)
                .FirstOrDefaultAsync(ct) ?? string.Empty;
            try
            {
                await units.LoadWipAsync(unit, dispatch.WorkOrderId, dispatch.OperationSeq, product, qtyOk, userId, now, ct);
                return (unit, null);
            }
            catch (InvalidOperationException ex) { return (null, ex.Message); }
        }
    }
}
