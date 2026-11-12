using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Controllers;

[Authorize]
public class SalesTrendController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

    private const string MenuKey = "SalesTrend";

    public SalesTrendController(
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

    public async Task<IActionResult> Index(int? year, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();
        var refYear = year ?? _dateTimeService.Now.Year;
        ViewBag.Searched = searched;
        ViewBag.Year     = refYear;
        ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);

        if (!searched)
            return View(new List<SalesTrendRowVm>());

        var rows = await BuildQueryAsync(refYear);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(int? year)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var refYear = year ?? _dateTimeService.Now.Year;
        var rows = await BuildQueryAsync(refYear);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? $"銷售同期比較（{refYear}年）" : $"Sales Trend {refYear}",
            Headers = isZh
                ? ["年月", "銷售金額", "上月金額", "MoM%", "去年同月金額", "YoY%"]
                : ["Year-Month", "Amount", "Prev Month", "MoM%", "Same Month LY", "YoY%"],
            Rows = rows.Select(r => new object?[]
            {
                r.YearMonth, r.Amount,
                r.PrevMonthAmount, r.MomPct.HasValue ? $"{r.MomPct:F2}%" : "-",
                r.SameMonthLastYearAmount, r.YoyPct.HasValue ? $"{r.YoyPct:F2}%" : "-"
            }).ToList()
        };
        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SalesTrend_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private async Task<List<SalesTrendRowVm>> BuildQueryAsync(int refYear)
    {
        var plantId = _userContext.PlantId ?? "";
        var startYear = refYear - 1;
        var startDate = new DateOnly(startYear, 1, 1);
        var endDate   = new DateOnly(refYear, 12, 31);

        var raw = await (
            from sol in _context.SalesOrderLines
            join so  in _context.SalesOrders on sol.SalesOrderId equals so.SalesOrderId
            where so.PlantId == plantId
               && so.OrderDate >= startDate && so.OrderDate <= endDate
               && so.ApprovalStatus != DocumentApprovalStatus.Draft
            select new { so.OrderDate.Year, so.OrderDate.Month, sol.LineAmount }
        ).AsNoTracking().ToListAsync();

        var monthly = raw
            .GroupBy(r => (r.Year, r.Month))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.LineAmount));

        var rows = new List<SalesTrendRowVm>();
        for (var m = 1; m <= 12; m++)
        {
            monthly.TryGetValue((refYear, m), out var cur);
            decimal? prev = null;
            if (m > 1 && monthly.TryGetValue((refYear, m - 1), out var pv)) prev = pv;
            decimal? ly = monthly.TryGetValue((refYear - 1, m), out var lyv) ? lyv : null;

            rows.Add(new SalesTrendRowVm
            {
                Year                     = refYear,
                Month                    = m,
                Amount                   = cur,
                PrevMonthAmount          = prev,
                MomPct                   = prev.HasValue && prev.Value != 0
                                           ? Math.Round((cur - prev.Value) / prev.Value * 100, 2) : null,
                SameMonthLastYearAmount  = ly,
                YoyPct                   = ly.HasValue && ly.Value != 0
                                           ? Math.Round((cur - ly.Value) / ly.Value * 100, 2) : null,
            });
        }
        return rows;
    }
}
