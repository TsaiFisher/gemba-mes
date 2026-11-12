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
    /// <summary>
    /// 多維度銷售毛利分析報表：依「客戶／品號／月份」維度彙總營收、銷貨成本、毛利與毛利率。
    /// </summary>
    [Authorize]
    public class MarginAnalysisController : ModuleControllerBase
    {
        private readonly IMarginAnalysisService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly MarginAnalysisTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "MarginAnalysis";

        public MarginAnalysisController(
            IMarginAnalysisService service,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            MarginAnalysisTool tool,
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
            string? fromDate, string? toDate, string dimension = "Product", bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched  = searched;
            ViewBag.FromDate  = fromDate;
            ViewBag.ToDate    = toDate;
            ViewBag.Dimension = dimension;
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            // 起訖留白時原本靜默回空報表——畫面上按了查詢什麼都沒發生，也沒有任何說明。
            // 區分「還沒查詢」與「查了但沒給期間」，後者要講出來。
            if (!searched)
                return View(new MarginAnalysisReport { Dimension = dimension });

            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
            {
                ViewBag.DateRangeRequired = true;
                return View(new MarginAnalysisReport { Dimension = dimension });
            }

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "", dimension);
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string fromDate, string toDate, string dimension = "Product")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
                return BadRequest();

            var report = await _service.GetReportAsync(from, to, _userContext.PlantId ?? "", dimension);
            var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now    = _dateTimeService.Now;

            var req = _tool.ToExportRequest(
                new MarginAnalysisRequest { PlantId = _userContext.PlantId, GroupBy = dimension },
                report.Rows, isZh ? "多維度銷售毛利分析" : "Multi-Dimension Margin Analysis", _headers);

            return File(_exportService.ToExcel(req),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Uri.EscapeDataString($"MarginAnalysis_{dimension}_{fromDate}_{toDate}_{now:yyyyMMdd_HHmm}.xlsx"));
        }
    }
}
