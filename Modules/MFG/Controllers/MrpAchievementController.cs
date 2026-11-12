using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Reporting;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers;

[Authorize]
public class MrpAchievementController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly MrpAchievementTool _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "MrpAchievement";

    public MrpAchievementController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IUomService uomService,
        MrpAchievementTool tool,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _uomService        = uomService;
        _tool              = tool;
        _localizer         = localizer;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? mrpRunId, string? searchProduct, bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.MrpRunId      = mrpRunId;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        // Recent completed MRP runs for dropdown
        ViewBag.MrpRuns = await _context.MrpRuns
            .AsNoTracking()
            .Where(r => r.PlantId == (_userContext.PlantId ?? "")
                     && r.Status == MrpRunStatus.Executed)
            .OrderByDescending(r => r.ExecutedAt)
            .Take(20)
            .Select(r => new { r.MrpRunId, r.ExecutedAt })
            .ToListAsync();

        ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

        if (!searched)
            return View(new List<MrpAchievementRowVm>());

        var (_, rows) = await BuildQueryAsync(mrpRunId, searchProduct);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? mrpRunId, string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(mrpRunId, searchProduct);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows, isZh ? "MRP 達成率" : "MRP Achievement Rate", _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"MrpAchievement_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>查詢邏輯已抽至 <see cref="MrpAchievementTool"/>（§5.4）。這裡只做參數轉換。</summary>
    private async Task<(MrpAchievementRequest Request, List<MrpAchievementRowVm> Rows)> BuildQueryAsync(
        string? mrpRunId, string? searchProduct)
    {
        var request = new MrpAchievementRequest
        {
            PlantId     = _userContext.PlantId,
            MrpRunId    = mrpRunId,
            ProductCode = searchProduct,
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }
}
