using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.PUR.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.PUR.Controllers;

/// <summary>
/// 供應商交期達成率（OTD，pur-domain.md §5.3/§5.5）：
/// 廠商×月份兩個維度的準時率、平均/最大逾期天數；含委外來源與廠商延誤排名。
/// </summary>
[Authorize]
public class SupplierDeliveryRateController : ModuleControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserContext         _userContext;
    private readonly IPermissionService   _permissionService;
    private readonly IExportService       _exportService;
    private readonly IDateTimeService     _dateTimeService;
    private readonly SupplierDeliveryRateTool _tool;
    private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    private const string MenuKey = "SupplierDeliveryRate";

    public SupplierDeliveryRateController(
        ApplicationDbContext context,
        IUserContext userContext,
        IPermissionService permissionService,
        IExportService exportService,
        IDateTimeService dateTimeService,
        SupplierDeliveryRateTool tool,
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
        string? searchSupplier, string source = "All",
        bool searched = false)
    {
        if (!await _permissionService.CanRead(MenuKey)) return Forbid();

        ViewBag.Searched        = searched;
        // 期間在本報表只是篩選（不是分母），所以留白給預設近 30 天而不是擋下來。
        // 原本留白＝起日 0001-01-01（掃遍全部歷史）＋迄日「今天」（未來日期的單靜默消失），
        // 兩者畫面上都沒有任何訊號。今日走 IDateTimeService＝廠別當地日，不是伺服器時區（R22）。
        (fromDate, toDate) = ReportDateDefaults.Apply(fromDate, toDate, _dateTimeService.GetLocalDate());

        ViewBag.FromDate        = fromDate;
        ViewBag.ToDate          = toDate;
        ViewBag.SearchSupplier  = searchSupplier;
        ViewBag.Source          = source;
        ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
        ViewBag.SupplierOptions = await _context.Companies
            .AsNoTracking().Where(c => c.IsSupplier && c.IsActive)
            .OrderBy(c => c.CompanyCode)
            .Select(c => new { c.CompanyCode, c.CompanyName })
            .ToListAsync();

        if (!searched)
            return View(new SupplierDeliveryRateVm());

        var vm = await BuildReportAsync(fromDate, toDate, searchSupplier, source);
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        string? fromDate, string? toDate,
        string? searchSupplier, string source = "All",
        string view = "supplier")
    {
        if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
        var vm   = await BuildReportAsync(fromDate, toDate, searchSupplier, source);
        var now  = _dateTimeService.Now;
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";

        // 欄頭只有一份：明細（供應商表）走工具宣告的欄位，月趨勢的欄頭同樣取自
        // ProjectZReportHeaderResource，不在這裡拼硬編的雙語陣列（§16.1 S-5）。
        ExportRequest req;
        if (view == "month")
        {
            req = new ExportRequest
            {
                Title   = isZh ? "交期達成率月趨勢" : "OTD Monthly Trend",
                Headers =
                [
                    _headers["SupplierDeliveryRate.YearMonth"],
                    _headers["SupplierDeliveryRate.TotalLines"],
                    _headers["SupplierDeliveryRate.OnTimeLines"],
                    _headers["SupplierDeliveryRate.LateLines"],
                    _headers["SupplierDeliveryRate.OnTimeRate"],
                    _headers["SupplierDeliveryRate.AvgOverdueDays"],
                    _headers["SupplierDeliveryRate.MaxOverdueDays"],
                ],
                Rows = vm.MonthRows.Select(r => new object?[]
                {
                    r.YearMonth, r.TotalLines, r.OnTimeLines, r.LateLines,
                    r.OnTimeRate, r.AvgOverdueDays, r.MaxOverdueDays
                }).ToList()
            };
        }
        else
        {
            req = _tool.ToExportRequest(
                BuildRequest(fromDate, toDate, searchSupplier, source),
                vm.SupplierRows,
                isZh ? "供應商交期達成率" : "Supplier Delivery Rate",
                _headers);
        }

        return File(_exportService.ToExcel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Uri.EscapeDataString($"SupplierDeliveryRate_{view}_{now:yyyyMMdd_HHmm}.xlsx"));
    }

    private Task<SupplierDeliveryRateVm> BuildReportAsync(
        string? fromDate, string? toDate, string? searchSupplier, string source)
        => _tool.ExecuteAsync(BuildRequest(fromDate, toDate, searchSupplier, source), User);

    /// <summary>
    /// 畫面參數 → 工具請求。日期在 <c>Index</c>／<c>Export</c> 進來前已由
    /// <c>ReportDateDefaults</c> 補過預設值；這裡的 <c>default</c> 退路是解析失敗的最後防線，
    /// 與抽出前 <c>BuildReportAsync</c> 的行為相同。
    /// </summary>
    private SupplierDeliveryRateRequest BuildRequest(
        string? fromDate, string? toDate, string? searchSupplier, string source)
    {
        DateOnly.TryParse(fromDate, out var start);
        DateOnly.TryParse(toDate,   out var end);
        if (end == default) end = _dateTimeService.GetLocalDate();

        return new SupplierDeliveryRateRequest
        {
            PlantId        = _userContext.PlantId,
            AbsolutePeriod = new DateRange(start, end),
            SupplierCode   = searchSupplier,
            Source         = source,
            AsOfDate       = _dateTimeService.GetLocalDate(),
        };
    }
}
