using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    /// <summary>手機模組權限管理（桌面版）— 整合進「系統管理」選單</summary>
    [Authorize]
    [RequireActiveAdmin]   // 只看作用中角色（見 AdminAccess）
    public class MobilePermissionController : Controller
    {
        private readonly ApplicationDbContext _db;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public MobilePermissionController(
            ApplicationDbContext db,
            RoleManager<ApplicationRole> roleManager,
            IExportService exportService,
            IDateTimeService dateTime)
        {
            _db = db;
            _roleManager = roleManager;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        /// <summary>角色清單（含搜尋、排序、分頁）</summary>
        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize,
            string? sortOrder, bool searched = false)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.NameSort = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.OrgSort = sortOrder == "org_asc" ? "org_desc" : "org_asc";
            ViewBag.SearchTerm = searchTerm;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Searched = searched;

            if (!searched)
                return View((PaginatedList<ApplicationRole>?)null);

            var query = _roleManager.Roles
                .Include(r => r.OrganizationUnit)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(lower)) ||
                    (r.NameZh != null && r.NameZh.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "name_asc"  => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "org_asc"   => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc"  => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _           => query.OrderBy(r => r.Name)
            };

            return View(await PaginatedList<ApplicationRole>.CreateAsync(
                query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        // ── 匯出 ─────────────────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _roleManager.Roles
                .Include(r => r.OrganizationUnit)
                .AsQueryable();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(lower)) ||
                    (r.NameZh != null && r.NameZh.ToLower().Contains(lower)));
            }

            query = sortOrder switch
            {
                "name_asc"  => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "org_asc"   => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc"  => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _           => query.OrderBy(r => r.Name)
            };

            var data = await query.AsNoTracking().ToListAsync();
            var now = _dateTime.Now;

            var req = new ExportRequest
            {
                Title = isZh ? "手機模組權限管理" : "Mobile Module Permission",
                Headers = isZh
                    ? ["角色名稱", "中文名稱", "組織單位"]
                    : ["Role Name", "Name (ZH)", "Organization"],
                Rows = data.Select(r => new object?[]
                {
                    r.Name ?? "",
                    r.NameZh ?? "",
                    r.OrganizationUnit?.Name ?? ""
                })
            };

            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv",
                               Uri.EscapeDataString($"MobilePermission_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json",
                               Uri.EscapeDataString($"MobilePermission_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req),
                               "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"MobilePermission_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        /// <summary>設定指定角色的手機模組權限</summary>
        public async Task<IActionResult> Edit(string id)
        {
            var role = await _roleManager.FindByIdAsync(id);
            if (role == null) return NotFound();

            var modules = await _db.MobileModules
                .IgnoreQueryFilters()
                .Where(m => m.IsActive)
                .Include(m => m.Actions)
                .OrderBy(m => m.SortOrder)
                .ToListAsync();

            var trustedId = role.Id;
            var existingPermissions = await _db.MobileRolePermissions
                .IgnoreQueryFilters()
                .Where(p => p.RoleId == trustedId)
                .ToListAsync();

            ViewBag.Role = role;
            ViewBag.Modules = modules;
            ViewBag.ExistingPermissions = existingPermissions
                .ToDictionary(p => p.ModuleActionId, p => p.IsAllowed);

            if (IsAjax) return PartialView("_MobilePermissionPanel");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string roleId, List<string> allowedActionIds)
        {
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role == null) return NotFound();

            var trustedRoleId = role.Id;

            // 取出所有 ModuleActionId，重新寫入
            var allActionIds = await _db.MobileModuleActions
                .IgnoreQueryFilters()
                .Select(a => a.Id)
                .ToListAsync();

            var existingPermissions = _db.MobileRolePermissions
                .IgnoreQueryFilters()
                .Where(p => p.RoleId == trustedRoleId);
            _db.MobileRolePermissions.RemoveRange(existingPermissions);

            allowedActionIds ??= new List<string>();

            foreach (var actionId in allActionIds)
            {
                _db.MobileRolePermissions.Add(new MobileRolePermission
                {
                    RoleId = trustedRoleId,
                    ModuleActionId = actionId,
                    IsAllowed = allowedActionIds.Contains(actionId)
                });
            }

            // AuditLog：記錄手機模組權限變更
            _db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Mobile.Permission",
                Action = AuditAction.Modified,
                EntityId = trustedRoleId,
                UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                UserName = User.Identity?.Name,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Changes = System.Text.Json.JsonSerializer.Serialize(new
                {
                    RoleId = trustedRoleId,
                    RoleName = role.Name,
                    AllowedCount = allowedActionIds.Count
                })
            });

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return Json(new { success = false, message = CoreText.Get("Msg_SaveFailed") });
                return StatusCode(500);
            }

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { success = true });

            return RedirectToAction(nameof(Index));
        }
    }
}
