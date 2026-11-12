using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT.Controllers
{
    [Authorize]
    public class EclController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IEclService _eclService;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "EclAllowance";

        public EclController(
            ApplicationDbContext context, IEclService eclService,
            IPermissionService permissionService, IUserContext userContext,
            IExportService exportService, IDateTimeService dateTimeService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _eclService = eclService;
            _permissionService = permissionService; _userContext = userContext;
            _exportService = exportService; _dateTimeService = dateTimeService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(
            string? searchPeriod, bool searched = false, int? pageNumber = 1, int? pageSize = 20)
        {
            // 列表是讀取動作，守 CanRead。原本守 CanCreate——而選單本身是用 CanRead 過濾的，
            // 於是「有讀取權、沒有建立權」的角色**看得到選單、點下去 403**。
            // 實測 ProjectZ 庫：「管理人員」對其中 8 支正是這個組合。
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Searched        = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.SearchPeriod    = searchPeriod;
            ViewBag.CanUpdate       = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanPrint        = await _permissionService.CanPrint(MenuKey);
            ViewBag.PeriodOptions   = await _context.AccountingPeriods
                .AsNoTracking()
                .OrderByDescending(p => p.PeriodId)
                .Select(p => p.PeriodId)
                .ToListAsync();

            // 損失率設定 (供 UI 展示)
            ViewBag.LossRates = await _context.EclLossRates
                .Where(r => r.PlantId == _userContext.PlantId || r.PlantId == null)
                .OrderBy(r => r.AgingDaysFrom)
                .AsNoTracking().ToListAsync();

            if (!searched) return View((PaginatedList<EclAllowance>?)null);

            var query = _context.EclAllowances
                .Where(a => a.PlantId == _userContext.PlantId)
                .AsNoTracking();
            if (!string.IsNullOrWhiteSpace(searchPeriod))
                query = query.Where(a => a.PeriodId == searchPeriod);

            query = query.OrderBy(a => a.PeriodId).ThenBy(a => a.AgingBucket);
            return View(await PaginatedList<EclAllowance>.CreateAsync(
                query, pageNumber ?? 1, pageSize ?? 20));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(string periodId)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                // 同 MonthEnd.RunEcl：期末減損基準日以廠別當地日為準，不得用 UTC；且取該期期末。
                var plantId   = _userContext.PlantId ?? "";
                var periodEnd = await _context.AccountingPeriods.AsNoTracking()
                    .Where(p => p.PeriodId == periodId && p.PlantId == plantId)
                    .Select(p => (DateOnly?)p.EndDate).FirstOrDefaultAsync();
                var asOfDate  = EclService.AsOfFor(periodEnd, _dateTimeService.GetLocalDate());
                var results   = await _eclService.ComputeEclAsync(periodId, plantId, asOfDate);
                var total    = results.Sum(r => r.EclAmount);
                return Json(new { success = true, message = string.Format(_localizer["EclBatchCompleted"].Value, total.ToString("N2")) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }
    }
}
