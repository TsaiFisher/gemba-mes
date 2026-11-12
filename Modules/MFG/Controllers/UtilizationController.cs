using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

[Authorize]
public class UtilizationController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "Utilization";

    public UtilizationController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IStringLocalizer<ProjectZSharedResource> localizer)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _localizer         = localizer;
    }

    public async Task<IActionResult> Index(
        string? fromDate, string? toDate,
        string? workCenterId, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched     = searched;
        ViewBag.FromDate     = fromDate;
        ViewBag.ToDate       = toDate;
        ViewBag.WorkCenterId = workCenterId;
        ViewBag.CanPrint     = await _permissionService.CanPrint(MenuKey);
        ViewBag.WorkCenterOptions = await _context.WorkCenters
            .AsNoTracking().OrderBy(wc => wc.WorkCenterCode)
            .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
            .ToListAsync();

        if (!searched)
            return View(new List<UtilizationRowVm>());

        // 本報表的分母是查詢期間本身（可用工時＝期間長度）。起訖留白時
        // DateTime.TryParse 會給出 default(DateTime)＝0001-01-01，期間長度變成
        // 約 1775 萬小時，稼動率必定顯示 0.0%，同時掃遍全部歷史派工。
        // 那不是「慢」而是錯的數字，所以這裡擋下來而不是給預設值。
        if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
        {
            ViewBag.DateRangeRequired = true;
            return View(new List<UtilizationRowVm>());
        }

        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate, string? workCenterId)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        // 與 Index 同一道閘：不擋的話直接打 /Utilization/Export 仍會匯出那份分母錯誤的報表。
        if (string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
            return BadRequest(_localizer["DateRangeRequired"].Value);
        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "工作中心稼動率" : "Work Center Utilization",
            Headers = isZh
                ? ["工作中心", "可用工時(hr)", "停機工時(hr)", "淨可用(hr)", "排程工時(hr)", "稼動率%"]
                : ["Work Center", "Planned(hr)", "Downtime(hr)", "Available(hr)", "Scheduled(hr)", "Utilization%"],
            Rows = rows.Select(r => new object?[]
            {
                r.WorkCenterId, r.PlannedHours, r.DowntimeHours,
                r.AvailableHours, r.ScheduledHours, r.UtilizationRate
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"Utilization_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<UtilizationRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate, string? workCenterId)
    {
        var plantId = _userContext.PlantId ?? "";
        DateTime.TryParse(fromDate, out var startParsed);
        DateTime.TryParse(toDate, out var endParsed);
        // 起訖在 Index／Export 都已擋成必填，走得到這條的只有解析失敗（手改網址、舊書籤）。
        // 廠別當地日而非伺服器時區（R22）。
        var endRaw = endParsed == default
            ? _dateTimeService.GetLocalDate().ToDateTime(TimeOnly.MinValue).AddDays(1)
            : endParsed.AddDays(1);

        // 數字本身就是廠別當地時間（mfg-domain.md §UTC 地雷），Kind 只是貼給 Npgsql 的標籤，
        // 必須對齊欄位型別——本報表同時查兩張表，型別不同故需兩組同號不同標籤的區間：
        //   Dispatches.ScheduledStart/End = timestamptz              → Utc
        //   PlannedDowntimes.StartTime/EndTime = timestamp（壁鐘）   → Unspecified
        // 拿 Utc 去查後者會擲 "Cannot write DateTime with Kind=UTC to
        // PostgreSQL type 'timestamp without time zone'"（查詢必 500）。
        var start = DateTime.SpecifyKind(startParsed, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(endRaw, DateTimeKind.Utc);
        var startLocal = DateTime.SpecifyKind(startParsed, DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(endRaw, DateTimeKind.Unspecified);

        // Scheduled hours from Dispatches (compute in-memory — PostgreSQL lacks DateDiffHour)
        var dispatchQ =
            from d in _context.Dispatches
            join op in _context.WorkOrderOperations
                on new { d.WorkOrderId, d.OperationSeq }
                equals new { op.WorkOrderId, op.OperationSeq }
            where d.PlantId == plantId
               && d.ScheduledStart.HasValue
               && d.ScheduledEnd.HasValue
               && d.ScheduledStart.Value >= start
               && d.ScheduledStart.Value < end
            select new
            {
                op.WorkCenterId,
                d.ScheduledStart,
                d.ScheduledEnd,
            };

        if (!string.IsNullOrWhiteSpace(workCenterId))
            dispatchQ = dispatchQ.Where(r => r.WorkCenterId == workCenterId);

        var dispatchRaw = await dispatchQ.AsNoTracking().ToListAsync();

        var scheduledByWc = dispatchRaw
            .GroupBy(r => r.WorkCenterId)
            .ToDictionary(
                g => g.Key,
                g => (decimal)g.Sum(r => (r.ScheduledEnd!.Value - r.ScheduledStart!.Value).TotalHours));

        // Downtime hours from PlannedDowntimes
        var downtimeQ =
            from pd in _context.PlannedDowntimes
            where pd.PlantId == plantId
               && pd.IsActive
               && pd.StartTime >= startLocal
               && pd.StartTime < endLocal
            select new { pd.WorkCenterId, pd.StartTime, pd.EndTime };

        if (!string.IsNullOrWhiteSpace(workCenterId))
            downtimeQ = downtimeQ.Where(r => r.WorkCenterId == workCenterId);

        var downtimeRaw = await downtimeQ.AsNoTracking().ToListAsync();

        var downtimeByWc = downtimeRaw
            .Where(r => r.WorkCenterId != null)
            .GroupBy(r => r.WorkCenterId!)
            .ToDictionary(
                g => g.Key,
                g => (decimal)g.Sum(r => (r.EndTime - r.StartTime).TotalHours));

        // 可用工時取自工作日曆（TM_WKS 正班＋TM_WKO 加班），而不是期間的日曆總時數。
        // 一次撈整個區間在記憶體做三層 fallback——IWorkCalendarService.GetWorkHoursAsync
        // 是「一天一個工作中心一次查詢」，用在這裡會是天數×工作中心數次往返。
        // 機台層（MachineCode != null）刻意排除：本報表是工作中心層級，納入會重複計。
        var fromDay = DateOnly.FromDateTime(startParsed);
        var toDay   = DateOnly.FromDateTime(endRaw.AddDays(-1));   // endRaw 是開區間上界
        var calRows = await _context.Set<Models.WorkCalendar>().AsNoTracking()
            .Where(w => w.PlantId == plantId
                     && w.MachineCode == null
                     && w.Date >= fromDay && w.Date <= toDay)
            .Select(w => new { w.WorkCenterId, w.Date, Hours = w.TM_WKS + w.TM_WKO })
            .ToListAsync();

        var wcCalendar = calRows.Where(c => c.WorkCenterId != null)
            .GroupBy(c => c.WorkCenterId!)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours));
        // 廠別層是沒有自己日曆的工作中心的退路（對齊 WorkCalendarService 的 fallback 順序）。
        var plantCalendarHours = calRows.Where(c => c.WorkCenterId == null).Sum(c => c.Hours);
        var plantHasCalendar   = calRows.Any(c => c.WorkCenterId == null);

        var allWorkCenters = scheduledByWc.Keys.Union(downtimeByWc.Keys).ToList();

        return allWorkCenters
            .Select(wc =>
            {
                scheduledByWc.TryGetValue(wc, out var sched);
                downtimeByWc.TryGetValue(wc, out var down);
                var hasOwn = wcCalendar.TryGetValue(wc, out var ownHours);
                return new UtilizationRowVm
                {
                    WorkCenterId   = wc,
                    PlannedHours   = hasOwn ? ownHours : plantCalendarHours,
                    HasCalendar    = hasOwn || plantHasCalendar,
                    DowntimeHours  = down,
                    ScheduledHours = sched,
                };
            })
            .OrderBy(r => r.WorkCenterId)
            .ToList();
    }
}
