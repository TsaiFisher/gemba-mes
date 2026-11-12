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
    public class SpecialDayController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IExportService _exportService;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;

        private const string MenuKey = "SpecialDayMaster";

        public SpecialDayController(ApplicationDbContext context,
            IUserContext userContext, IExportService exportService, IPermissionService permissionService,
            IStringLocalizerFactory localizerFactory)
        {
            _context = context;
            _userContext = userContext;
            _exportService = exportService;
            _permissionService = permissionService;
            _localizerFactory = localizerFactory;
            _localizer = localizerFactory.Create(typeof(ProjectZSharedResource));
        }


        // GET: /SpecialDay
        public async Task<IActionResult> Index(
            int? year, string? dayType,
            int? pageNumber, int? pageSize,
            string? sortOrder, string? viewMode,
            bool searched = false)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.Year     = year ?? DateTime.Today.Year;
            ViewBag.DayType  = dayType;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.ViewMode = viewMode ?? "list";  // list | calendar
            ViewBag.DateSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "date_asc" ? "date_desc" : "date_asc";
            ViewBag.TypeSort = sortOrder == "type_asc" ? "type_desc" : "type_asc";
            ViewBag.CurrentYear = year ?? DateTime.Today.Year;

            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);

            if (!searched) return View((PaginatedList<SpecialDay>?)null);

            var query = _context.SpecialDays.AsNoTracking()
                .Where(s => s.Year == (year ?? DateTime.Today.Year));

            if (!string.IsNullOrEmpty(dayType))
                query = query.Where(s => s.DayType == dayType);

            query = sortOrder switch
            {
                "date_desc" => query.OrderByDescending(s => s.Date),
                "type_asc"  => query.OrderBy(s => s.DayType).ThenBy(s => s.Date),
                "type_desc" => query.OrderByDescending(s => s.DayType).ThenBy(s => s.Date),
                _           => query.OrderBy(s => s.Date),
            };

            var ps = pageSize ?? 20;
            return View(await PaginatedList<SpecialDay>.CreateAsync(query, pageNumber ?? 1, ps));
        }

        // GET: /SpecialDay/CalendarData?year=2025&month=1
        [HttpGet]
        public async Task<IActionResult> CalendarData(int year, int month)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var from = new DateOnly(year, month, 1);
            var to   = from.AddMonths(1).AddDays(-1);

            var days = await _context.SpecialDays.AsNoTracking()
                .Where(s => s.Date >= from && s.Date <= to)
                .OrderBy(s => s.Date)
                .Select(s => new {
                    date        = s.Date.ToString("yyyy-MM-dd"),
                    dayType     = s.DayType,
                    description = s.Description
                })
                .ToListAsync();

            return Json(days);
        }

        // GET: /SpecialDay/CalendarDataYear?year=2025
        [HttpGet]
        public async Task<IActionResult> CalendarDataYear(int year)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();
            var days = await _context.SpecialDays
                .AsNoTracking()
                .Where(s => s.Year == year)
                .Select(s => new {
                    date    = s.Date.ToString("yyyy-MM-dd"),
                    dayType = s.DayType,
                    name    = s.Description
                })
                .ToListAsync();
            return Json(days);
        }

        // GET: /SpecialDay/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            ViewBag.IsEdit = false;
            var model = new SpecialDay { Year = DateTime.Today.Year };
            if (IsAjax) return PartialView("_SpecialDayForm", model);
            return View("_SpecialDayForm", model);
        }

        // POST: /SpecialDay/Create
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(SpecialDay model)
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_SpecialDayForm", model);
                return View("_SpecialDayForm", model);
            }

            var exists = await _context.SpecialDays.AnyAsync(s => s.Date == model.Date);
            if (exists)
            {
                ModelState.AddModelError(nameof(model.Date), _localizer["DuplicateRecord"].Value);
                ViewBag.IsEdit = false;
                if (IsAjax) return PartialView("_SpecialDayForm", model);
                return View("_SpecialDayForm", model);
            }

            model.Year      = model.Date.Year;
            model.CreatedAt = DateTime.UtcNow;
            model.UpdatedAt = DateTime.UtcNow;
            _context.SpecialDays.Add(model);
            await _context.SaveChangesAsync();

            if (IsAjax) return Json(new { success = true, message = _localizer["CreateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true, year = model.Year });
        }

        // GET: /SpecialDay/Edit?date=2025-01-01
        [HttpGet]
        public async Task<IActionResult> Edit(string date)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(date, out var d)) return NotFound();
            var rec = await _context.SpecialDays.FirstOrDefaultAsync(s => s.Date == d);
            if (rec == null) return NotFound();

            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_SpecialDayForm", rec);
            return View("_SpecialDayForm", rec);
        }

        // POST: /SpecialDay/Edit
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string date, SpecialDay updated)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            if (!ModelState.IsValid)
            {
                ViewBag.IsEdit = true;
                if (IsAjax) return PartialView("_SpecialDayForm", updated);
                return View("_SpecialDayForm", updated);
            }

            if (!DateOnly.TryParse(date, out var d)) return NotFound();
            var rec = await _context.SpecialDays.FirstOrDefaultAsync(s => s.Date == d);
            if (rec == null) return NotFound();

            rec.DayType                = updated.DayType;
            rec.Description            = updated.Description;
            rec.CompensatoryDayOfWeek  = updated.CompensatoryDayOfWeek;
            rec.UpdatedAt              = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            if (IsAjax) return Json(new { success = true, message = _localizer["UpdateSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true, year = rec.Year });
        }

        // POST: /SpecialDay/Delete
        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string date)
        {
            if (!await _permissionService.CanDelete(MenuKey)) return Forbid();
            if (!DateOnly.TryParse(date, out var d)) return NotFound();
            var rec = await _context.SpecialDays.FirstOrDefaultAsync(s => s.Date == d);
            if (rec != null)
            {
                _context.SpecialDays.Remove(rec);
                await _context.SaveChangesAsync();
            }
            if (IsAjax) return Json(new { success = true, message = _localizer["DeleteSuccess"].Value });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        // GET: /SpecialDay/Export
        public async Task<IActionResult> Export(string format = "xlsx", int? year = null, string? dayType = null)
        {
            if (!await _permissionService.CanPrint(MenuKey)) return Forbid();
            var query = _context.SpecialDays.AsNoTracking().AsQueryable();
            if (year.HasValue) query = query.Where(s => s.Year == year.Value);
            if (!string.IsNullOrEmpty(dayType)) query = query.Where(s => s.DayType == dayType);

            var data = await query.OrderBy(s => s.Date).ToListAsync();
            var L = _localizerFactory.Create(
                "Modules.BAS.Views.SpecialDay.Index",
                GetType().Assembly.GetName().Name!);
            var headers = new[]
            {
                L["Date"].Value, L["DayType"].Value, L["Description"].Value,
                L["CompensatoryDayOfWeek"].Value, L["Year"].Value
            };
            var rows = data.Select(s => new object?[]
            {
                s.Date.ToString("yyyy-MM-dd"), s.DayType, s.Description, s.CompensatoryDayOfWeek, s.Year
            }).ToList();

            var req = new ExportRequest { Headers = headers, Rows = rows, Title = "SpecialDay" };
            return format?.ToLower() switch
            {
                "xlsx"  => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "SpecialDay.xlsx"),
                "csv"   => File(_exportService.ToCsv(req), "text/csv", "SpecialDay.csv"),
                _       => File(_exportService.ToJson(req), "application/json", "SpecialDay.json"),
            };
        }
    }
}
