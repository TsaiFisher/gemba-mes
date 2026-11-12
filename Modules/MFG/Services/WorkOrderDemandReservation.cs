using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 工單產出品「留給原需求」的預留（mfg-domain.md §24.2a，2026-10-13）。
    ///
    /// <para>工單的原需求：
    /// <list type="bullet">
    /// <item>成品工單（無父工單、由 MRP 展開）→ 來源 MRP 需求列（<c>ReservationSourceType.MrpRunLine</c>，
    ///       需求列記著銷貨訂單／內部訂單的單號與列號，出貨時據此消耗）。</item>
    /// <item>半成品子工單 → 父工單消耗它的那一筆用料（<c>ReservationSourceType.WorkOrderMaterial</c>，
    ///       父工單發料時由 <see cref="IInventoryReservationService.ConsumeForWorkOrderAsync"/> 消耗）。</item>
    /// <item>手動建立、不是 MRP 來的工單 → 沒有可追的原需求，不預留。</item>
    /// </list></para>
    ///
    /// <para>為什麼要有：投產重算以「毛需求 − 可用量」淨掉了庫存，工單少做了，但那批拿來抵的庫存沒有人鎖——
    /// 隨時可能被別的出貨或工單用掉，最後原需求落空；重算為 0 不投產時更明顯。完工入庫的產出同理。</para>
    /// </summary>
    public interface IWorkOrderDemandReservation
    {
        /// <summary>原需求目前對本工單產品的在手預留量（§24.2「可用＝本單預留＋自由量」的本單預留部分）。</summary>
        Task<decimal> ReservedForDemandAsync(WorkOrder wo, CancellationToken ct = default);

        /// <summary>把 <paramref name="qty"/> 鎖給原需求；沒有可追的原需求時不做事。回傳實際鎖定量。</summary>
        Task<decimal> LockForDemandAsync(WorkOrder wo, decimal qty, CancellationToken ct = default);
    }

    public sealed class WorkOrderDemandReservation(ApplicationDbContext db, IInventoryReservationService reservations)
        : IWorkOrderDemandReservation
    {
        public async Task<decimal> ReservedForDemandAsync(WorkOrder wo, CancellationToken ct = default)
        {
            var q = db.Set<InventoryReservation>()
                .Where(r => r.ProductCode == wo.ProductCode
                         && r.Category == ReservationCategory.OnHand
                         && r.Status == ReservationStatus.Active);

            if (!string.IsNullOrEmpty(wo.ParentWorkOrderId))
                q = q.Where(r => r.SourceType == ReservationSourceType.WorkOrderMaterial
                              && r.SourceDocId == wo.ParentWorkOrderId);
            else if (!string.IsNullOrEmpty(wo.SourceMrpRunId))
                q = q.Where(r => r.SourceType == ReservationSourceType.MrpRunLine
                              && r.SourceDocId == wo.SourceMrpRunId
                              && r.SourceLineSeq == wo.SourceMrpRunLineSeq);
            else
                return 0m;

            return await q.SumAsync(r => (decimal?)r.ReservedQty, ct) ?? 0m;
        }

        public async Task<decimal> LockForDemandAsync(WorkOrder wo, decimal qty, CancellationToken ct = default)
        {
            if (qty <= 0) return 0m;
            var plantId = wo.PlantId ?? string.Empty;

            if (!string.IsNullOrEmpty(wo.ParentWorkOrderId))
            {
                // 父工單消耗這個半成品的那一筆用料（MRP 展開時以 ChildWorkOrderId 掛上）
                var mat = await db.WorkOrderMaterials.AsNoTracking()
                    .Where(m => m.WorkOrderId == wo.ParentWorkOrderId && m.ChildWorkOrderId == wo.WorkOrderId)
                    .Select(m => new { m.OperationSeq })
                    .FirstOrDefaultAsync(ct);
                if (mat == null) return 0m;
                var parentStart = await db.WorkOrders.AsNoTracking().IgnoreQueryFilters()
                    .Where(w => w.WorkOrderId == wo.ParentWorkOrderId)
                    .Select(w => (DateTime?)w.PlannedStartDate).FirstOrDefaultAsync(ct) ?? wo.PlannedEndDate;
                await reservations.ReserveAsync(plantId, wo.ProductCode, qty,
                    ReservationSourceType.WorkOrderMaterial, wo.ParentWorkOrderId!, mat.OperationSeq, parentStart, ct);
                return qty;
            }

            if (!string.IsNullOrEmpty(wo.SourceMrpRunId) && wo.SourceMrpRunLineSeq is { } seq)
            {
                var required = await db.MrpRunLines.AsNoTracking()
                    .Where(l => l.MrpRunId == wo.SourceMrpRunId && l.LineSeq == seq)
                    .Select(l => (DateTime?)l.RequiredDate).FirstOrDefaultAsync(ct) ?? wo.PlannedEndDate;
                await reservations.ReserveAsync(plantId, wo.ProductCode, qty,
                    ReservationSourceType.MrpRunLine, wo.SourceMrpRunId!, seq, required, ct);
                return qty;
            }

            return 0m;
        }
    }
}
