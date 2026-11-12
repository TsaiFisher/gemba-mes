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
using System.Globalization;

namespace SmartFactory.MES.Modules.ACT.Controllers
{
    [Authorize]
    public class NrvAssessmentController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly INrvAssessmentService _nrvService;
        private readonly IPermissionService _permissionService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTimeService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;

        private const string MenuKey = "NrvAssessment";

        public NrvAssessmentController(
            ApplicationDbContext context, INrvAssessmentService nrvService,
            IPermissionService permissionService, IUserContext userContext,
            IExportService exportService, IDateTimeService dateTimeService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context; _nrvService = nrvService;
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

            if (!searched) return View((PaginatedList<InventoryNrvAssessment>?)null);

            var query = _context.InventoryNrvAssessments
                .Where(a => a.PlantId == _userContext.PlantId)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchPeriod))
                query = query.Where(a => a.PeriodId == searchPeriod);

            query = query.OrderByDescending(a => a.ImpairmentLoss);
            return View(await PaginatedList<InventoryNrvAssessment>.CreateAsync(
                query, pageNumber ?? 1, pageSize ?? 20));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(string periodId, decimal sellingCostRatio = 0.02m)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return Json(new { success = false, message = _localizer["NoUpdatePermission"].Value });
            try
            {
                var results = await _nrvService.RunAssessmentAsync(
                    periodId, _userContext.PlantId ?? "", sellingCostRatio);
                return Json(new { success = true, message = string.Format(_localizer["NrvBatchCompleted"].Value, results.Count) });
            }
            catch (Exception ex) { return Json(new { success = false, message = ex.Message }); }
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? searchPeriod, string format = "xlsx")
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();

            var query = _context.InventoryNrvAssessments
                .Where(a => a.PlantId == _userContext.PlantId)
                .AsNoTracking();
            if (!string.IsNullOrWhiteSpace(searchPeriod))
                query = query.Where(a => a.PeriodId == searchPeriod);

            var data = await query.OrderByDescending(a => a.ImpairmentLoss).ToListAsync();
            var isZh = CultureInfo.CurrentUICulture.Name != "en-US";
            var now  = _dateTimeService.Now;

            var req = new ExportRequest
            {
                Title = "NrvAssessment",
                Headers = isZh
                    ? ["期間", "品號", "在庫量", "帳面成本", "NRV 單價", "銷售費用", "NRV", "減損損失"]
                    : ["Period", "Product", "Qty", "CarryCost", "NrvPrice", "SellCost", "NRV", "Impairment"],
                Rows = data.Select(r => new object?[]
                { r.PeriodId, r.ProductCode, r.QtyOnHand, r.CarryingCost,
                  r.NrvPrice, r.SellingCost, r.NetRealizableValue, r.ImpairmentLoss })
            };

            return format switch
            {
                "csv" => File(_exportService.ToCsv(req), "text/csv",
                              Uri.EscapeDataString($"NRV_{now:yyyyMMdd}.csv")),
                _     => File(_exportService.ToExcel(req),
                              "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                              Uri.EscapeDataString($"NRV_{_userContext.PlantId}_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }
    }
}
