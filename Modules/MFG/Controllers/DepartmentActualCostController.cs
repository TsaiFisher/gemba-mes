using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    /// <summary>
    /// 部門（工作中心）當月實際費用維護（roadmap-fin-cst-sub.md §3.3 費率重估分子來源）。
    /// 月結時由會計逐工作中心輸入當月實際直接人工 / 製造費用，作為
    /// <see cref="SmartFactory.MES.Modules.MFG.Services.IActualRateReestimationService"/> 重估費率的分子。
    /// 與 MonthEnd 的 RunActualRateReestimation POST body 為同一張表的兩條輸入路徑，本畫面供獨立維護 / 重開帳重跑。
    /// </summary>
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class DepartmentActualCostController : ModuleControllerBase
    {
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private readonly ICurrencyService _currencyService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;
        private const string MenuKey = "DepartmentActualCost";

        public DepartmentActualCostController(
            IStringLocalizer<ProjectZSharedResource> localizer,
            ICurrencyService currencyService,
            IExportService exportService,
            IDateTimeService dateTime)
        {
            _localizer = localizer;
            _currencyService = currencyService;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        public async Task<IActionResult> Index(string? periodId, string? workCenterCode,
            int pageNumber = 1, int pageSize = 20, bool searched = false)
        {
            if (!await Permissions.CanRead(MenuKey)) return Forbid();

            ViewBag.PeriodId = periodId;
            ViewBag.WorkCenterCode = workCenterCode;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            await LoadFilterOptionsAsync();
            await PrepareIndexPermissionsAsync(MenuKey);

            if (!searched) return View((PaginatedList<DepartmentActualCost>?)null);

            var query = Db.Set<DepartmentActualCost>().AsNoTracking()
                .Where(d => d.PlantId == UserContext.PlantId);
            if (!string.IsNullOrEmpty(periodId))
                query = query.Where(d => d.PeriodId == periodId);
            if (!string.IsNullOrEmpty(workCenterCode))
                query = query.Where(d => d.WorkCenterCode == workCenterCode);

            query = query.OrderByDescending(d => d.PeriodId).ThenBy(d => d.WorkCenterCode);

            var list = await PaginatedList<DepartmentActualCost>.CreateAsync(query, pageNumber, pageSize);
            ViewBag.WorkCenterNames = await WorkCenterNamesAsync();
            return View(list);
        }

        public async Task<IActionResult> Create()
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return ValidationPartial("_Form", new DepartmentActualCost());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(DepartmentActualCost model)
        {
            if (!await Permissions.CanCreate(MenuKey)) return Forbid();

            await ValidateAsync(model, isEdit: false);
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return ValidationPartial("_Form", model);
            }

            await ApplyValuesAsync(model);
            model.PlantId = UserContext.PlantId;
            model.UpdatedAt = DateTime.UtcNow;

            Db.Set<DepartmentActualCost>().Add(model);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["CreateSuccess"].Value);
        }

        public async Task<IActionResult> Edit(int id)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            var rec = await Db.Set<DepartmentActualCost>().FindAsync(id);
            if (rec == null) return NotFound();
            await PrepareFormDropdowns();
            return ValidationPartial("_Form", rec);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, DepartmentActualCost model)
        {
            if (!await Permissions.CanUpdate(MenuKey)) return Forbid();
            if (id != model.Id) return NotFound();

            await ValidateAsync(model, isEdit: true);
            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return ValidationPartial("_Form", model);
            }

            var existing = await Db.Set<DepartmentActualCost>().FindAsync(id);
            if (existing == null) return NotFound();

            existing.PeriodId = model.PeriodId;
            existing.WorkCenterCode = model.WorkCenterCode;
            existing.ActualLaborCost = await RoundMoneyAsync(model.ActualLaborCost);
            existing.ActualOverheadCost = await RoundMoneyAsync(model.ActualOverheadCost);
            existing.UpdatedAt = DateTime.UtcNow;

            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["UpdateSuccess"].Value);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            if (!await Permissions.CanDelete(MenuKey)) return JsonFail(_localizer["NoDeletePermission"].Value);

            var rec = await Db.Set<DepartmentActualCost>().FindAsync(id);
            if (rec == null) return JsonFail(_localizer["RecordNotFound"].Value);

            Db.Set<DepartmentActualCost>().Remove(rec);
            await Db.SaveChangesAsync();
            return JsonOk(message: _localizer["DeleteSuccess"].Value);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? periodId, string? workCenterCode, string format = "xlsx")
        {
            if (!await Permissions.CanPrint(MenuKey)) return Forbid();

            var query = Db.Set<DepartmentActualCost>().AsNoTracking()
                .Where(d => d.PlantId == UserContext.PlantId);
            if (!string.IsNullOrEmpty(periodId))
                query = query.Where(d => d.PeriodId == periodId);
            if (!string.IsNullOrEmpty(workCenterCode))
                query = query.Where(d => d.WorkCenterCode == workCenterCode);

            var data = await query.OrderByDescending(d => d.PeriodId).ThenBy(d => d.WorkCenterCode).ToListAsync();
            var wcNames = await WorkCenterNamesAsync();
            var localNow = _dateTime.Now;

            var req = new ExportRequest
            {
                Title = _localizer["DepartmentActualCost"].Value,
                Headers = new[]
                {
                    _localizer["PeriodId"].Value,
                    _localizer["WorkCenterCode"].Value,
                    _localizer["ActualLaborCost"].Value,
                    _localizer["ActualOverheadCost"].Value,
                    _localizer["UpdatedAt"].Value,
                },
                Rows = data.Select(d => new object?[]
                {
                    d.PeriodId,
                    wcNames.TryGetValue(d.WorkCenterCode, out var n) ? $"{d.WorkCenterCode} — {n}" : d.WorkCenterCode,
                    d.ActualLaborCost,
                    d.ActualOverheadCost,
                    _dateTime.ToLocalTime(d.UpdatedAt).ToString("yyyy-MM-dd HH:mm"),
                })
            };

            return format switch
            {
                "csv" => File(_exportService.ToCsv(req), "text/csv",
                    Uri.EscapeDataString($"DepartmentActualCost_{localNow:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                    Uri.EscapeDataString($"DepartmentActualCost_{localNow:yyyyMMdd}.json")),
                _ => File(_exportService.ToExcel(req),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    Uri.EscapeDataString($"DepartmentActualCost_{UserContext.PlantId}_{localNow:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        // ── 私有輔助 ──

        /// <summary>新增/修改共用驗證：必填、金額非負、唯一鍵（廠別+期間+工作中心）。</summary>
        private async Task ValidateAsync(DepartmentActualCost model, bool isEdit)
        {
            if (model.ActualLaborCost < 0)
                ModelState.AddModelError(nameof(model.ActualLaborCost), _localizer["AmountMustBeNonNegative"].Value);
            if (model.ActualOverheadCost < 0)
                ModelState.AddModelError(nameof(model.ActualOverheadCost), _localizer["AmountMustBeNonNegative"].Value);

            if (!string.IsNullOrWhiteSpace(model.PeriodId) && !string.IsNullOrWhiteSpace(model.WorkCenterCode))
            {
                var dup = await Db.Set<DepartmentActualCost>().AsNoTracking()
                    .AnyAsync(d => d.PlantId == UserContext.PlantId
                                   && d.PeriodId == model.PeriodId
                                   && d.WorkCenterCode == model.WorkCenterCode
                                   && (!isEdit || d.Id != model.Id));
                if (dup)
                    ModelState.AddModelError(nameof(model.WorkCenterCode), _localizer["DepartmentActualCostDuplicate"].Value);
            }
        }

        private async Task ApplyValuesAsync(DepartmentActualCost model)
        {
            model.ActualLaborCost = await RoundMoneyAsync(model.ActualLaborCost);
            model.ActualOverheadCost = await RoundMoneyAsync(model.ActualOverheadCost);
        }

        private async Task<decimal> RoundMoneyAsync(decimal amount)
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            return string.IsNullOrEmpty(baseCurrency) ? amount : await _currencyService.RoundAsync(amount, baseCurrency);
        }

        private async Task<int> ResolveBaseCurrencyDpAsync()
        {
            var baseCurrency = await _currencyService.GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return 2;
            var map = await _currencyService.GetAllDecimalPlacesAsync();
            return map.TryGetValue(baseCurrency, out var dp) ? dp : 2;
        }

        private async Task<Dictionary<string, string>> WorkCenterNamesAsync() =>
            await Db.Set<WorkCenter>().AsNoTracking()
                .ToDictionaryAsync(wc => wc.WorkCenterCode, wc => wc.WorkCenterName);

        private async Task LoadFilterOptionsAsync()
        {
            ViewBag.PeriodOptions = await Db.Set<AccountingPeriod>().AsNoTracking()
                .OrderByDescending(p => p.PeriodId)
                .Select(p => p.PeriodId)
                .ToListAsync();
            ViewBag.WorkCenterOptions = await Db.Set<WorkCenter>().AsNoTracking()
                .Where(wc => wc.IsActive)
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();
        }

        private async Task PrepareFormDropdowns()
        {
            await LoadFilterOptionsAsync();
            ViewBag.BaseCurrencyDp = await ResolveBaseCurrencyDpAsync();
            await PrepareIndexPermissionsAsync(MenuKey);
        }
    }
}
