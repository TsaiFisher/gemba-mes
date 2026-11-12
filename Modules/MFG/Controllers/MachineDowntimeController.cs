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
public class MachineDowntimeController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "MachineDowntime";

    public MachineDowntimeController(
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
        string? workCenterId, string? machineCode,
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched     = searched;
        ViewBag.FromDate     = fromDate;
        ViewBag.ToDate       = toDate;
        ViewBag.WorkCenterId = workCenterId;
        ViewBag.MachineCode  = machineCode;
        ViewBag.CanPrint     = await _permissionService.CanPrint(MenuKey);
        ViewBag.WorkCenterOptions = await _context.WorkCenters
            .AsNoTracking().OrderBy(wc => wc.WorkCenterCode)
            .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
            .ToListAsync();
        ViewBag.MachineOptions = await _context.WorkCenterMachines
            .AsNoTracking().OrderBy(m => m.MachineCode)
            .Select(m => new { m.MachineCode, m.MachineName })
            .ToListAsync();

        if (!searched)
            return View(new List<MachineDowntimeRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId, machineCode);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? workCenterId, string? machineCode)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId, machineCode);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "設備停機分析" : "Machine Downtime Analysis",
            Headers = isZh
                ? ["工作中心", "設備代號", "停機類型", "次數", "停機工時(hr)", "原因"]
                : ["Work Center", "Machine", "Type", "Events", "Downtime(hr)", "Reason"],
            Rows = rows.Select(r => new object?[]
            {
                r.WorkCenterId, r.MachineCode, r.DowntimeType,
                r.EventCount, r.TotalHours, r.Reason
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"MachineDowntime_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<MachineDowntimeRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate,
        string? workCenterId, string? machineCode)
    {
        var plantId = _userContext.PlantId ?? "";
        DateTime.TryParse(fromDate, out var startParsed);
        DateTime.TryParse(toDate, out var endParsed);
        var endRaw = endParsed == default ? DateTime.UtcNow.Date.AddDays(1) : endParsed.AddDays(1);

        // PlannedDowntimes.StartTime/EndTime 是 timestamp without time zone（廠別壁鐘，
        // 見 PlannedDowntime model 與 mfg-domain.md §計劃停機），比較值的 Kind 必須是
        // Unspecified——貼 Utc 會擲 "Cannot write DateTime with Kind=UTC to
        // PostgreSQL type 'timestamp without time zone'"（查詢必 500）。
        // 數字不變，只是換掉貼給 Npgsql 的標籤，查詢區間與原本一致。
        var start = DateTime.SpecifyKind(startParsed, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(endRaw, DateTimeKind.Unspecified);

        var query =
            from pd in _context.PlannedDowntimes
            where pd.PlantId == plantId
               && pd.IsActive
               && pd.StartTime >= start
               && pd.StartTime < end
            select pd;

        if (!string.IsNullOrWhiteSpace(workCenterId))
            query = query.Where(r => r.WorkCenterId == workCenterId);
        if (!string.IsNullOrWhiteSpace(machineCode))
            query = query.Where(r => r.MachineCode == machineCode);

        var raw = await query.AsNoTracking().ToListAsync();

        return raw
            .GroupBy(r => new { r.WorkCenterId, r.MachineCode, r.DowntimeType, r.Reason })
            .Select(g => new MachineDowntimeRowVm
            {
                WorkCenterId = g.Key.WorkCenterId,
                MachineCode  = g.Key.MachineCode,
                DowntimeType = g.Key.DowntimeType,
                EventCount   = g.Count(),
                TotalHours   = (decimal)g.Sum(r => (r.EndTime - r.StartTime).TotalHours),
                Reason       = g.Key.Reason,
            })
            .OrderBy(r => r.WorkCenterId)
            .ThenByDescending(r => r.TotalHours)
            .ToList();
    }
}
