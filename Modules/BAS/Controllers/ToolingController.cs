using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Controllers
{
    [Authorize]
    public class ToolingController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ToolingMaster";

        public ToolingController(ApplicationDbContext context, IApprovalService approvalService,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _approvalService = approvalService;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        // GET: /Tooling
        public async Task<IActionResult> Index(
            string? searchTerm,
            int? pageNumber, int? pageSize,
            string? sortOrder,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.SearchTerm = searchTerm;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.CodeSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "code_asc" ? "code_desc" : "code_asc";
            ViewBag.NameSort = sortOrder == "name_asc" ? "name_desc" : "name_asc";

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            ViewBag.ToolingTypeNames = await _context.CodeEntries.AsNoTracking()
                .Where(e => e.Category == "tooling_type" && e.IsActive)
                .ToDisplayDictionaryAsync();

            if (!searched) return View((PaginatedList<Tooling>?)null);

            var query = _context.Toolings.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ToolingCode.ToLower().Contains(lower) ||
                    t.ToolingName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.ToolingCode),
                "name_asc"  => query.OrderBy(t => t.ToolingName),
                "name_desc" => query.OrderByDescending(t => t.ToolingName),
                _           => query.OrderBy(t => t.ToolingCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Tooling>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        private async Task PrepareFormDropdowns()
        {
            ViewBag.ToolingTypeOptions = await _context.CodeEntries
                .AsNoTracking()
                .Where(e => e.Category == "tooling_type" && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .ToDisplayOptionsAsync();
        }

        // GET: /Tooling/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            await PrepareFormDropdowns();
            var model = new Tooling { IsActive = true };
            if (IsAjax) return PartialView("_ToolingForm", model);
            return View("_ToolingForm", model);
        }

        // POST: /Tooling/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Tooling.ToolingCode), nameof(Tooling.ToolingName), nameof(Tooling.Spec),
            nameof(Tooling.CavityCount), nameof(Tooling.ToolingType),
            nameof(Tooling.ProductsPerCavity), nameof(Tooling.PmIntervalShots),
            nameof(Tooling.IsActive), nameof(Tooling.Remarks))] Tooling model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                await PrepareFormDropdowns();
                if (IsAjax) return PartialView("_ToolingForm", model);
                return View("_ToolingForm", model);
            }

            var exists = await _context.Toolings.AnyAsync(t => t.ToolingCode == model.ToolingCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.ToolingCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                await PrepareFormDropdowns();
                if (IsAjax) return PartialView("_ToolingForm", model);
                return View("_ToolingForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.Toolings.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Tooling/Edit/{toolingCode}
        [HttpGet]
        public async Task<IActionResult> Edit(string toolingCode)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Toolings.FindAsync(toolingCode);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            await PrepareFormDropdowns();
            if (IsAjax) return PartialView("_ToolingForm", rec);
            return View("_ToolingForm", rec);
        }

        // POST: /Tooling/Edit/{toolingCode}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string toolingCode, Tooling updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                await PrepareFormDropdowns();
                if (IsAjax) return PartialView("_ToolingForm", updated);
                return View("_ToolingForm", updated);
            }

            var rec = await _context.Toolings.FindAsync(toolingCode);
            if (rec == null) return NotFound();

            rec.ToolingName  = updated.ToolingName;
            rec.Spec         = updated.Spec;
            rec.CavityCount  = updated.CavityCount;
            rec.ToolingType  = updated.ToolingType;
            rec.ProductsPerCavity = updated.ProductsPerCavity;
            rec.PmIntervalShots   = updated.PmIntervalShots;   // AccumulatedShots 系統維護，不隨表單覆寫
            rec.Remarks      = updated.Remarks;
            rec.IsActive     = updated.IsActive;
            rec.UpdatedAt    = DateTime.UtcNow;

            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["SaveFailed"].Value });
                return StatusCode(500);
            }

            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // POST: /Tooling/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string toolingCode)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Toolings.FindAsync(toolingCode);
            if (rec != null)
            {
                // 途程模穴規劃、換模設置、派工、生產工時皆以 ToolingCode 鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免模具刪除後留下孤兒資料。
                if (await IsToolingInUseAsync(toolingCode))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["ToolingInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["ToolingInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.Toolings.Remove(rec);
                try { await _context.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["DeleteFailedReferenced"].Value });
                    return StatusCode(500);
                }
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        /// <summary>模具是否仍被途程工序、換模設置、派工或生產工時引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsToolingInUseAsync(string toolingCode)
        {
            if (await _context.RoutingOperations.AnyAsync(o => o.ToolingCode == toolingCode))
                return true;
            if (await _context.ProductionSetups.IgnoreQueryFilters().AnyAsync(s => s.ToolingCode == toolingCode))
                return true;
            if (await _context.Dispatches.IgnoreQueryFilters().AnyAsync(d => d.ToolingCode == toolingCode))
                return true;
            if (await _context.ProductionSessions.IgnoreQueryFilters().AnyAsync(s => s.ToolingCode == toolingCode))
                return true;
            return false;
        }

        // GET: /Tooling/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Toolings.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ToolingCode.ToLower().Contains(lower) ||
                    t.ToolingName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.ToolingCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Tooling.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["ToolingCode"].Value, L["ToolingName"].Value, L["Spec"].Value,
                L["CavityCount"].Value, L["ToolingType"].Value,
                L["AccumulatedShots"].Value, L["PmIntervalShots"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.ToolingCode, t.ToolingName, t.Spec, t.CavityCount, t.ToolingType,
                t.AccumulatedShots, t.PmIntervalShots, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Tooling" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Tooling.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Tooling.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Tooling.json"),
            };
        }
    }
}
