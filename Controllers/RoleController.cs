using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    [RequireActiveAdmin]   // 只看作用中角色（見 AdminAccess）
    public class RoleController : Controller
    {
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly SmartFactory.MES.Data.ApplicationDbContext _context;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public RoleController(RoleManager<ApplicationRole> roleManager,
            SmartFactory.MES.Data.ApplicationDbContext context, IExportService exportService,
            IDateTimeService dateTime)
        {
            _roleManager = roleManager;
            _context = context;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        private async Task PrepareOrgListAsync(string? selectedId = null)
            => ViewBag.OrganizationUnitId = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
                await _context.OrganizationUnits.IgnoreQueryFilters().OrderBy(o => o.Name).ToListAsync(),
                "Id", "Name", selectedId);

        private async Task PrepareAllPlantsAsync()
            => ViewBag.AllPlants = await _context.Plants.IgnoreQueryFilters().OrderBy(p => p.Id).ToListAsync();

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize, string? sortOrder = null, bool searched = false)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.NameSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.NameEnSort = sortOrder == "en_asc" ? "en_desc" : "en_asc";
            ViewBag.OrgSort = sortOrder == "org_asc" ? "org_desc" : "org_asc";
            ViewBag.SearchTerm = searchTerm;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Searched = searched;

            if (!searched)
                return View((PaginatedList<ApplicationRole>?)null);

            var query = _context.Set<ApplicationRole>()
                .Include(r => r.OrganizationUnit)
                .IgnoreQueryFilters()
                .AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(lower)) ||
                    (r.NormalizedName != null && r.NormalizedName.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "name_asc"  => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "en_asc"    => query.OrderBy(r => r.NameEn),
                "en_desc"   => query.OrderByDescending(r => r.NameEn),
                "org_asc"   => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc"  => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _           => query.OrderBy(r => r.Name),
            };
            return View(await PaginatedList<ApplicationRole>.CreateAsync(query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _context.Set<ApplicationRole>()
                .Include(r => r.OrganizationUnit)
                .IgnoreQueryFilters()
                .AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(lower)) ||
                    (r.NormalizedName != null && r.NormalizedName.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "name_asc"  => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "en_asc"    => query.OrderBy(r => r.NameEn),
                "en_desc"   => query.OrderByDescending(r => r.NameEn),
                "org_asc"   => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc"  => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _           => query.OrderBy(r => r.Name),
            };
            var data = await query.AsNoTracking().ToListAsync();
            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "角色管理" : "Role Management",
                Headers = isZh
                    ? ["角色名稱", "英文名稱", "組織單位"]
                    : ["Role Name", "Name (EN)", "Organization"],
                Rows = data.Select(r => new object?[] { r.Name ?? "", r.NameEn ?? "", r.OrganizationUnit?.Name ?? "" })
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"Role_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"Role_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"Role_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        public async Task<IActionResult> Create()
        {
            await PrepareOrgListAsync();
            await PrepareAllPlantsAsync();
            ViewBag.IsEdit = false;
            ViewBag.SelectedPlantIds = new List<string>();
            ViewBag.DefaultPlantId = (string?)null;
            if (IsAjax) return PartialView("_RoleForm", new ApplicationRole());
            return View("_RoleForm", new ApplicationRole());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ApplicationRole role, string[]? selectedPlantIds, string? defaultPlantId)
        {
            if (ModelState.IsValid)
            {
                var result = await _roleManager.CreateAsync(role);
                if (result.Succeeded)
                {
                    await SavePlantAssignmentsAsync(role.Id, selectedPlantIds, defaultPlantId);
                    if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_RoleCreated") });
                    return RedirectToAction(nameof(Index), new { searched = true });
                }
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
            }
            await PrepareOrgListAsync(role.OrganizationUnitId);
            await PrepareAllPlantsAsync();
            ViewBag.IsEdit = false;
            ViewBag.SelectedPlantIds = selectedPlantIds?.ToList() ?? new List<string>();
            ViewBag.DefaultPlantId = defaultPlantId;
            if (IsAjax) return PartialView("_RoleForm", role);
            return View("_RoleForm", role);
        }

        public async Task<IActionResult> Edit(string id)
        {
            var role = await _context.Set<ApplicationRole>()
                .Include(r => r.PlantAssignments)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (role == null) return NotFound();
            await PrepareOrgListAsync(role.OrganizationUnitId);
            await PrepareAllPlantsAsync();
            ViewBag.IsEdit = true;
            ViewBag.SelectedPlantIds = role.PlantAssignments.Select(p => p.PlantId).ToList();
            ViewBag.DefaultPlantId = role.PlantAssignments.FirstOrDefault(p => p.IsDefault)?.PlantId;
            if (IsAjax) return PartialView("_RoleForm", role);
            return View("_RoleForm", role);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, ApplicationRole updatedRole, string[]? selectedPlantIds, string? defaultPlantId)
        {
            if (id != updatedRole.Id) return NotFound();
            if (ModelState.IsValid)
            {
                var role = await _roleManager.FindByIdAsync(id);
                if (role == null) return NotFound();
                role.Name = updatedRole.Name;
                role.NameEn = updatedRole.NameEn;
                role.OrganizationUnitId = updatedRole.OrganizationUnitId;
                var result = await _roleManager.UpdateAsync(role);
                if (result.Succeeded)
                {
                    await SavePlantAssignmentsAsync(id, selectedPlantIds, defaultPlantId);
                    if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_RoleUpdated") });
                    return RedirectToAction(nameof(Index), new { searched = true });
                }
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
            }
            await PrepareOrgListAsync(updatedRole.OrganizationUnitId);
            await PrepareAllPlantsAsync();
            ViewBag.IsEdit = true;
            ViewBag.SelectedPlantIds = selectedPlantIds?.ToList() ?? new List<string>();
            ViewBag.DefaultPlantId = defaultPlantId;
            if (IsAjax) return PartialView("_RoleForm", updatedRole);
            return View("_RoleForm", updatedRole);
        }

        private async Task SavePlantAssignmentsAsync(string roleId, string[]? selectedPlantIds, string? defaultPlantId)
        {
            var existing = await _context.RolePlantAssignments
                .Where(rpa => rpa.RoleId == roleId).ToListAsync();
            _context.RolePlantAssignments.RemoveRange(existing);

            if (selectedPlantIds != null && selectedPlantIds.Length > 0)
            {
                foreach (var plantId in selectedPlantIds)
                {
                    _context.RolePlantAssignments.Add(new SmartFactory.MES.Models.RolePlantAssignment
                    {
                        RoleId = roleId,
                        PlantId = plantId,
                        IsDefault = plantId == defaultPlantId
                    });
                }
            }
            await _context.SaveChangesAsync();
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var role = await _roleManager.FindByIdAsync(id);
            if (role == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_RoleNotFound") });
                return NotFound();
            }

            var trustedId = role.Id;

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            var usersInRole = await _context.Set<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>()
                .CountAsync(ur => ur.RoleId == trustedId);
            if (usersInRole > 0)
                reasons.Add(CoreText.Get("Msg_RoleInUse_UsersFmt", usersInRole));

            if (await _context.ApprovalSteps.AnyAsync(s => s.TargetRoleId == trustedId))
                reasons.Add(CoreText.Get("Msg_RoleInUse_TemplateSteps"));

            if (await _context.ApprovalRequests.IgnoreQueryFilters()
                    .AnyAsync(r => r.RequesterRoleId == trustedId && r.Status == ApprovalStatus.Pending))
                reasons.Add(CoreText.Get("Msg_InUse_PendingApprovals"));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_RoleInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            // 清理附屬資料（MenuPermission、ActiveRoleId）
            var usersWithActiveRole = _context.Users.Where(u => u.ActiveRoleId == trustedId);
            foreach (var user in usersWithActiveRole)
                user.ActiveRoleId = null;
            var permissions = _context.MenuPermissions.Where(p => p.RoleId == trustedId);
            _context.MenuPermissions.RemoveRange(permissions);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_DeleteFailed") });
                return StatusCode(500);
            }
            await _roleManager.DeleteAsync(role);
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_RoleDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }
    }
}
