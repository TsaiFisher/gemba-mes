using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

/// <summary>
/// OEE（設備綜合效率）報表：依機台彙總可用率 × 表現性 × 良率。
/// 稼動時間取自上工紀錄、計畫時間取自工作日曆、產出/良率取自報工。
/// </summary>
[Authorize]
public class OeeController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IOeeService          _oeeService;
    private readonly IUomService          _uomService;

    private const string MenuKey = "Oee";

    public OeeController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IOeeService oeeService,
        IUomService uomService)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _oeeService        = oeeService;
        _uomService        = uomService;
    }

    public async Task<IActionResult> Index(
        string? machineCode, DateOnly? dateFrom, DateOnly? dateTo, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        var (from, to) = ResolveRange(dateFrom, dateTo);
        ViewBag.Searched    = searched;
        ViewBag.MachineCode = machineCode;
        ViewBag.DateFrom    = from;
        ViewBag.DateTo      = to;
        ViewBag.CanPrint    = await _permissionService.CanPrint(MenuKey);
        ViewBag.MachineOptions = await _context.WorkCenterMachines
            .AsNoTracking().OrderBy(m => m.MachineCode)
            .Select(m => new { m.MachineCode, m.MachineName })
            .Distinct()
            .ToListAsync();

        ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

        if (!searched)
            return View(new OeeReport());

        var report = await _oeeService.BuildAsync(
            _userContext.PlantId ?? "", from, to, machineCode);
        return View(report);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? machineCode, DateOnly? dateFrom, DateOnly? dateTo)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

        var (from, to) = ResolveRange(dateFrom, dateTo);
        var report = await _oeeService.BuildAsync(_userContext.PlantId ?? "", from, to, machineCode);

        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = new ExportRequest
        {
            Title   = isZh ? "OEE 設備綜合效率" : "OEE Report",
            Headers = isZh
                ? ["機台", "機台名稱", "品號", "品名", "計畫工時(分)", "稼動工時(分)", "理想工時(分)", "總產出", "良品",
                   "可用率%", "表現性%", "良率%", "OEE%"]
                : ["Machine", "Name", "Product", "ProductName", "Planned(min)", "Run(min)", "Ideal(min)", "Total", "Good",
                   "Availability%", "Performance%", "Quality%", "OEE%"],
            Rows = report.Rows.Select(r => new object?[]
            {
                r.MachineCode, r.MachineName, r.ProductCode, r.ProductName,
                r.PlannedMinutes, r.RunMinutes, r.IdealMinutes,
                r.TotalQty, r.GoodQty,
                Math.Round(r.Availability * 100m, 2), Math.Round(r.Performance * 100m, 2),
                Math.Round(r.Quality * 100m, 2), Math.Round(r.Oee * 100m, 2),
            }).ToList()
        };

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"Oee_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>預設區間：近 30 天。</summary>
    private (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(_dateTimeService.Now);
        var t = to ?? today;
        var f = from ?? t.AddDays(-30);
        if (f > t) (f, t) = (t, f);
        return (f, t);
    }
}
