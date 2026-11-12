using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Reporting;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    [Authorize]
    public class GrossMarginController : ModuleControllerBase
    {
        private readonly IGrossMarginService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly GrossMarginTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "GrossMargin";

        public GrossMarginController(
            IGrossMarginService service,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            GrossMarginTool tool,
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

            ViewBag.Searched  = searched;
            ViewBag.FromDate  = fromDate;
            ViewBag.ToDate    = toDate;
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            // 起訖留白時原本靜默回空報表——畫面上按了查詢什麼都沒發生，也沒有任何說明。
            // 區分「還沒查詢」與「查了但沒給期間」，後者要講出來。
            if (!searched)
                return View((SmartFactory.MES.Modules.RPT.Models.GrossMarginReport?)null);

            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
            {
                ViewBag.DateRangeRequired = true;
                return View((SmartFactory.MES.Modules.RPT.Models.GrossMarginReport?)null);
            }

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "");
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string fromDate, string toDate)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
                return BadRequest();

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "");
            var qrUrl  = Url.Action("Index", "GrossMargin", null, Request.Scheme)!;
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            await PreparePrintViewBagAsync(qrUrl, Array.Empty<string>());
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate   = toDate;
            return View("Print", report);
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
                new GrossMarginRequest { PlantId = _userContext.PlantId },
                report.Rows, isZh ? "毛利分析" : "Gross Margin", _headers);

            return format == "csv"
                ? File(_exportService.ToCsv(req), "text/csv",
                       Uri.EscapeDataString($"GM_{fromDate}_{toDate}.csv"))
                : File(_exportService.ToExcel(req),
                       "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                       Uri.EscapeDataString($"GM_{fromDate}_{toDate}_{now:yyyyMMdd_HHmm}.xlsx"));
        }
    }
}
