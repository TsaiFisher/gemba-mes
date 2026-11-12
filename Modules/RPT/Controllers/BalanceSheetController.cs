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
    public class BalanceSheetController : ModuleControllerBase
    {
        private readonly IBalanceSheetService _service;
        private readonly IProfitLossService _profitLossService;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICurrencyService _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly BalanceSheetTool _tool;
        private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

        private const string MenuKey = "BalanceSheet";

        public BalanceSheetController(
            IBalanceSheetService service,
            IProfitLossService profitLossService,
            IPermissionService permissionService,
            IUserContext userContext,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer,
            BalanceSheetTool tool,
            IStringLocalizer<ProjectZReportHeaderResource> headers)
        {
            _service           = service;
            _profitLossService = profitLossService;
            _permissionService = permissionService;
            _userContext       = userContext;
            _exportService     = exportService;
            _dateTimeService   = dateTimeService;
            _currencyService   = currencyService;
            _localizer         = localizer;
            _tool = tool;
            _headers = headers;
        }

        /// <summary>
        /// 截至該期的累計本期損益。
        ///
        /// <see cref="IBalanceSheetService"/> 刻意不做期末結轉（損益科目 4~8 不併入權益），
        /// 於是「資產 = 負債 + 權益」在這張報表上本來就不會成立——差額正是本期損益。
        /// 服務層那個決定是對的（沒有結轉分錄就不該假裝有），但畫面只擺三張合計卡，
        /// 看報表的人得到的是「資產 19,800、負債 12,600、權益 0」這種湊不起來的數字，
        /// 而且看不出少的是什麼。故由控制器補上第四個數，讓恆等式在畫面上是完整的。
        ///
        /// 資產負債表是累計到 asOfPeriod，所以損益也要從最早期間累計過來（起點給空字串，
        /// 服務層以字串比較篩選 PeriodId，空字串小於任何期別）。
        /// </summary>
        private async Task<decimal> ResolveNetProfitAsync(string asOfPeriod)
            => (await _profitLossService.GetReportAsync("", asOfPeriod, _userContext.PlantId ?? "")).NetProfit;

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        public async Task<IActionResult> Index(string? asOfPeriod, bool searched = false)
        {
            // 機密分級改由 MenuPermission 資料表達（見 RolePermissionSeeder.ConfidentialReports）；閘門動詞一律 CanRead。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched   = searched;
            ViewBag.AsOfPeriod = asOfPeriod;
            ViewBag.CanPrint   = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.PeriodOptions = await Db.AccountingPeriods
                .AsNoTracking()
                .OrderByDescending(p => p.PeriodId)
                .Select(p => p.PeriodId)
                .ToListAsync();

            if (!searched || string.IsNullOrWhiteSpace(asOfPeriod))
                return View((SmartFactory.MES.Modules.RPT.Models.BalanceSheetReport?)null);

            var report = await _service.GetReportAsync(asOfPeriod, _userContext.PlantId ?? "");
            ViewBag.NetProfit = await ResolveNetProfitAsync(asOfPeriod);
            return View(report);
        }

        [HttpGet]
        public async Task<IActionResult> Print(string asOfPeriod)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var report = await _service.GetReportAsync(asOfPeriod, _userContext.PlantId ?? "");
            ViewBag.NetProfit = await ResolveNetProfitAsync(asOfPeriod);
            var qrUrl  = Url.Action("Index", "BalanceSheet", null, Request.Scheme)!;
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            await PreparePrintViewBagAsync(qrUrl, Array.Empty<string>());
            return View("Print", report);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string asOfPeriod, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var report = await _service.GetReportAsync(asOfPeriod, _userContext.PlantId ?? "");
            var isZh   = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now    = _dateTimeService.Now;

            // 兩層攤平成一層，類別跟著每一列走——與工具的 GetExportRowsAsync 同一段邏輯
            // （類別的在地化改由工具查 resx，見 BalanceSheetTool.CategoryLabel）。
            var rows = report.Sections
                .SelectMany(s => s.Items.Select(i => new BalanceSheetExportRow(s.Category, i)))
                .ToList();

            var req = _tool.ToExportRequest(
                new BalanceSheetRequest { PlantId = _userContext.PlantId },
                rows,
                isZh ? $"資產負債表 截至 {asOfPeriod}" : $"Balance Sheet as of {asOfPeriod}",
                _headers);

            return format == "csv"
                ? File(_exportService.ToCsv(req), "text/csv",
                       Uri.EscapeDataString($"BS_{asOfPeriod}.csv"))
                : File(_exportService.ToExcel(req),
                       "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                       Uri.EscapeDataString($"BS_{asOfPeriod}_{now:yyyyMMdd_HHmm}.xlsx"));
        }
    }
}
