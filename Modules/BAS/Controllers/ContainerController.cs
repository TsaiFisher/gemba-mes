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
    public class ContainerController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ContainerMaster";

        public ContainerController(ApplicationDbContext context, IApprovalService approvalService,
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


        // GET: /Container
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

            if (!searched) return View((PaginatedList<Container>?)null);

            var query = _context.Containers.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ContainerCode.ToLower().Contains(lower) ||
                    t.ContainerName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.ContainerCode),
                "name_asc"  => query.OrderBy(t => t.ContainerName),
                "name_desc" => query.OrderByDescending(t => t.ContainerName),
                _           => query.OrderBy(t => t.ContainerCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Container>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Container/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Container { IsActive = true };
            if (IsAjax) return PartialView("_ContainerForm", model);
            return View("_ContainerForm", model);
        }

        // POST: /Container/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Container.ContainerCode), nameof(Container.ContainerName), nameof(Container.Spec),
            nameof(Container.MaxCapacity), nameof(Container.IsActive))] Container model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ContainerForm", model);
                return View("_ContainerForm", model);
            }

            var exists = await _context.Containers.AnyAsync(t => t.ContainerCode == model.ContainerCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.ContainerCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ContainerForm", model);
                return View("_ContainerForm", model);
            }

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.Containers.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Container/Edit/{containerCode}
        [HttpGet]
        public async Task<IActionResult> Edit(string containerCode)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Containers.FindAsync(containerCode);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ContainerForm", rec);
            return View("_ContainerForm", rec);
        }

        // POST: /Container/Edit/{containerCode}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string containerCode, Container updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ContainerForm", updated);
                return View("_ContainerForm", updated);
            }

            var rec = await _context.Containers.FindAsync(containerCode);
            if (rec == null) return NotFound();

            rec.ContainerName = updated.ContainerName;
            rec.Spec          = updated.Spec;
            rec.MaxCapacity   = updated.MaxCapacity;
            rec.IsActive      = updated.IsActive;
            rec.UpdatedAt     = DateTime.UtcNow;

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

        // POST: /Container/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string containerCode)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Containers.FindAsync(containerCode);
            if (rec != null)
            {
                // 途程開工/完工容器設定、報工容器紀錄皆以 ContainerCode 鬆散字串參照（無 DB 層 FK 可攔截），
                // 主動查核，避免容器刪除後留下孤兒資料。
                if (await IsContainerInUseAsync(containerCode))
                {
                    if (IsAjax) return Json(new { success = false, message = _localizer["ContainerInUseCannotDelete"].Value });
                    TempData["ErrorMessage"] = _localizer["ContainerInUseCannotDelete"].Value;
                    return RedirectToAction(nameof(Index), new { searched = true });
                }

                _context.Containers.Remove(rec);
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

        /// <summary>容器是否仍被途程開工/完工容器設定或報工容器紀錄引用中；跨廠檢查（IgnoreQueryFilters）。</summary>
        private async Task<bool> IsContainerInUseAsync(string containerCode)
        {
            if (await _context.RoutingOperations.AnyAsync(o =>
                    o.ContainerInCode == containerCode || o.ContainerOutCode == containerCode))
                return true;
            if (await _context.ProductionReportContainers.AnyAsync(c => c.ContainerCode == containerCode))
                return true;
            if (await _context.ProductionReports.IgnoreQueryFilters().AnyAsync(r => r.ContainerCode == containerCode))
                return true;
            return false;
        }

        // GET: /Container/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Containers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ContainerCode.ToLower().Contains(lower) ||
                    t.ContainerName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.ContainerCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Container.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["ContainerCode"].Value, L["ContainerName"].Value, L["Spec"].Value,
                L["MaxCapacity"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.ContainerCode, t.ContainerName, t.Spec, t.MaxCapacity, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Container" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Container.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Container.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Container.json"),
            };
        }
    }
}
