using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public interface IFieldTelemetryReportService
    {
        Task<FieldTelemetryReportVm> BuildAsync(string? plantId, int hours, string? kind, DateTime nowUtc);
    }

    /// <summary>
    /// 現場遙測儀表板的彙總。全部在資料庫端 GROUP BY；明細只取最近的一段。
    ///
    /// <para>範圍是「本廠＋沒有廠別的」：未登入時（登入頁出錯、工作階段過期）送來的事件沒有廠別，
    /// 不納入的話最需要看的那一類永遠看不到。那類事件只有錯誤訊息與頁面，不含業務資料。</para>
    /// </summary>
    public sealed class FieldTelemetryReportService : IFieldTelemetryReportService
    {
        internal static readonly int[] AllowedHours = { 24, 168, 720 };
        private readonly ApplicationDbContext _db;

        public FieldTelemetryReportService(ApplicationDbContext db) => _db = db;

        public async Task<FieldTelemetryReportVm> BuildAsync(string? plantId, int hours, string? kind, DateTime nowUtc)
        {
            if (!AllowedHours.Contains(hours)) hours = 24;
            var since = nowUtc.AddHours(-hours);
            var q = _db.ClientTelemetryEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.ReceivedAt >= since && (e.PlantId == plantId || e.PlantId == null));

            var errors = q.Where(e => e.Kind == ClientTelemetryKind.Error || e.Kind == ClientTelemetryKind.Rejection);
            var api = q.Where(e => e.Kind == ClientTelemetryKind.ApiStats);
            var offline = q.Where(e => e.Kind == ClientTelemetryKind.Offline);

            var vm = new FieldTelemetryReportVm { Hours = hours, SelectedKind = kind };
            vm.ErrorCount = await errors.SumAsync(e => (int?)e.Count) ?? 0;
            vm.ApiCalls = await api.SumAsync(e => (int?)e.Count) ?? 0;
            vm.ApiSlow = await api.SumAsync(e => (int?)((e.Under10s ?? 0) + (e.Over10s ?? 0))) ?? 0;
            vm.ApiFailures = await api.SumAsync(e => (int?)(e.Failures ?? 0)) ?? 0;
            vm.ApiConflicts = await api.SumAsync(e => (int?)(e.Conflicts ?? 0)) ?? 0;
            vm.OfflineCount = await offline.SumAsync(e => (int?)e.Count) ?? 0;
            vm.OfflineMinutes = (int)((await offline.SumAsync(e => (long?)e.DurationMs) ?? 0) / 60_000);
            vm.ActiveDevices = await q.Where(e => e.DeviceId != null).Select(e => e.DeviceId).Distinct().CountAsync();

            vm.TopErrors = await errors
                .GroupBy(e => new { e.Kind, e.Message, e.Page })
                .Select(g => new FieldTelemetryErrorRow
                {
                    Kind = g.Key.Kind, Message = g.Key.Message, Page = g.Key.Page,
                    Count = g.Sum(e => e.Count),
                    Devices = g.Select(e => e.DeviceId).Distinct().Count(),
                    LastSeenUtc = g.Max(e => e.ReceivedAt),
                })
                .OrderByDescending(r => r.Count).ThenByDescending(r => r.LastSeenUtc)
                .Take(20).ToListAsync();

            vm.Endpoints = await api
                .GroupBy(e => e.Endpoint)
                .Select(g => new FieldTelemetryEndpointRow
                {
                    Endpoint = g.Key,
                    Calls = g.Sum(e => e.Count),
                    TotalMs = g.Sum(e => e.DurationMs ?? 0),
                    MaxMs = g.Max(e => e.MaxMs ?? 0),
                    Slow3To10 = g.Sum(e => e.Under10s ?? 0),
                    Over10s = g.Sum(e => e.Over10s ?? 0),
                    Failures = g.Sum(e => e.Failures ?? 0),
                    Conflicts = g.Sum(e => e.Conflicts ?? 0),
                })
                .OrderByDescending(r => r.Failures + r.Conflicts + r.Over10s + r.Slow3To10).ThenByDescending(r => r.Calls)
                .Take(30).ToListAsync();

            // 裝置現況：每支手機最後一次回報，以及最後一次回報的佇列（還有幾筆沒送、最舊等了多久）
            var lastSeen = await q.Where(e => e.DeviceId != null)
                .GroupBy(e => e.DeviceId!)
                .Select(g => new { DeviceId = g.Key, Last = g.Max(e => e.ReceivedAt), Errors = g.Count(e => e.Kind == ClientTelemetryKind.Error || e.Kind == ClientTelemetryKind.Rejection) })
                .OrderByDescending(x => x.Last).Take(100).ToListAsync();
            var ids = lastSeen.Select(x => x.DeviceId).ToList();
            var latest = await q.Where(e => e.DeviceId != null && ids.Contains(e.DeviceId!))
                .GroupBy(e => e.DeviceId!)
                .Select(g => g.OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id).First())
                .ToListAsync();
            var latestQueue = await q.Where(e => e.Kind == ClientTelemetryKind.Queue && e.DeviceId != null && ids.Contains(e.DeviceId!))
                .GroupBy(e => e.DeviceId!)
                .Select(g => g.OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id).First())
                .ToListAsync();
            vm.Devices = lastSeen.Select(x =>
            {
                var l = latest.FirstOrDefault(e => e.DeviceId == x.DeviceId);
                var lq = latestQueue.FirstOrDefault(e => e.DeviceId == x.DeviceId);
                return new FieldTelemetryDeviceRow
                {
                    DeviceId = x.DeviceId,
                    LastSeenUtc = x.Last,
                    UserName = l?.UserName,
                    AppVersion = l?.AppVersion,
                    NetType = l?.NetType,
                    RttMs = l?.RttMs,
                    Errors = x.Errors,
                    QueuePending = lq?.QueuePending ?? 0,
                    QueueOldestMinutes = lq?.QueueOldestMinutes,
                };
            })
            .OrderByDescending(d => d.QueuePending).ThenByDescending(d => d.LastSeenUtc)
            .ToList();
            vm.DevicesWithPending = vm.Devices.Count(d => d.QueuePending > 0);

            var recent = q.Where(e => e.Kind != ClientTelemetryKind.ApiStats);
            if (!string.IsNullOrEmpty(kind) && ClientTelemetryKind.All.Contains(kind)) recent = recent.Where(e => e.Kind == kind);
            vm.Recent = await recent.OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id).Take(500).ToListAsync();
            return vm;
        }
    }
}
