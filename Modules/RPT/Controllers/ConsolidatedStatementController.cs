using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT.Controllers
{
    /// <summary>
    /// 合併財報（跨廠）。
    ///
    /// <para><b>為什麼需要它</b>：本系統的會計期間、月結清單、資產負債表、損益表全部依廠別分開
    /// ——那是對的，各廠的月結進度本來就可以不同。但多廠屬同一法人時，對外要出的是一套帳，
    /// 而在這支之前系統出不了那張表。</para>
    ///
    /// <para><b>為什麼是獨立畫面而不是在既有財報上加一個「全公司」選項</b>：
    /// 合併資料的可見範圍比使用者本廠大，那是權限問題不是顯示選項。獨立 MenuKey
    /// 才能讓「看得到本廠財報」與「看得到全公司財報」分開授權。</para>
    ///
    /// <para><b>廠際往來不需要沖銷分錄</b>：跨廠調撥兩腿一借一貸（撥出廠 借 1450／貸 1401，
    /// 撥入廠 借 1401／貸 1450），加總即歸零。而且調撥**按成本搬、不加價**
    /// （<c>InventoryPostingService</c> 進出兩腿用同一個 UnitCost），所以也沒有未實現內部損益
    /// 要消除——那通常是合併報表最難的一塊。加總之後 1450 還剩下的就是在途存貨。</para>
    /// </summary>
    [Authorize]
    public class ConsolidatedStatementController : ModuleControllerBase
    {
        private readonly ApplicationDbContext  _context;
        private readonly IBalanceSheetService  _balanceSheet;
        private readonly IProfitLossService    _profitLoss;
        private readonly IPermissionService    _permissionService;
        private readonly IExportService        _exportService;
        private readonly IDateTimeService      _dateTimeService;
        private readonly ICurrencyService      _currencyService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "ConsolidatedStatement";

        public ConsolidatedStatementController(
            ApplicationDbContext context,
            IBalanceSheetService balanceSheet,
            IProfitLossService profitLoss,
            IPermissionService permissionService,
            IExportService exportService,
            IDateTimeService dateTimeService,
            ICurrencyService currencyService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context           = context;
            _balanceSheet      = balanceSheet;
            _profitLoss        = profitLoss;
            _permissionService = permissionService;
            _exportService     = exportService;
            _dateTimeService   = dateTimeService;
            _currencyService   = currencyService;
            _localizer         = localizer;
        }

        public async Task<IActionResult> Index(
            string? asOfPeriod, string? fromPeriod, string[]? plantIds, bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            var allPlants = (await _context.Plants.AsNoTracking()
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.Name, p.NameEn })
                .ToListAsync())
                .Select(p => new { p.Id, Name = SmartFactory.MES.Modules._Shared.PlantDisplay.Name(p.Name, p.NameEn, p.Id) })
                .ToList();

            ViewBag.Searched       = searched;
            ViewBag.CanPrint       = await _permissionService.CanPrint(MenuKey);
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            ViewBag.PlantOptions   = allPlants;
            // 期間清單刻意繞過廠別過濾：合併畫面要列的是**全公司有哪些期別**，
            // 只列本廠的話，操作者在甲廠時選不到乙廠才建的期間。
            ViewBag.PeriodOptions  = await _context.AccountingPeriods.IgnoreQueryFilters()
                .AsNoTracking()
                .Select(p => p.PeriodId)
                .Distinct()
                .OrderByDescending(p => p)
                .ToListAsync();

            // 預設全選：合併報表的預設語意就是「全公司」，
            // 預設空集合會讓第一次進畫面看到一張全是 0 的報表。
            var selected = (plantIds is { Length: > 0 })
                ? plantIds.Where(id => allPlants.Any(p => p.Id == id)).ToList()
                : allPlants.Select(p => p.Id).ToList();
            ViewBag.SelectedPlants = selected;
            ViewBag.AsOfPeriod     = asOfPeriod;
            ViewBag.FromPeriod     = fromPeriod;

            if (!searched || string.IsNullOrWhiteSpace(asOfPeriod) || selected.Count == 0)
                return View((ConsolidatedStatementVm?)null);

            // 損益期間：起期留白時從有史以來累計到 asOfPeriod——與單廠資產負債表
            // 計算本期損益的做法一致（服務層以字串比較篩 PeriodId，空字串小於任何期別）。
            var from = string.IsNullOrWhiteSpace(fromPeriod) ? "" : fromPeriod;

            var bs = await _balanceSheet.GetConsolidatedReportAsync(asOfPeriod, selected);
            var pl = await _profitLoss.GetConsolidatedReportAsync(from, asOfPeriod, selected);

            var vm = new ConsolidatedStatementVm
            {
                AsOfPeriod   = asOfPeriod,
                FromPeriod   = from,
                BalanceSheet = bs,
                ProfitLoss   = pl,
                // 本期損益一律從有史以來累計（與單廠畫面同口徑）；
                // 使用者指定起期時，上面那份 pl 只是區間損益，兩者不可混用。
                NetProfit    = string.IsNullOrWhiteSpace(fromPeriod)
                    ? pl.NetProfit
                    : (await _profitLoss.GetConsolidatedReportAsync("", asOfPeriod, selected)).NetProfit,
                InterPlantInTransit = InTransitOf(bs),
                Plants = await BuildPlantStatusAsync(selected, asOfPeriod, allPlants
                    .ToDictionary(p => p.Id, p => p.Name)),
            };

            return View(vm);
        }

        /// <summary>
        /// 合併後 1450 的餘額＝在途存貨。取自報表而不是另外查一次資料庫，
        /// 兩邊各查一次就會在「哪些廠、哪個期間」這兩個條件上慢慢分岔。
        /// </summary>
        private static decimal InTransitOf(BalanceSheetReport bs)
            => bs.Sections
                 .SelectMany(s => s.Items)
                 .Where(i => i.AccountCode == Modules.ACT.StandardAccounts.InterPlantClearingCode)
                 .Sum(i => i.Balance);

        private async Task<List<PlantCloseStatusVm>> BuildPlantStatusAsync(
            IReadOnlyCollection<string> plantIds, string periodId,
            IReadOnlyDictionary<string, string> names)
        {
            // IgnoreQueryFilters：期間是 IPlantEntity，不繞過的話只看得到操作者當下那一廠，
            // 而這張表整個重點就是「各廠現在分別是什麼狀態」。
            var rows = await _context.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PeriodId == periodId && plantIds.Contains(p.PlantId!))
                .Select(p => new { p.PlantId, p.Status })
                .ToListAsync();

            var byPlant = rows.ToDictionary(r => r.PlantId!, r => r.Status);

            return plantIds.Select(id => new PlantCloseStatusVm
            {
                PlantId   = id,
                PlantName = names.TryGetValue(id, out var n) ? n : id,
                // 查無期間列＝那一廠沒建這個期間。不補預設狀態：
                // 「沒建期間」與「已開帳」在治理上是兩回事（前者連過帳閘門都不生效）。
                Status    = byPlant.TryGetValue(id, out var st) ? st : null,
            }).ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Export(
            string asOfPeriod, string? fromPeriod, string[]? plantIds, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var allPlantIds = await _context.Plants.AsNoTracking().Select(p => p.Id).ToListAsync();
            var selected = (plantIds is { Length: > 0 })
                ? plantIds.Where(allPlantIds.Contains).ToList()
                : allPlantIds;
            if (selected.Count == 0) return BadRequest(_localizer["Consolidated_NoPlant"].Value);

            var bs   = await _balanceSheet.GetConsolidatedReportAsync(asOfPeriod, selected);
            var pl   = await _profitLoss.GetConsolidatedReportAsync(
                           string.IsNullOrWhiteSpace(fromPeriod) ? "" : fromPeriod, asOfPeriod, selected);
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var now  = _dateTimeService.Now;

            // 兩張表攤成一份：第一欄標明是哪一張，讓匯出檔自己說得清楚是什麼。
            var rows = bs.Sections
                .SelectMany(s => s.Items.Select(i => new object?[]
                {
                    isZh ? "資產負債表" : "Balance Sheet",
                    s.Category, i.AccountCode, i.AccountName, i.Balance,
                }))
                .Concat(pl.Lines.Select(l => new object?[]
                {
                    isZh ? "損益表" : "Profit & Loss",
                    l.Category, l.AccountCode, l.AccountName, l.Amount,
                }))
                .ToList();

            var req = new ExportRequest
            {
                Title   = isZh ? $"合併財報 截至 {asOfPeriod}" : $"Consolidated statements as of {asOfPeriod}",
                Headers = isZh
                    ? ["報表", "分段", "科目代碼", "科目名稱", "金額"]
                    : ["Statement", "Section", "Account", "Name", "Amount"],
                Rows = rows,
            };

            return format?.ToLower() switch
            {
                "csv" => File(_exportService.ToCsv(req), "text/csv",
                    Uri.EscapeDataString($"Consolidated_{asOfPeriod}.csv")),
                _ => File(_exportService.ToExcel(req),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    Uri.EscapeDataString($"Consolidated_{asOfPeriod}_{now:yyyyMMdd_HHmm}.xlsx")),
            };
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }
    }
}
