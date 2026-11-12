using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Models.Analytics;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 「產線即時看板」聚合查詢（一鍵看整條線在忙什麼）。純即時快照，不快取——
    /// 這是即時現場狀態，不是期間統計，快取反而會讓看板顯示過期資料。
    /// </summary>
    public class LineStatusAnalyticsService
    {
        private readonly ApplicationDbContext _db;

        public LineStatusAnalyticsService(ApplicationDbContext db) => _db = db;

        public async Task<LineStatusDashboardVm> BuildDashboardAsync(string? plantId)
        {
            var lines = await _db.Set<WorkCenter>().AsNoTracking()
                .Where(w => w.PlantId == plantId && w.IsProductionLine && w.IsActive)
                .OrderBy(w => w.WorkCenterCode)
                .ToListAsync();

            var vm = new LineStatusDashboardVm();
            if (lines.Count == 0) return vm;

            var lineCodes = lines.Select(l => l.WorkCenterCode).ToList();

            var activeDispatches = await (
                from d in _db.Dispatches.AsNoTracking()
                join o in _db.WorkOrderOperations.AsNoTracking()
                    on new { d.WorkOrderId, d.OperationSeq } equals new { o.WorkOrderId, o.OperationSeq }
                where d.PlantId == plantId
                      && (d.Status == DispatchStatus.InProgress || d.Status == DispatchStatus.SetupInProgress)
                      && lineCodes.Contains(o.WorkCenterId)
                select new { Dispatch = d, WorkCenterId = o.WorkCenterId }
            ).ToListAsync();

            var dispatchIds = activeDispatches.Select(x => x.Dispatch.DispatchId).ToList();
            var workOrderIds = activeDispatches.Select(x => x.Dispatch.WorkOrderId).Distinct().ToList();

            var workOrders = await _db.WorkOrders.AsNoTracking()
                .Where(w => workOrderIds.Contains(w.WorkOrderId))
                .ToDictionaryAsync(w => w.WorkOrderId, w => w.ProductCode);

            var openSessions = dispatchIds.Count == 0
                ? new List<ProductionSession>()
                : await _db.ProductionSessions.AsNoTracking()
                    .Where(s => dispatchIds.Contains(s.DispatchId) && s.Status == ProductionSessionStatus.Open)
                    .ToListAsync();

            var openSetups = dispatchIds.Count == 0
                ? new List<ProductionSetup>()
                : await _db.ProductionSetups.AsNoTracking()
                    .Where(s => dispatchIds.Contains(s.DispatchId) && s.Status == ProductionSetupStatus.Open)
                    .ToListAsync();

            var operatorIds = openSessions.Select(s => s.OperatorId)
                .Concat(openSetups.Select(s => s.OperatorId))
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x!)
                .Distinct()
                .ToList();
            var userNames = operatorIds.Count == 0
                ? new Dictionary<string, string>()
                : await _db.Users.AsNoTracking()
                    .Where(u => operatorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id);

            string DisplayName(string? id) => id != null && userNames.TryGetValue(id, out var n) ? n : (id ?? "");

            foreach (var line in lines)
            {
                var card = new LineStatusCardVm
                {
                    WorkCenterCode = line.WorkCenterCode,
                    WorkCenterName = line.WorkCenterName,
                };

                var lineDispatches = activeDispatches.Where(x => x.WorkCenterId == line.WorkCenterCode).ToList();
                foreach (var x in lineDispatches)
                {
                    var d = x.Dispatch;
                    var sessions = openSessions.Where(s => s.DispatchId == d.DispatchId)
                        .Select(s => new LineOperatorVm { OperatorName = DisplayName(s.OperatorId), StartAt = s.StartAt })
                        .OrderBy(o => o.StartAt)
                        .ToList();
                    var setups = openSetups.Where(s => s.DispatchId == d.DispatchId)
                        .Select(s => new LineOperatorVm { OperatorName = DisplayName(s.OperatorId), StartAt = s.SetupStartAt })
                        .OrderBy(o => o.StartAt)
                        .ToList();

                    card.ActiveDispatches.Add(new LineActiveDispatchVm
                    {
                        // 歷史/單一資源模式的派工 MachineCode 存的是工作中心代號本身——
                        // 只有真正落在某條線別上時才回傳線別代號，看板才需要分組顯示。
                        LineCode      = d.MachineCode != null && d.MachineCode != line.WorkCenterCode
                                        ? d.MachineCode : string.Empty,
                        DispatchId    = d.DispatchId,
                        WorkOrderId   = d.WorkOrderId,
                        ProductCode   = workOrders.GetValueOrDefault(d.WorkOrderId, ""),
                        StatusLabel   = d.Status.ToString(),
                        Quantity      = d.Quantity,
                        QtyDone       = d.QtyDone,
                        ActiveOperators      = sessions,
                        ActiveSetupOperators = setups,
                    });
                }

                card.LineState = lineDispatches.Any(x => x.Dispatch.Status == DispatchStatus.InProgress)
                    ? "Producing"
                    : lineDispatches.Any(x => x.Dispatch.Status == DispatchStatus.SetupInProgress)
                        ? "SettingUp"
                        : "Idle";

                vm.Lines.Add(card);
            }

            return vm;
        }
    }
}
