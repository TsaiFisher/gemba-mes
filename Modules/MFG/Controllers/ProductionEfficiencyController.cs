using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Modules.MFG.Controllers;

[Authorize]
public class ProductionEfficiencyController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "ProductionEfficiency";

    public ProductionEfficiencyController(
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
        // 期間在本報表只是篩選（不是分母），所以留白給預設近 30 天而不是擋下來。
        // 原本留白＝起日 0001-01-01（掃遍全部歷史）＋迄日「今天」（未來日期的單靜默消失），
        // 兩者畫面上都沒有任何訊號。今日走 IDateTimeService＝廠別當地日，不是伺服器時區（R22）。
        (fromDate, toDate) = ReportDateDefaults.Apply(fromDate, toDate, _dateTimeService.GetLocalDate());

        ViewBag.FromDate     = fromDate;
        ViewBag.ToDate       = toDate;
        ViewBag.WorkCenterId = workCenterId;
        ViewBag.CanPrint     = await _permissionService.CanPrint(MenuKey);
        ViewBag.WorkCenterOptions = await _context.WorkCenters
            .AsNoTracking().OrderBy(wc => wc.WorkCenterCode)
            .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
            .ToListAsync();

        if (!searched)
            return View(new List<ProductionEfficiencyRowVm>());

        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate, string? workCenterId)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var rows = await BuildQueryAsync(fromDate, toDate, workCenterId);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "生產效率分析" : "Production Efficiency",
            Headers = isZh
                ? ["工作中心", "派工數", "標準工時(min)", "實際工時(min)", "效率%"]
                : ["Work Center", "Dispatches", "Std Minutes", "Actual Minutes", "Efficiency%"],
            Rows = rows.Select(r => new object?[]
            {
                r.WorkCenterId, r.DispatchCount,
                r.StandardMinutes, r.ActualMinutes, r.EfficiencyRate
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"ProductionEfficiency_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<ProductionEfficiencyRowVm>> BuildQueryAsync(
        string? fromDate, string? toDate, string? workCenterId)
    {
        var plantId = _userContext.PlantId ?? "";
        // 分母改用實際報工時段（2026-10-27 使用者裁決）。原本用排程起訖：
        // 起點是計畫、終點卻在完工時被 ETA 重算改成完工時刻，提早開工的派工結束比開始還早，
        // 排程工時成了負數；且排程時段本來就不是「實際花了多久」。
        // 期間以完工時刻（最後一次下工）落在哪一天為準。上工時刻是真 UTC，期間界線要換成廠別當地日的 UTC 時刻。
        var today = _dateTimeService.GetLocalDate();
        var fromDay = DateOnly.TryParse(fromDate, out var f) ? f : today.AddDays(-30);
        // 起訖已由 ReportDateDefaults 補齊（R22），這是解析失敗的最後防線
        var toDay   = DateOnly.TryParse(toDate, out var t) ? t : today;
        var start = DateTime.SpecifyKind(_dateTimeService.ToUtcTime(
            DateTime.SpecifyKind(fromDay.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified)), DateTimeKind.Utc);
        var end   = DateTime.SpecifyKind(_dateTimeService.ToUtcTime(
            DateTime.SpecifyKind(toDay.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified)), DateTimeKind.Utc);

        var ids = await _context.ProductionSessions.AsNoTracking()
            .Where(s => s.PlantId == plantId && s.EndAt != null)
            .GroupBy(s => s.DispatchId)
            .Where(g => g.Max(s => s.EndAt) >= start && g.Max(s => s.EndAt) < end)
            .Select(g => g.Key)
            .ToListAsync();
        var completedInPeriod = (await _context.ProductionSessions.AsNoTracking()
                .Where(s => ids.Contains(s.DispatchId) && s.EndAt != null)
                .Select(s => new { s.DispatchId, s.StartAt, EndAt = s.EndAt!.Value })
                .ToListAsync())
            .GroupBy(s => s.DispatchId)
            .ToDictionary(g => g.Key, g => ActualMinutes(g.Select(s => (s.StartAt, s.EndAt))));

        var query =
            from d in _context.Dispatches
            join op in _context.WorkOrderOperations
                on new { d.WorkOrderId, d.OperationSeq }
                equals new { op.WorkOrderId, op.OperationSeq }
            where d.PlantId == plantId
               && d.Status == DispatchStatus.Closed
               && ids.Contains(d.DispatchId)
            select new
            {
                d.DispatchId,
                op.WorkCenterId,
                // 原單位欄位一併取出，標準工時的單位換算改在記憶體端以共用 helper 處理，
                // 避免在 SQL 投影寫死換算方向（原本非 Minute 一律 ×60，Second 應 ÷60，造成 3600 倍誤差）。
                op.UnitTime,
                op.SetupTime,
                op.TimeUnit,
                op.SetupTimeUnit,
                d.Quantity,
            };

        if (!string.IsNullOrWhiteSpace(workCenterId))
            query = query.Where(r => r.WorkCenterId == workCenterId);

        var raw = await query.AsNoTracking().ToListAsync();

        return raw
            .GroupBy(r => r.WorkCenterId)
            .Select(g => new ProductionEfficiencyRowVm
            {
                WorkCenterId     = g.Key,
                DispatchCount    = g.Count(),
                // Standard = 準備工時（SetupTimeUnit）＋單件工時×量（TimeUnit），兩者單位可不同，分別換算為分鐘
                StandardMinutes  = g.Sum(r => OperationTimeUnitExtensions.ToMinutes(r.SetupTime, r.SetupTimeUnit)
                                            + OperationTimeUnitExtensions.ToMinutes(r.UnitTime * r.Quantity, r.TimeUnit)),
                ActualMinutes    = Math.Round(g.Sum(r => completedInPeriod[r.DispatchId]), 1),
            })
            .OrderBy(r => r.WorkCenterId)
            .ToList();
    }

    /// <summary>
    /// 一張派工的實際工時：各次上工時段的**聯集**分鐘數。
    /// 生產線多人同時上工時各人的時段重疊，逐筆相加會隨人數加倍；中斷（下工到再上工）不算。
    /// </summary>
    internal static decimal ActualMinutes(IEnumerable<(DateTime Start, DateTime End)> sessions)
    {
        decimal total = 0;
        DateTime? curStart = null, curEnd = null;
        foreach (var (s, e) in sessions.Where(x => x.End > x.Start).OrderBy(x => x.Start))
        {
            if (curEnd == null || s > curEnd)
            {
                if (curEnd != null) total += (decimal)(curEnd.Value - curStart!.Value).TotalMinutes;
                (curStart, curEnd) = (s, e);
            }
            else if (e > curEnd) curEnd = e;
        }
        if (curEnd != null) total += (decimal)(curEnd.Value - curStart!.Value).TotalMinutes;
        return total;
    }
}
