using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Reporting;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    [Authorize]
    public class ProfitLossController : ModuleControllerBase
    {
        private readonly IProfitLossService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ProfitLossTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "ProfitLoss";

        public ProfitLossController(
            IProfitLossService service,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            ProfitLossTool tool,
            IStringLocalizer<ProjectZReportHeaderResource> headers)
        {
            _service = service;
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
            string? fromPeriod, string? toPeriod, bool searched = false)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched    = searched;
            ViewBag.FromPeriod  = fromPeriod;
            ViewBag.ToPeriod    = toPeriod;
            ViewBag.CanPrint    = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.PeriodOptions = await Db.AccountingPeriods
                .AsNoTracking()
                .OrderByDescending(p => p.PeriodId)
                .Select(p => p.PeriodId)
                .ToListAsync();

            if (!searched || string.IsNullOrWhiteSpace(fromPeriod) || string.IsNullOrWhiteSpace(toPeriod))
                return View((SmartFactory.MES.Modules.RPT.Models.ProfitLossReport?)null);

            var report = await _service.GetReportAsync(fromPeriod, toPeriod, _userContext.PlantId ?? "");
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string fromPeriod, string toPeriod)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var report = await _service.GetReportAsync(fromPeriod, toPeriod, _userContext.PlantId ?? "");
            var qrUrl  = Url.Action("Index", "ProfitLoss", null, Request.Scheme)!;
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            await PreparePrintViewBagAsync(qrUrl, Array.Empty<string>());
            return View("Print", report);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string fromPeriod, string toPeriod, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var report = await _service.GetReportAsync(fromPeriod, toPeriod, _userContext.PlantId ?? "");
            var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now    = _dateTimeService.Now;

            var req = _tool.ToExportRequest(
                new ProfitLossRequest { PlantId = _userContext.PlantId },
                report.Lines, isZh ? "損益表" : "Profit & Loss", _headers);

            return format == "csv"
                ? File(_exportService.ToCsv(req), "text/csv",
                       Uri.EscapeDataString($"PL_{fromPeriod}_{toPeriod}.csv"))
                : File(_exportService.ToExcel(req),
                       "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                       Uri.EscapeDataString($"PL_{fromPeriod}_{toPeriod}_{now:yyyyMMdd_HHmm}.xlsx"));
        }
    }
}
