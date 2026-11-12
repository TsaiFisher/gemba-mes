using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Controllers
{
    [Authorize]
    [RequireActiveAdmin]   // 只看作用中角色（見 AdminAccess）
    public class PermissionController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly IMenuService _menuService;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public PermissionController(ApplicationDbContext context, RoleManager<ApplicationRole> roleManager,
            IMenuService menuService, IExportService exportService,
            IDateTimeService dateTime)
        {
            _context = context;
            _roleManager = roleManager;
            _menuService = menuService;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize, string? sortOrder = null, bool searched = false)
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
                searchTerm = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(searchTerm)) ||
                    (r.NameEn != null && r.NameEn.ToLower().Contains(searchTerm))
                );
            }

            query = sortOrder switch
            {
                "name_asc" => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "org_asc" => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc" => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _ => query.OrderBy(r => r.Name),
            };

            return View(await PaginatedList<ApplicationRole>.CreateAsync(query.AsNoTracking(), pageNumber ?? 1, ViewBag.CurrentPageSize));
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _roleManager.Roles.Include(r => r.OrganizationUnit).AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(r =>
                    (r.Name != null && r.Name.ToLower().Contains(lower)) ||
                    (r.NameEn != null && r.NameEn.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "name_asc"  => query.OrderBy(r => r.Name),
                "name_desc" => query.OrderByDescending(r => r.Name),
                "org_asc"   => query.OrderBy(r => r.OrganizationUnit!.Name),
                "org_desc"  => query.OrderByDescending(r => r.OrganizationUnit!.Name),
                _           => query.OrderBy(r => r.Name),
            };
            var data = await query.AsNoTracking().ToListAsync();
            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "功能權限管理" : "Permission Settings",
                Headers = isZh
                    ? ["角色名稱", "英文名稱", "組織單位"]
                    : ["Role Name", "Name (EN)", "Organization"],
                Rows = data.Select(r => new object?[] { r.Name ?? "", r.NameEn ?? "", r.OrganizationUnit?.Name ?? "" })
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"Permission_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"Permission_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"Permission_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        public async Task<IActionResult> Edit(string id)
        {
            var role = await _roleManager.FindByIdAsync(id);
            if (role == null) return NotFound();

            var trustedId = role.Id;
            // 只限 Admin 的項目（帳號、角色、權限設定…）不列入矩陣：勾了也不會生效，
            // 原本勾得到，選單就照勾選顯示、點進去卻是 403（見 AdminAccess）。
            var menus = _menuService.GetFlattenedAccessibleMenus()
                .Where(m => m.MenuKey != "Home" && m.MenuKey != "UserSettings" && !AdminAccess.IsRoleRestricted(m))
                .ToList();
            var menuTree = WithoutRoleRestricted(_menuService.GetFullMenu()
                .Where(m => m.MenuKey != "Home" && m.MenuKey != "UserSettings"));
            var existingPermissions = await _context.MenuPermissions
                .Where(p => p.RoleId == trustedId)
                .ToListAsync();

            ViewBag.Role = role;
            ViewBag.Menus = menus;
            ViewBag.MenuTree = menuTree;

            // AJAX 請求 → 回傳 Side Panel 局部視圖
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return PartialView("_PermissionPanel", existingPermissions);

            return View(existingPermissions);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string roleId, List<MenuPermission> permissions)
        {
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role == null) return NotFound();

            var trustedRoleId = role.Id;

            // 移除舊權限
            var oldPermissions = _context.MenuPermissions.Where(p => p.RoleId == trustedRoleId);
            _context.MenuPermissions.RemoveRange(oldPermissions);

            // 加入新權限 (只加入有勾選任何一個 CRUD 的項目)
            foreach (var p in permissions)
            {
                if (p.CanRead || p.CanCreate || p.CanUpdate || p.CanDelete || p.CanPrint)
                {
                    p.Id = Guid.NewGuid().ToString();
                    p.RoleId = trustedRoleId;
                    _context.MenuPermissions.Add(p);
                }
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                    return Json(new { success = false, message = CoreText.Get("Msg_SaveFailed") });
                return StatusCode(500);
            }

            // AJAX 請求 → 回傳 JSON
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { success = true });

            return RedirectToAction(nameof(Index));
        }
            /// <summary>移除只限特定角色的項目（遞迴，回傳複本；子項全被移除的群組也一併拿掉）。</summary>
        internal static List<MenuItem> WithoutRoleRestricted(IEnumerable<MenuItem> items)
        {
            var result = new List<MenuItem>();
            foreach (var item in items)
            {
                if (AdminAccess.IsRoleRestricted(item)) continue;
                if (item.SubItems.Count == 0) { result.Add(item); continue; }
                var subs = WithoutRoleRestricted(item.SubItems);
                if (subs.Count == 0) continue;
                result.Add(new MenuItem
                {
                    Title = item.Title, Controller = item.Controller, Action = item.Action, Page = item.Page,
                    Icon = item.Icon, MenuKey = item.MenuKey, Roles = item.Roles,
                    SupportedActions = item.SupportedActions, SubItems = subs
                });
            }
            return result;
        }
    }
}
