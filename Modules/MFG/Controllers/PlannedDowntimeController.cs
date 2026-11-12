using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using System.Text.Json;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Controllers
{
    [Authorize]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class PlannedDowntimeController : ModuleControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserContext _userContext;
        private readonly IPermissionService _permissionService;
        private readonly IStringLocalizer<ProjectZSharedResource> _localizer;
        private const string MenuKey = "PlannedDowntime";

        public PlannedDowntimeController(
            ApplicationDbContext context,
            IUserContext userContext,
            IPermissionService permissionService,
            IStringLocalizer<ProjectZSharedResource> localizer)
        {
            _context = context;
            _userContext = userContext;
            _permissionService = permissionService;
            _localizer = localizer;
        }

        public async Task<IActionResult> Index(int? pageNumber = 1, DateOnly? fromDate = null, DateOnly? toDate = null,
            string? workCenterId = null, bool searched = false, int pageSize = 20)
        {
            if (!await _permissionService.CanRead(MenuKey)) return Forbid();

            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");
            ViewBag.WorkCenterId = workCenterId;
            ViewBag.Searched = searched;
            ViewBag.CurrentPageSize = pageSize;
            ViewBag.WorkCenterOptions = await _context.WorkCenters
                .AsNoTracking().OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new { wc.WorkCenterCode, wc.WorkCenterName })
                .ToListAsync();

            await SetPermissionViewBagAsync();
            if (!searched) return View((PaginatedList<PlannedDowntime>?)null);

            var query = _context.Set<PlannedDowntime>().AsNoTracking();

            if (fromDate.HasValue)
            {
                var from = fromDate.Value.ToDateTime(TimeOnly.MinValue);
                query = query.Where(p => p.EndTime >= from);
            }
            if (toDate.HasValue)
            {
                var to = toDate.Value.ToDateTime(TimeOnly.MaxValue);
                query = query.Where(p => p.StartTime <= to);
            }
            if (!string.IsNullOrEmpty(workCenterId))
                query = query.Where(p => p.WorkCenterId == workCenterId);

            query = query.OrderByDescending(p => p.StartTime).ThenBy(p => p.WorkCenterId).ThenBy(p => p.MachineCode);

            var list = await PaginatedList<PlannedDowntime>.CreateAsync(query, pageNumber ?? 1, pageSize);

            return View(list);
        }

        public async Task<IActionResult> Create()
        {
            if (!await _permissionService.CanCreate(MenuKey)) return Forbid();
            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", new PlannedDowntime()) : View("_Form", new PlannedDowntime());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(PlannedDowntime model)
        {
            if (!await _permissionService.CanCreate(MenuKey))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoCreatePermission"].Value)});</script>", "text/html") : Forbid();

            Normalize(model);
            ValidateRange(model);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            model.PlantId ??= _userContext.PlantId;
            model.CreatedAt = model.UpdatedAt = DateTime.UtcNow;

            _context.Set<PlannedDowntime>().Add(model);
            await _context.SaveChangesAsync();

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["CreateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        public async Task<IActionResult> Edit(int id)
        {
            if (!await _permissionService.CanUpdate(MenuKey)) return Forbid();
            var rec = await _context.Set<PlannedDowntime>().FindAsync(id);
            if (rec == null) return NotFound();

            await PrepareFormDropdowns();
            return IsAjax ? PartialView("_Form", rec) : View("_Form", rec);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, PlannedDowntime model)
        {
            if (!await _permissionService.CanUpdate(MenuKey))
                return IsAjax ? Content($"<script>alert({JsonSerializer.Serialize(_localizer["NoUpdatePermission"].Value)});</script>", "text/html") : Forbid();

            if (id != model.Id) return NotFound();

            Normalize(model);
            ValidateRange(model);

            if (!ModelState.IsValid)
            {
                await PrepareFormDropdowns();
                return IsAjax ? PartialView("_Form", model) : View("_Form", model);
            }

            var existing = await _context.Set<PlannedDowntime>().FindAsync(id);
            if (existing == null) return NotFound();

            existing.WorkCenterId = model.WorkCenterId;
            existing.MachineCode  = model.MachineCode;
            existing.StartTime    = model.StartTime;
            existing.EndTime      = model.EndTime;
            existing.DowntimeType = model.DowntimeType;
            existing.Reason       = model.Reason;
            existing.IsActive     = model.IsActive;
            existing.UpdatedAt    = DateTime.UtcNow;

            _context.Update(existing);
            await _context.SaveChangesAsync();

            if (IsAjax)
                return Content("<script>window.toastSuccess && window.toastSuccess(); refreshList && refreshList();</script>", "text/html");
            TempData["SuccessMessage"] = _localizer["UpdateSuccess"].Value;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            if (!await _permissionService.CanDelete(MenuKey))
                return IsAjax ? Json(new { success = false, message = _localizer["NoDeletePermission"].Value }) : Forbid();

            var rec = await _context.Set<PlannedDowntime>().FindAsync(id);
            if (rec == null)
            {
                if (IsAjax) return Json(new { success = false, message = _localizer["RecordNotFound"].Value });
                return NotFound();
            }

            _context.Set<PlannedDowntime>().Remove(rec);
            await _context.SaveChangesAsync();

            var message = _localizer["DeleteSuccess"].Value;
            if (IsAjax) return Json(new { success = true, message });
            TempData["SuccessMessage"] = message;
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private static void Normalize(PlannedDowntime model)
        {
            model.WorkCenterId = string.IsNullOrWhiteSpace(model.WorkCenterId) ? null : model.WorkCenterId;
            model.MachineCode  = string.IsNullOrWhiteSpace(model.MachineCode)  ? null : model.MachineCode;
            // datetime-local 綁定為 Unspecified；對齊 timestamp without time zone
            model.StartTime = DateTime.SpecifyKind(model.StartTime, DateTimeKind.Unspecified);
            model.EndTime   = DateTime.SpecifyKind(model.EndTime, DateTimeKind.Unspecified);
        }

        private void ValidateRange(PlannedDowntime model)
        {
            // 工作中心或機台至少擇一（全廠停機應走 SpecialDay/日曆，mfg-domain.md §產能模型）
            if (string.IsNullOrWhiteSpace(model.WorkCenterId) && string.IsNullOrWhiteSpace(model.MachineCode))
                ModelState.AddModelError(nameof(model.MachineCode), _localizer["WorkCenterOrMachineRequired"].Value);
            if (model.EndTime <= model.StartTime)
                ModelState.AddModelError(nameof(model.EndTime), _localizer["EndMustAfterStart"].Value);
        }

        private async Task PrepareFormDropdowns()
        {
            // 工作中心 + 其機台（排除已廢棄）；MachinesByWc 供畫面依工作中心篩選機台
            var wcs = await _context.Set<SmartFactory.MES.Modules.BAS.Models.WorkCenter>()
                .AsNoTracking()
                .Where(wc => wc.IsActive)
                .OrderBy(wc => wc.WorkCenterCode)
                .Select(wc => new
                {
                    wc.WorkCenterCode,
                    wc.WorkCenterName,
                    Machines = wc.Machines
                        .Where(m => m.MachineStatus != SmartFactory.MES.Modules.BAS.Models.MachineStatus.Retired)
                        .OrderBy(m => m.MachineCode)
                        .Select(m => new { m.MachineCode, m.MachineName })
                        .ToList()
                })
                .ToListAsync();

            ViewBag.WorkCenters = wcs.Select(w => new { w.WorkCenterCode, w.WorkCenterName }).ToList();
            ViewBag.MachinesByWc = wcs.ToDictionary(
                w => w.WorkCenterCode,
                w => w.Machines.Select(m => new { code = m.MachineCode, name = m.MachineName }).ToList());

            // 停機類型 → 代碼維護（CodeEntry: downtime_type）
            ViewBag.DowntimeTypeOptions = await _context.CodeEntries
                .AsNoTracking()
                .Where(e => e.Category == "downtime_type" && e.IsActive)
                .OrderBy(e => e.SortOrder)
                .ToDisplayOptionsAsync();

            await SetPermissionViewBagAsync();
        }

        private async Task SetPermissionViewBagAsync()
        {
            ViewBag.CanCreate = await _permissionService.CanCreate(MenuKey);
            ViewBag.CanUpdate = await _permissionService.CanUpdate(MenuKey);
            ViewBag.CanDelete = await _permissionService.CanDelete(MenuKey);
            ViewBag.CanPrint  = await _permissionService.CanPrint(MenuKey);
        }
    }
}
