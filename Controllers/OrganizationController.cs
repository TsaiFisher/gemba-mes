using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    [RequireActiveAdmin]   // 只看作用中角色（見 AdminAccess）
    public class OrganizationController : Controller
    {
        private readonly SmartFactory.MES.Data.ApplicationDbContext _context;
        private readonly SmartFactory.MES.Services.IPermissionService _permissionService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public OrganizationController(SmartFactory.MES.Data.ApplicationDbContext context,
            SmartFactory.MES.Services.IPermissionService permissionService, IExportService exportService,
            IDateTimeService dateTime)
        {
            _context = context;
            _permissionService = permissionService;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        public async Task<IActionResult> Index(string? searchTerm, bool searched = false)
        {
            ViewBag.SearchTerm = searchTerm;
            ViewBag.Searched = searched;

            if (!searched)
                return View((IEnumerable<OrganizationUnit>?)null);

            var allUnits = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(u => u.SortOrder)
                .ThenBy(u => u.Name)
                .ToListAsync();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                var filtered = allUnits.Where(u =>
                    (u.Name != null && u.Name.ToLower().Contains(lower)) ||
                    (u.Path != null && u.Path.ToLower().Contains(lower))
                ).ToList();
                ViewBag.IsSearchMode = true;
                return View(filtered);
            }

            var rootNodes = allUnits.Where(u => string.IsNullOrEmpty(u.ParentId)).ToList();
            foreach (var node in rootNodes)
                PopulateChildren(node, allUnits);

            var effectivePlantIds = new Dictionary<string, string?>();
            foreach (var unit in allUnits)
                effectivePlantIds[unit.Id] = GetEffectivePlantId(unit, allUnits);
            ViewBag.EffectivePlantIds = effectivePlantIds;

            ViewBag.IsSearchMode = false;
            return View(rootNodes);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var data = await _context.OrganizationUnits.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(u => u.SortOrder).ThenBy(u => u.Name).ToListAsync();
            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "組織架構管理" : "Organization Structure",
                Headers = isZh
                    ? ["組織名稱", "路徑", "排序"]
                    : ["Organization Name", "Path", "Sort Order"],
                Rows = data.Select(u => new object?[] { u.Name ?? "", u.Path ?? "", u.SortOrder })
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"Organization_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"Organization_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"Organization_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        private void PopulateChildren(OrganizationUnit node, List<OrganizationUnit> allUnits)
        {
            node.Children = allUnits.Where(u => u.ParentId == node.Id)
                .OrderBy(u => u.SortOrder)
                .ThenBy(u => u.Name)
                .ToList();
            foreach (var child in node.Children)
                PopulateChildren(child, allUnits);
        }

        public async Task<IActionResult> Create()
        {
            ViewBag.ParentId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
                await _context.OrganizationUnits.IgnoreQueryFilters().ToListAsync(), "Id", "Name");
            ViewBag.Plants = await _context.Plants.ToListAsync();
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_OrgForm", new OrganizationUnit());
            return View("_OrgForm", new OrganizationUnit());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(OrganizationUnit organizationUnit)
        {
            if (ModelState.IsValid)
            {
                if (!string.IsNullOrEmpty(organizationUnit.ParentId))
                {
                    var parent = await _context.OrganizationUnits.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(u => u.Id == organizationUnit.ParentId);
                    organizationUnit.Path = (parent?.Path != null ? parent.Path + "/" : "") + organizationUnit.Name;
                }
                else
                {
                    organizationUnit.Path = organizationUnit.Name;
                }
                _context.Add(organizationUnit);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_CreateFailed") });
                    return StatusCode(500);
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_OrgCreated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }
            ViewBag.ParentId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
                await _context.OrganizationUnits.IgnoreQueryFilters().ToListAsync(), "Id", "Name", organizationUnit.ParentId);
            ViewBag.Plants = await _context.Plants.ToListAsync();
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_OrgForm", organizationUnit);
            return View("_OrgForm", organizationUnit);
        }

        public async Task<IActionResult> Edit(string id)
        {
            var organizationUnit = await _context.OrganizationUnits.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == id);
            if (organizationUnit == null) return NotFound();

            var allUnits = await _context.OrganizationUnits.IgnoreQueryFilters().ToListAsync();
            var excludedIds = GetDescendantIds(id, allUnits);
            excludedIds.Add(id);
            var validParents = allUnits.Where(u => !excludedIds.Contains(u.Id)).ToList();

            ViewBag.ParentId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(validParents, "Id", "Name", organizationUnit.ParentId);
            ViewBag.Plants = await _context.Plants.ToListAsync();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_OrgForm", organizationUnit);
            return View("_OrgForm", organizationUnit);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, OrganizationUnit organizationUnit)
        {
            if (id != organizationUnit.Id) return NotFound();

            if (ModelState.IsValid)
            {
                try
                {
                    if (!string.IsNullOrEmpty(organizationUnit.ParentId))
                    {
                        var parent = await _context.OrganizationUnits.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(u => u.Id == organizationUnit.ParentId);
                        organizationUnit.Path = (parent?.Path != null ? parent.Path + "/" : "") + organizationUnit.Name;
                    }
                    else
                    {
                        organizationUnit.Path = organizationUnit.Name;
                    }
                    _context.Update(organizationUnit);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!OrganizationUnitExists(organizationUnit.Id)) return NotFound();
                    else throw;
                }
                if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_OrgUpdated") });
                return RedirectToAction(nameof(Index), new { searched = true });
            }

            var allUnitsForError = await _context.OrganizationUnits.IgnoreQueryFilters().ToListAsync();
            var excludedIdsForError = GetDescendantIds(id, allUnitsForError);
            excludedIdsForError.Add(id);
            var validParentsForError = allUnitsForError.Where(u => !excludedIdsForError.Contains(u.Id)).ToList();
            ViewBag.ParentId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(validParentsForError, "Id", "Name", organizationUnit.ParentId);
            ViewBag.Plants = await _context.Plants.ToListAsync();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_OrgForm", organizationUnit);
            return View("_OrgForm", organizationUnit);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var organizationUnit = await _context.OrganizationUnits.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == id);
            if (organizationUnit == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_OrgNotFound") });
                return NotFound();
            }

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            if (await _context.OrganizationUnits.IgnoreQueryFilters().AnyAsync(u => u.ParentId == id))
                reasons.Add(CoreText.Get("Msg_OrgInUse_Children"));

            if (await _context.Set<ApplicationRole>().AnyAsync(r => r.OrganizationUnitId == id))
                reasons.Add(CoreText.Get("Msg_OrgInUse_Roles"));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_OrgInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            _context.OrganizationUnits.Remove(organizationUnit);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_OrgDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private HashSet<string> GetDescendantIds(string unitId, List<OrganizationUnit> allUnits)
        {
            var result = new HashSet<string>();
            foreach (var child in allUnits.Where(u => u.ParentId == unitId))
            {
                result.Add(child.Id);
                foreach (var desc in GetDescendantIds(child.Id, allUnits))
                    result.Add(desc);
            }
            return result;
        }

        private string? GetEffectivePlantId(OrganizationUnit unit, List<OrganizationUnit> allUnits)
        {
            if (!string.IsNullOrEmpty(unit.PlantId)) return unit.PlantId;
            if (string.IsNullOrEmpty(unit.ParentId)) return null;
            var parent = allUnits.FirstOrDefault(u => u.Id == unit.ParentId);
            return parent == null ? null : GetEffectivePlantId(parent, allUnits);
        }

        private bool OrganizationUnitExists(string id) => _context.OrganizationUnits.IgnoreQueryFilters().Any(e => e.Id == id);
    }
}
