using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// §21.5 物料備齊確認閘口實作（v1）。
    /// 規則（逐料）：
    ///   1. Backflush 料 → Ready（完工才沖，開工前不硬性要求）。
    ///   2. 有 ChildWorkOrderId（自製半成品）→ 子工單須 Completed，否則 PendingSubassembly。
    ///   3. 其餘 → 本工單可用量（廠級自由量 ＋ 本工單自身 Active 在手預留）≥ RequiredQty，否則 Shortage。
    /// 整體：任一非 Ready → Shortage；全 Ready → Ready。寫回 WorkOrder.MaterialReadinessStatus。
    /// </summary>
    public class WorkOrderMaterialReadinessService : IWorkOrderMaterialReadinessService
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryReservationService _reservations;

        public WorkOrderMaterialReadinessService(ApplicationDbContext db, IInventoryReservationService reservations)
        {
            _db = db;
            _reservations = reservations;
        }

        public async Task<MaterialReadinessResult> CheckAsync(string workOrderId, CancellationToken ct = default)
        {
            var wo = await _db.WorkOrders
                .Include(w => w.Operations).ThenInclude(o => o.Materials)
                .FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct)
                ?? throw new InvalidOperationException(PzText.Get("Mfg_WorkOrderNotFoundFmt", workOrderId));

            var plantId = wo.PlantId ?? string.Empty;
            var materials = wo.Operations.SelectMany(o => o.Materials).ToList();

            var lines = new List<MaterialReadinessLine>();
            foreach (var mat in materials)
            {
                // 1. Backflush：完工才沖，開工前視為 Ready
                if (mat.IssueMethod == MaterialIssueMethod.Backflush)
                {
                    lines.Add(new MaterialReadinessLine(mat.ProductCode, mat.RequiredQty, 0m, MaterialLineReadiness.Ready));
                    continue;
                }

                // 2. 自製半成品：子工單須完工
                if (!string.IsNullOrEmpty(mat.ChildWorkOrderId))
                {
                    var childCompleted = await _db.WorkOrders.AsNoTracking()
                        .AnyAsync(w => w.WorkOrderId == mat.ChildWorkOrderId
                                    && (w.Status == WorkOrderStatus.Completed || w.Status == WorkOrderStatus.Closed), ct);
                    lines.Add(new MaterialReadinessLine(
                        mat.ProductCode, mat.RequiredQty, 0m,
                        childCompleted ? MaterialLineReadiness.Ready : MaterialLineReadiness.PendingSubassembly));
                    continue;
                }

                // 3. 採購/庫存料：本工單可用量＝廠級自由量 ＋ 本工單自身已鎖住的預留
                //    （MRP 展開時已為本工單鎖住在手庫存，該預留同時被 GetPlantAvailableQtyAsync 扣掉；
                //     不加回的話，MRP 鎖料越成功、本工單越被自己的預留判為缺料。
                //     自身預留含「該工單的備料單」預留——備料確認時工單預留已轉移過去）
                var free = await _reservations.GetPlantAvailableQtyAsync(plantId, mat.ProductCode, ct);
                var own  = await _reservations.GetReservedForWorkOrderAsync(
                    workOrderId, mat.ProductCode, ct);
                var available = free + own;
                lines.Add(new MaterialReadinessLine(
                    mat.ProductCode, mat.RequiredQty, available,
                    available >= mat.RequiredQty ? MaterialLineReadiness.Ready : MaterialLineReadiness.Shortage));
            }

            var overall = lines.All(l => l.State == MaterialLineReadiness.Ready)
                ? MaterialReadinessStatus.Ready
                : MaterialReadinessStatus.Shortage;

            wo.MaterialReadinessStatus = overall;
            wo.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new MaterialReadinessResult(overall, lines);
        }
    }
}
