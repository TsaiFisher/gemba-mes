using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IProductionCostAccrualService" />
    public class ProductionCostAccrualService : IProductionCostAccrualService
    {
        private readonly ApplicationDbContext _db;

        public ProductionCostAccrualService(ApplicationDbContext db) => _db = db;

        public async Task AccrueOnSessionStopAsync(ProductionSession closedSession)
        {
            if (closedSession.EndAt == null) return;
            var hours = (decimal)(closedSession.EndAt.Value - closedSession.StartAt).TotalHours;
            if (hours <= 0) return;

            var dispatch = await _db.Dispatches
                .FirstOrDefaultAsync(d => d.DispatchId == closedSession.DispatchId);
            if (dispatch == null) return;

            var op = await _db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq);
            if (op == null) return;

            var wc = await _db.Set<WorkCenter>().AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkCenterCode == op.WorkCenterId);
            if (wc == null) return;

            var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            if (wo == null) return;

            // 人工成本：不論一般機台或生產線，都是每人各自工時直接累加（多人各做各的工時，本來就該加總)。
            wo.ActualLaborCost += hours * wc.StandardLaborCost;

            if (!wc.IsProductionLine)
            {
                // 一般機台恆同時只有 1 筆 Open session，session 工時＝機台占用時間，直接累加即可。
                wo.ActualOverheadCost += hours * wc.MachineHourlyRate;
            }
            else
            {
                // 生產線可能多人同時上工：overhead（機台/產線費用）不可按人頭疊加，否則 5 人同時
                // 上工 2 小時，overhead 會被灌成 10 小時份。改成「這張派工單最後一人下工」時，
                // 依「自上次結算後、本輪最早的上工時刻」到現在的運轉時間窗結算一次。
                var stillOpen = await _db.ProductionSessions.AnyAsync(s =>
                    s.DispatchId == dispatch.DispatchId &&
                    s.Status == ProductionSessionStatus.Open &&
                    s.SessionId != closedSession.SessionId);
                if (!stillOpen)
                {
                    var chargedThrough = dispatch.OverheadChargedThroughUtc ?? DateTime.MinValue;
                    var windowStart = await _db.ProductionSessions
                        .Where(s => s.DispatchId == dispatch.DispatchId && s.StartAt > chargedThrough)
                        .MinAsync(s => (DateTime?)s.StartAt) ?? closedSession.StartAt;

                    var overheadHours = (decimal)(closedSession.EndAt.Value - windowStart).TotalHours;
                    if (overheadHours > 0)
                        wo.ActualOverheadCost += overheadHours * wc.MachineHourlyRate;

                    dispatch.OverheadChargedThroughUtc = closedSession.EndAt.Value;
                }
            }

            wo.UpdatedAt = DateTime.UtcNow;
        }

        public async Task AccrueOnSetupDoneAsync(IReadOnlyList<ProductionSetup> closedSetups, Dispatch dispatch)
        {
            if (closedSetups.Count == 0) return;

            var op = await _db.WorkOrderOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.WorkOrderId == dispatch.WorkOrderId
                                       && o.OperationSeq == dispatch.OperationSeq);
            if (op == null) return;

            var wc = await _db.Set<WorkCenter>().AsNoTracking()
                .FirstOrDefaultAsync(w => w.WorkCenterCode == op.WorkCenterId);
            if (wc == null) return;

            var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == dispatch.WorkOrderId);
            if (wo == null) return;

            // 人工：逐人各自的備料時長加總。備料人員可能不是後面實際生產的人，
            // 這段工時要記在自己的頭上，才不會讓「換模的人」在成本上隱形。
            var laborHours = closedSetups
                .Where(s => s.SetupEndAt != null && s.SetupEndAt > s.SetupStartAt)
                .Sum(s => (decimal)(s.SetupEndAt!.Value - s.SetupStartAt).TotalHours);

            if (laborHours > 0)
                wo.ActualLaborCost += laborHours * wc.StandardLaborCost;

            // 製造費用：機台/產線在準備期間就占用一次，依時間窗結算，不隨人數疊加。
            var windowStart = closedSetups.Min(s => s.SetupStartAt);
            var windowEnd   = closedSetups.Max(s => s.SetupEndAt ?? s.SetupStartAt);
            var overheadHours = (decimal)(windowEnd - windowStart).TotalHours;
            if (overheadHours > 0)
                wo.ActualOverheadCost += overheadHours * wc.MachineHourlyRate;

            wo.UpdatedAt = DateTime.UtcNow;
        }
    }
}
