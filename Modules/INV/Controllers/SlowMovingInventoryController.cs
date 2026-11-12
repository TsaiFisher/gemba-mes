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
public class SlowMovingInventoryController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly SlowMovingInventoryTool _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "SlowMovingInventory";

    // 門檻與掃描上限的唯一定義在工具端，此處僅轉引用，避免兩邊各自漂移。
    private const int DefaultIdleDays  = SlowMovingInventoryTool.DefaultIdleDays;
    private const int MaxInventoryScan = SlowMovingInventoryTool.MaxInventoryScan;

    public SlowMovingInventoryController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        SlowMovingInventoryTool tool,
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
        int? idleDays, string? searchProduct, bool searched = false)
    {
        // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched      = searched;
        ViewBag.IdleDays      = idleDays ?? DefaultIdleDays;
        ViewBag.SearchProduct = searchProduct;
        ViewBag.CanPrint      = await _permissionService.CanPrint(MenuKey);
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<SlowMovingInventoryRowVm>());

        var (_, rows, tooMany) = await BuildQueryAsync(idleDays ?? DefaultIdleDays, searchProduct);
        ViewBag.TooMany   = tooMany;
        ViewBag.ScanLimit = MaxInventoryScan;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(int? idleDays, string? searchProduct)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var threshold = idleDays ?? DefaultIdleDays;
        var (request, rows, tooMany) = await BuildQueryAsync(threshold, searchProduct);
        if (tooMany)
            return RedirectToAction(nameof(Index),
                new { idleDays = threshold, searchProduct, searched = true });
        var now       = _dateTimeService.Now;
        var isZh      = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows,
            isZh ? $"呆滯庫存（{threshold}天以上）" : $"Slow-Moving Inventory (>{threshold}d)", _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SlowMoving_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>查詢本體已抽至 <see cref="SlowMovingInventoryTool"/>，此處僅做畫面參數的轉接。</summary>
    private async Task<(SlowMovingInventoryRequest Request,
                        List<SlowMovingInventoryRowVm> Rows, bool TooMany)> BuildQueryAsync(
        int idleDaysThreshold, string? searchProduct)
    {
        var request = new SlowMovingInventoryRequest
        {
            PlantId           = _userContext.PlantId,
            IdleDaysThreshold = idleDaysThreshold,
            ProductCode       = searchProduct,
        };

        var result = await _tool.ExecuteAsync(request, User);
        return (request, result.Rows.ToList(), result.TooMany);
    }
}
