using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// OEE 服務：依機台彙總期間內的三要素。
    ///   稼動時間：ProductionSession（Closed，起訖累計分鐘）
    ///   計畫時間：WorkCalendar 機台層（TM_WKS+TM_WKO 小時 ×60）
    ///   產出/良率：ProductionReport（QtyOk / QtyNg，經 SessionId 歸屬機台）
    ///   理想工時：ProductionReport → Dispatch → WorkOrderOperation.UnitTime（依 TimeUnit 換算分鐘）
    /// 讀取一律 IgnoreQueryFilters + 明確 plantId（多為 IPlantEntity，測試環境無環境廠別亦可運作）。
    /// </summary>
    public class OeeService : IOeeService
    {
        private readonly ApplicationDbContext _db;
        public OeeService(ApplicationDbContext db) => _db = db;

        public async Task<OeeReport> BuildAsync(
            string plantId, DateOnly from, DateOnly to, string? machineCode,
            CancellationToken ct = default)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var toUtc   = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            // 上工紀錄（用於機台歸屬 + 稼動時間）；WorkOrderId 用於拆分品號維度
            var sessions = await _db.ProductionSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.PlantId == plantId
                         && s.MachineCode != null
                         && s.StartAt >= fromUtc && s.StartAt < toUtc
                         && (machineCode == null || s.MachineCode == machineCode))
                .Select(s => new { s.SessionId, s.MachineCode, s.WorkOrderId, s.StartAt, s.EndAt, s.Status })
                .ToListAsync(ct);

            var workOrderIds = sessions.Select(s => s.WorkOrderId).Distinct().ToList();
            var productMap = await _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(w => workOrderIds.Contains(w.WorkOrderId))
                .Select(w => new { w.WorkOrderId, w.ProductCode })
                .ToDictionaryAsync(w => w.WorkOrderId, w => w.ProductCode, ct);

            // Session → (機台, 品號)：每個上工紀錄僅歸屬單一工單，故品號唯一，不受跨品號機台彙總問題影響
            var sessionKey = new Dictionary<string, (string Machine, string Product)>();
            var runMinutes = new Dictionary<(string Machine, string Product), decimal>();
            foreach (var s in sessions)
            {
                var mc = s.MachineCode!;
                var pc = productMap.GetValueOrDefault(s.WorkOrderId, "");
                sessionKey[s.SessionId] = (mc, pc);
                if (s.Status == ProductionSessionStatus.Closed && s.EndAt.HasValue)
                {
                    var mins = (decimal)(s.EndAt.Value - s.StartAt).TotalMinutes;
                    if (mins > 0)
                    {
                        var key = (mc, pc);
                        runMinutes[key] = runMinutes.GetValueOrDefault(key) + mins;
                    }
                }
            }

            // 報工（產出/良率）；經 SessionId 歸屬機台
            var reports = await _db.ProductionReports.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.PlantId == plantId && r.ReportedAt >= fromUtc && r.ReportedAt < toUtc)
                .Select(r => new { r.SessionId, r.DispatchId, r.QtyOk, r.QtyNg })
                .ToListAsync(ct);

            // 理想工時對照：Dispatch → (WorkOrderId, OperationSeq) → WorkOrderOperation.UnitTime
            var dispatchIds = reports.Select(r => r.DispatchId).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
            var dispatches = await _db.Dispatches.IgnoreQueryFilters().AsNoTracking()
                .Where(d => dispatchIds.Contains(d.DispatchId))
                .Select(d => new { d.DispatchId, d.WorkOrderId, d.OperationSeq })
                .ToListAsync(ct);
            var dispatchOp = dispatches.ToDictionary(d => d.DispatchId, d => (d.WorkOrderId, d.OperationSeq));

            var woKeys = dispatches.Select(d => d.WorkOrderId).Distinct().ToList();
            var ops = await _db.WorkOrderOperations.IgnoreQueryFilters().AsNoTracking()
                .Where(o => woKeys.Contains(o.WorkOrderId))
                .Select(o => new { o.WorkOrderId, o.OperationSeq, o.UnitTime, o.TimeUnit })
                .ToListAsync(ct);
            var opUnitMin = ops.ToDictionary(
                o => (o.WorkOrderId, o.OperationSeq), o => ToMinutes(o.UnitTime, o.TimeUnit));

            // 計畫生產時間：WorkCalendar 機台層
            var calendars = await _db.WorkCalendars.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.PlantId == plantId
                         && c.MachineCode != null
                         && c.Date >= from && c.Date <= to
                         && (machineCode == null || c.MachineCode == machineCode))
                .Select(c => new { c.MachineCode, c.TM_WKS, c.TM_WKO })
                .ToListAsync(ct);
            var plannedMinutes = new Dictionary<string, decimal>();
            foreach (var c in calendars)
                plannedMinutes[c.MachineCode!] =
                    plannedMinutes.GetValueOrDefault(c.MachineCode!) + (c.TM_WKS + c.TM_WKO) * 60m;

            // 依（機台, 品號）彙總產出/良率/理想工時
            var totalQty = new Dictionary<(string Machine, string Product), decimal>();
            var goodQty  = new Dictionary<(string Machine, string Product), decimal>();
            var idealMin = new Dictionary<(string Machine, string Product), decimal>();
            foreach (var r in reports)
            {
                if (string.IsNullOrEmpty(r.SessionId) || !sessionKey.TryGetValue(r.SessionId, out var key))
                    continue;   // 無法歸屬機台
                var produced = r.QtyOk + r.QtyNg;
                totalQty[key] = totalQty.GetValueOrDefault(key) + produced;
                goodQty[key]  = goodQty.GetValueOrDefault(key)  + r.QtyOk;

                if (!string.IsNullOrEmpty(r.DispatchId)
                    && dispatchOp.TryGetValue(r.DispatchId, out var opKey)
                    && opUnitMin.TryGetValue(opKey, out var unitMin))
                    idealMin[key] = idealMin.GetValueOrDefault(key) + produced * unitMin;
            }

            // 涉及的所有（機台, 品號）組合；無任何生產紀錄、僅有計畫工時的機台以品號=""representing整機閒置列
            var rowKeys = new HashSet<(string Machine, string Product)>();
            rowKeys.UnionWith(runMinutes.Keys);
            rowKeys.UnionWith(totalQty.Keys);
            foreach (var mc in plannedMinutes.Keys)
                if (!rowKeys.Any(k => k.Machine == mc))
                    rowKeys.Add((mc, ""));

            var machines = rowKeys.Select(k => k.Machine).Distinct().ToList();
            var names = await _db.WorkCenterMachines.AsNoTracking()
                .Where(m => machines.Contains(m.MachineCode))
                .Select(m => new { m.MachineCode, m.MachineName })
                .ToListAsync(ct);
            var nameMap = names
                .GroupBy(m => m.MachineCode)
                .ToDictionary(g => g.Key, g => g.First().MachineName);

            var productCodes = rowKeys.Select(k => k.Product).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var productInfo = await _db.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom })
                .ToDictionaryAsync(p => p.ProductCode, ct);

            var report = new OeeReport
            {
                Rows = rowKeys.Select(k =>
                {
                    productInfo.TryGetValue(k.Product, out var pi);
                    return new OeeRow
                    {
                        MachineCode    = k.Machine,
                        MachineName    = nameMap.TryGetValue(k.Machine, out var n) ? n : "",
                        ProductCode    = k.Product,
                        ProductName    = pi?.ProductName ?? "",
                        UomCode        = pi?.StockUom,
                        // PlannedMinutes 為機台層級（WorkCalendar 不分品號），同機台各品號列共用同一值；
                        // Availability = 該品號 RunMinutes ÷ 機台 PlannedMinutes，代表該品號佔用機台總可用工時的比例。
                        PlannedMinutes = Round2(plannedMinutes.GetValueOrDefault(k.Machine)),
                        RunMinutes     = Round2(runMinutes.GetValueOrDefault(k)),
                        IdealMinutes   = Round2(idealMin.GetValueOrDefault(k)),
                        TotalQty       = totalQty.GetValueOrDefault(k),
                        GoodQty        = goodQty.GetValueOrDefault(k),
                    };
                })
                .OrderBy(r => r.MachineCode).ThenBy(r => r.ProductCode)
                .ToList()
            };
            return report;
        }

        private static decimal ToMinutes(decimal unitTime, OperationTimeUnit unit) => unit switch
        {
            OperationTimeUnit.Second => unitTime / 60m,
            OperationTimeUnit.Hour   => unitTime * 60m,
            _                        => unitTime,   // Minute
        };

        private static decimal Round2(decimal v) => System.Math.Round(v, 2, System.MidpointRounding.AwayFromZero);
    }
}
