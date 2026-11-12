using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Controllers;

[Authorize]
public class DeliveryRateController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly DeliveryRateTool     _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "DeliveryRate";

    public DeliveryRateController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        DeliveryRateTool tool,
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
        string? searchCustomer,
        bool searched = false)
    {
        // 動詞一律 CanRead（2026-08-14 裁決，覆寫 §8.1-終）：報表沒有「新增」語意，
        // 不得拿 CanCreate 當「收窄可見範圍」的代理；可見範圍改由 MenuPermission 逐角色表達。
        // ⚠️ 這是放寬——原僅該模組 Full 角色可見。理由與影響見 report-permission-inventory.md §四之三。
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();
        ViewBag.Searched        = searched;
        // 期間在本報表只是篩選（不是分母），所以留白給預設近 30 天而不是擋下來。
        // 原本留白＝起日 0001-01-01（掃遍全部歷史）＋迄日「今天」（未來日期的單靜默消失），
        // 兩者畫面上都沒有任何訊號。今日走 IDateTimeService＝廠別當地日，不是伺服器時區（R22）。
        (fromDate, toDate) = ReportDateDefaults.Apply(fromDate, toDate, _dateTimeService.GetLocalDate());

        ViewBag.FromDate        = fromDate;
        ViewBag.ToDate          = toDate;
        ViewBag.SearchCustomer  = searchCustomer;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.CustomerOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsCustomer && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();

        if (!searched)
            return View(new List<DeliveryRateRowVm>());

        var (_, rows) = await BuildQueryAsync(fromDate, toDate, searchCustomer);
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? fromDate, string? toDate, string? searchCustomer)
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var (request, rows) = await BuildQueryAsync(fromDate, toDate, searchCustomer);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        var req = _tool.ToExportRequest(request, rows, isZh ? "訂單交期達成率" : "Delivery Rate", _headers);
        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"DeliveryRate_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    /// <summary>
    /// 查詢邏輯已抽至 <see cref="DeliveryRateTool"/>（§5.4）。這裡只做參數轉換。
    /// 迄日留白時退回 <c>DateTime.Today</c>＝維持現行（伺服器時區）語意。
    /// </summary>
    private async Task<(DeliveryRateRequest Request, List<DeliveryRateRowVm> Rows)> BuildQueryAsync(
        string? fromDate, string? toDate, string? searchCustomer)
    {
        var request = new DeliveryRateRequest
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = LegacyQueryPeriod.Parse(fromDate, toDate,
                                 // 廠別當地日（R22）。查詢列的起訖已由 ReportDateDefaults 補齊，
                                 // 這裡是解析失敗時的最後防線。
                                 _dateTimeService.GetLocalDate()),
            CustomerCode   = searchCustomer,
        };

        var rows = await _tool.ExecuteAsync(request, User);
        return (request, rows.ToList());
    }
}
