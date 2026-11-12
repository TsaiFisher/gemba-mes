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
public class WorkOrderProgressController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly IUomService          _uomService;
    private readonly WorkOrderProgressTool _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "WorkOrderProgress";

    public WorkOrderProgressController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        IUomService uomService,
        WorkOrderProgressTool tool,
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
        string? searchProduct, string? fromDate, string? toDate,
        string? filterStatus, bool searched = false)
    {
        // 動詞一律 CanRead（2026-08-14 裁決，覆寫 §8.1-終）：報表沒有「新增」語意，
        // 不得拿 CanCreate 當「收窄可見範圍」的代理；可見範圍改由 MenuPermission 逐角色表達。
        // ⚠️ 這是放寬——原僅該模組 Full 角色可見。理由與影響見 report-permission-inventory.md §四之三。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.FromDate      = fromDate;
        ViewBag.ToDate        = toDate;
        ViewBag.FilterStatus  = filterStatus;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        ViewBag.UomDecimals = await _uomService.GetAllDecimalPlacesAsync();

        if (!searched)
            return View(new List<WorkOrderProgressRow>());

        var (_, rows) = await BuildQueryAsync(searchProduct, fromDate, toDate, filterStatus);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? searchProduct, string? fromDate, string? toDate, string? filterStatus)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(searchProduct, fromDate, toDate, filterStatus);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows, isZh ? "工單進度報表" : "Work Order Progress", _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"WorkOrderProgress_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 查詢邏輯已抽至 <see cref="WorkOrderProgressTool"/>（§5.4）。這裡只做參數轉換。
    /// 兩個日期界線**各自獨立**傳入（解析失敗就不傳），逾期基準顯式傳 <c>DateTime.UtcNow</c>
    /// ＝維持現行行為；工具自己的預設也是 UtcNow，但可被排程與測試覆寫。
    /// </summary>
    private async Task<(WorkOrderProgressRequest Request, List<WorkOrderProgressRow> Rows)> BuildQueryAsync(
        string? searchProduct, string? fromDate, string? toDate, string? filterStatus)
    {
        var request = new WorkOrderProgressRequest
        {
            PlantId        = _userContext.PlantId,
            ProductKeyword = searchProduct,
            Status         = filterStatus,
            PlannedEndFrom = DateTime.TryParse(fromDate, out var f) ? DateOnly.FromDateTime(f) : null,
            PlannedEndTo   = DateTime.TryParse(toDate,   out var t) ? DateOnly.FromDateTime(t) : null,
            AsOfInstant    = DateTime.UtcNow,
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }
}
