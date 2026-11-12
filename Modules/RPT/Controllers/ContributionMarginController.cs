using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;
using static SmartFactory.MES.Modules.RPT.Services.ContributionMarginCalculator;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Reporting;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    /// <summary>
    /// 產品邊際貢獻分析報表：以工單成本要素推變動占比，計算營收−變動成本＝邊際貢獻。
    /// 變動/固定分類預設存於系統參數，可於查詢列單次覆寫，或「儲存為預設」持久化。
    /// </summary>
    [Authorize]
    public class ContributionMarginController : ModuleControllerBase
    {
        private readonly IContributionMarginService _service;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ContributionMarginTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "ContributionMargin";

        public ContributionMarginController(
            IContributionMarginService service,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            ContributionMarginTool tool,
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
            string? fromDate, string? toDate,
            bool? matVar, bool? laborVar, bool? ohVar, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched = searched;
            ViewBag.FromDate = fromDate;
            ViewBag.ToDate   = toDate;
            ViewBag.CanPrint = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();

            var policy = await ResolvePolicyAsync(matVar, laborVar, ohVar, searched);

            // 起訖留白時原本靜默回空報表——畫面上按了查詢什麼都沒發生，也沒有任何說明。
            // 區分「還沒查詢」與「查了但沒給期間」，後者要講出來。
            if (!searched || !DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
            {
                if (searched) ViewBag.DateRangeRequired = true;
                return View(new ContributionMarginReport
                {
                    MaterialVariable = policy.Material, LaborVariable = policy.Labor, OverheadVariable = policy.Overhead,
                });
            }

            var report = await _service.BuildAsync(from, to, _userContext.PlantId ?? "", policy);
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Export(
            string fromDate, string toDate, bool? matVar, bool? laborVar, bool? ohVar)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(fromDate, out var from) || !DateOnly.TryParse(toDate, out var to))
                return BadRequest();

            var policy = await ResolvePolicyAsync(matVar, laborVar, ohVar, searched: true);
            var report = await _service.BuildAsync(from, to, _userContext.PlantId ?? "", policy);
            var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now    = _dateTimeService.Now;

            var req = _tool.ToExportRequest(
                new ContributionMarginRequest { PlantId = _userContext.PlantId },
                report.Rows, isZh ? "產品邊際貢獻分析" : "Product Contribution Margin", _headers);

            return File(_exportService.ToExcel(req),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Uri.EscapeDataString($"ContributionMargin_{fromDate}_{toDate}_{now:yyyyMMdd_HHmm}.xlsx"));
        }

        /// <summary>將目前的變動成本分類儲存為系統預設（管理者可調）。</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDefaults(bool? matVar, bool? laborVar, bool? ohVar)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return JsonFail(_localizer["NoPermission"].Value);

            await _service.SaveDefaultPolicyAsync(
                new VariablePolicy(matVar ?? false, laborVar ?? false, ohVar ?? false));
            return JsonOk(message: _localizer["SavedAsDefault"].Value);
        }

        /// <summary>解析變動成本分類：未查詢時用系統預設；查詢時以核選狀態為準（未勾＝false）。</summary>
        private async Task<VariablePolicy> ResolvePolicyAsync(bool? matVar, bool? laborVar, bool? ohVar, bool searched)
        {
            if (!searched)
                return await _service.GetDefaultPolicyAsync();
            return new VariablePolicy(matVar ?? false, laborVar ?? false, ohVar ?? false);
        }
    }
}
