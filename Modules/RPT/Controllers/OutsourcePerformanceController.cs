using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Reporting;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    [Authorize]
    public class OutsourcePerformanceController : ModuleControllerBase
    {
        private readonly IOutsourcePerformanceService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly OutsourcePerformanceTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "OutsourcePerformance";

        public OutsourcePerformanceController(
            IOutsourcePerformanceService service,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            OutsourcePerformanceTool tool,
            IStringLocalizer<ProjectZReportHeaderResource> headers)
        {
            _service           = service;
            _permissionService = permissionService;
            _userContext       = userContext;
            _exportService     = exportService;
            _dateTimeService   = dateTimeService;
            _currencyService   = currencyService;
            _localizer         = localizer;
            _tool = tool;
            _headers = headers;
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index(
            string? fromDate, string? toDate, bool searched = false)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched = searched;
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate   = toDate;
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            if (!searched || string.IsNullOrWhiteSpace(fromDate) || string.IsNullOrWhiteSpace(toDate))
                return View((OutsourcePerformanceReport?)null);

            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
                return View((OutsourcePerformanceReport?)null);

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "");
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string fromDate, string toDate, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
                return BadRequest();

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "");
            var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now    = _dateTimeService.Now;

            var req = _tool.ToExportRequest(
                new OutsourcePerformanceRequest { PlantId = _userContext.PlantId },
                report.Rows, isZh ? "委外加工對帳與損耗分析" : "Outsource Reconciliation & Scrap", _headers);

            return format == "csv"
                ? File(_exportService.ToCsv(req), "text/csv",
                       Uri.EscapeDataString($"OSP_{fromDate}_{toDate}.csv"))
                : File(_exportService.ToExcel(req),
                       "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                       Uri.EscapeDataString($"OSP_{fromDate}_{toDate}_{now:yyyyMMdd_HHmm}.xlsx"));
        }
    }
}
