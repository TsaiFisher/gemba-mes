using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers;

[Authorize]
public class PurchaseOverdueController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly PurchaseOverdueTool  _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "PurchaseOverdue";

    public PurchaseOverdueController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        PurchaseOverdueTool tool,
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
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        int minOverdueDays = 1,
        bool searched = false)
    {
        // 動詞一律 CanRead（2026-08-14 裁決，覆寫 §8.1-終）：報表沒有「新增」語意，
        // 不得拿 CanCreate 當「收窄可見範圍」的代理；可見範圍改由 MenuPermission 逐角色表達。
        // ⚠️ 這是放寬——原僅該模組 Full 角色可見。理由與影響見 report-permission-inventory.md §四之三。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched       = searched;
        // 期間在本報表只是篩選（不是分母），所以留白給預設近 30 天而不是擋下來。
        // 原本留白＝起日 0001-01-01（掃遍全部歷史）＋迄日「今天」（未來日期的單靜默消失），
        // 兩者畫面上都沒有任何訊號。今日走 IDateTimeService＝廠別當地日，不是伺服器時區（R22）。
        (fromDate, toDate) = ReportDateDefaults.Apply(fromDate, toDate, _dateTimeService.GetLocalDate());

        ViewBag.FromDate       = fromDate;
        ViewBag.ToDate         = toDate;
        ViewBag.SearchSupplier = searchSupplier;
        ViewBag.SearchProduct  = searchProduct;
        ViewBag.MinOverdueDays = minOverdueDays;
        ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
        ViewBag.SupplierOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();
        ViewBag.ProductOptions = await _context.Products
            .AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.ProductCode)
            .Select(p => new { p.ProductCode, p.ProductName })
            .ToListAsync();

        if (!searched)
            return View(new List<PurchaseOverdueRowVm>());

        var (_, rows) = await BuildQueryAsync(fromDate, toDate, searchSupplier, searchProduct, minOverdueDays);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        int minOverdueDays = 1)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(fromDate, toDate, searchSupplier, searchProduct, minOverdueDays);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows, isZh ? "採購逾期跟催" : "Overdue Purchase Orders", _headers);

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"PurchaseOverdue_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 查詢邏輯已抽至 <see cref="PurchaseOverdueTool"/>（§5.4）。這裡只做參數轉換。
    /// <c>AsOfDate</c> 是「逾期幾天」的基準日，改走 <c>IDateTimeService.GetLocalDate()</c>：
    /// 原本取伺服器時區，與廠別時區不同時整批逾期天數會差一天（廠別在 UTC 以西時多算一天）。
    /// 這不是預設值不理想，是算出來的數字就錯了。
    /// </summary>
    private async Task<(PurchaseOverdueRequest Request, List<PurchaseOverdueRowVm> Rows)> BuildQueryAsync(
        string? fromDate, string? toDate,
        string? searchSupplier, string? searchProduct,
        int minOverdueDays = 1)
    {
        var request = new PurchaseOverdueRequest
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = LegacyQueryPeriod.Parse(fromDate, toDate,
                                 // 廠別當地日（R22）。查詢列的起訖已由 ReportDateDefaults 補齊，
                                 // 這裡是解析失敗時的最後防線。
                                 _dateTimeService.GetLocalDate()),
            AsOfDate       = _dateTimeService.GetLocalDate(),
            SupplierCode   = searchSupplier,
            ProductCode    = searchProduct,
            MinOverdueDays = minOverdueDays,
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }
}
