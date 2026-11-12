using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.INV.ViewModels;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Controllers;

[Authorize]
public class StockShortageController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly StockShortageTool    _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "StockShortage";

    public StockShortageController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        StockShortageTool tool,
        IStringLocalizer<ProjectZSharedResource> localizer,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _context           = context;
        _userContext       = userContext;
        _permissionService = permissionService;
        _exportService     = exportService;
        _dateTimeService   = dateTimeService;
        _tool              = tool;
        _localizer         = localizer;
        _headers           = headers;
    }

    public async Task<IActionResult> Index(
        string? searchProduct, bool searched = false, string? byDate = null)
    {
        // 動詞一律 CanRead（2026-08-14 裁決，覆寫 §8.1-終）：報表沒有「新增」語意，
        // 不得拿 CanCreate 當「收窄可見範圍」的代理；可見範圍改由 MenuPermission 逐角色表達。
        // ⚠️ 這是放寬——原僅該模組 Full 角色可見。理由與影響見 report-permission-inventory.md §四之三。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.ByDate        = ResolveByDate(byDate).ToString("yyyy-MM-dd");
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<StockShortageRowVm>());

        var (_, rows) = await BuildQueryAsync(searchProduct, byDate);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? searchProduct, string? byDate = null)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(searchProduct, byDate);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows, isZh ? "缺貨分析" : "Stock Shortage Analysis", _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"StockShortage_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>查詢本體已抽至 <see cref="StockShortageTool"/>，此處僅做畫面參數的轉接。</summary>
    private async Task<(StockShortageRequest Request, List<StockShortageRowVm> Rows)> BuildQueryAsync(string? searchProduct, string? byDate)
    {
        var request = new StockShortageRequest
        {
            PlantId = _userContext.PlantId,
            Keyword = searchProduct,
            ByDate  = ResolveByDate(byDate),
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }

    /// <summary>在途截止日：留白＝今天（廠別當地日）起 <see cref="StockShortageTool.DefaultHorizonDays"/> 天。</summary>
    private DateOnly ResolveByDate(string? byDate) =>
        DateOnly.TryParse(byDate, out var d) ? d : _dateTimeService.GetLocalDate().AddDays(StockShortageTool.DefaultHorizonDays);
}
