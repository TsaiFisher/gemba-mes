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
    public class ShiftController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IApprovalService _approvalService;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "ShiftMaster";

        public ShiftController(ApplicationDbContext context, IApprovalService approvalService,
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


        // GET: /Shift
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

            if (!searched) return View((PaginatedList<Shift>?)null);

            var query = _context.Shifts.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ShiftCode.ToLower().Contains(lower) ||
                    t.ShiftName.ToLower().Contains(lower));
            }

            query = sortOrder switch
            {
                "code_desc" => query.OrderByDescending(t => t.ShiftCode),
                "name_asc"  => query.OrderBy(t => t.ShiftName),
                "name_desc" => query.OrderByDescending(t => t.ShiftName),
                _           => query.OrderBy(t => t.ShiftCode),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<Shift>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /Shift/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new Shift { IsActive = true };
            if (IsAjax) return PartialView("_ShiftForm", model);
            return View("_ShiftForm", model);
        }

        // POST: /Shift/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(
            nameof(Shift.ShiftCode), nameof(Shift.ShiftName), nameof(Shift.StartTime), nameof(Shift.EndTime),
            nameof(Shift.BreakStart), nameof(Shift.BreakEnd), nameof(Shift.WorkMinutes), nameof(Shift.IsActive))] Shift model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ShiftForm", model);
                return View("_ShiftForm", model);
            }

            var exists = await _context.Shifts.AnyAsync(t => t.ShiftCode == model.ShiftCode);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.ShiftCode), _localizer["DuplicateCode"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_ShiftForm", model);
                return View("_ShiftForm", model);
            }

            // 淨工時由上下班時間扣午休伺服端推算（避免手填漏扣休息）；無時間時保留原值
            model.WorkMinutes = Shift.ComputeWorkMinutes(model.StartTime, model.EndTime, model.BreakStart, model.BreakEnd)
                                ?? model.WorkMinutes;

            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.Shifts.Add(model);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["CreateFailedDuplicate"].Value });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /Shift/Edit/{shiftCode}
        [HttpGet]
        public async Task<IActionResult> Edit(string shiftCode)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Shifts.FindAsync(shiftCode);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_ShiftForm", rec);
            return View("_ShiftForm", rec);
        }

        // POST: /Shift/Edit/{shiftCode}
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string shiftCode, Shift updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_ShiftForm", updated);
                return View("_ShiftForm", updated);
            }

            var rec = await _context.Shifts.FindAsync(shiftCode);
            if (rec == null) return NotFound();

            rec.ShiftName    = updated.ShiftName;
            rec.StartTime    = updated.StartTime;
            rec.EndTime      = updated.EndTime;
            rec.BreakStart   = updated.BreakStart;
            rec.BreakEnd     = updated.BreakEnd;
            rec.WorkMinutes  = Shift.ComputeWorkMinutes(updated.StartTime, updated.EndTime, updated.BreakStart, updated.BreakEnd)
                               ?? updated.WorkMinutes;
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

        // POST: /Shift/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string shiftCode)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            var rec = await _context.Shifts.FindAsync(shiftCode);
            if (rec != null)
            {
                _context.Shifts.Remove(rec);
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

        // GET: /Shift/Export
        public async Task<IActionResult> Export(string format = "xlsx", string? searchTerm = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.Shifts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(t =>
                    t.ShiftCode.ToLower().Contains(lower) ||
                    t.ShiftName.ToLower().Contains(lower));
            }

            var data = await query.OrderBy(t => t.ShiftCode).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.Shift.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["ShiftCode"].Value, L["ShiftName"].Value, L["StartTime"].Value,
                L["EndTime"].Value, L["WorkMinutes"].Value, L["IsActive"].Value
            };
            var rows = data.Select(t => new object?[]
            {
                t.ShiftCode, t.ShiftName,
                t.StartTime?.ToString("HH:mm"),
                t.EndTime?.ToString("HH:mm"),
                t.WorkMinutes, t.IsActive
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "Shift" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Shift.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "Shift.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "Shift.json"),
            };
        }
    }
}
