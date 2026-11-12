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
    public class UserController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;
        private readonly ApplicationDbContext _context;
        private readonly IExportService _exportService;
        private readonly IDateTimeService _dateTime;

        public UserController(
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager,
            ApplicationDbContext context,
            IExportService exportService,
            IDateTimeService dateTime)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _context = context;
            _exportService = exportService;
            _dateTime = dateTime;
        }

        private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

        public async Task<IActionResult> Index(string? searchTerm, int? pageNumber, int? pageSize, string? sortOrder = null, bool searched = false)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.NameSort  = string.IsNullOrEmpty(sortOrder) || sortOrder == "name_asc" ? "name_desc" : "name_asc";
            ViewBag.EmailSort = sortOrder == "email_asc" ? "email_desc" : "email_asc";
            ViewBag.LangSort  = sortOrder == "lang_asc"  ? "lang_desc"  : "lang_asc";
            ViewBag.SearchTerm = searchTerm;
            ViewBag.CurrentPageSize = pageSize ?? 20;
            ViewBag.Searched = searched;

            if (!searched)
                return View((PaginatedList<ApplicationUser>?)null);

            var query = _userManager.Users.AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(u =>
                    (u.UserName != null && u.UserName.ToLower().Contains(lower)) ||
                    (u.Email    != null && u.Email.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "name_asc"   => query.OrderBy(u => u.UserName),
                "name_desc"  => query.OrderByDescending(u => u.UserName),
                "email_asc"  => query.OrderBy(u => u.Email),
                "email_desc" => query.OrderByDescending(u => u.Email),
                "lang_asc"   => query.OrderBy(u => u.Language),
                "lang_desc"  => query.OrderByDescending(u => u.Language),
                _            => query.OrderBy(u => u.UserName),
            };

            PaginatedList<ApplicationUser> paginatedList = await PaginatedList<ApplicationUser>.CreateAsync(
                query.AsNoTracking(), pageNumber ?? 1, (int)ViewBag.CurrentPageSize);

            // 載入每位使用者的角色
            var allRoles = await _roleManager.Roles.ToListAsync();
            var userRoles = new Dictionary<string, List<ApplicationRole>>();
            foreach (var user in paginatedList)
            {
                var roleNames = await _userManager.GetRolesAsync(user);
                userRoles[user.Id] = allRoles.Where(r => roleNames.Contains(r.Name!)).ToList();
            }
            ViewBag.UserRoles = userRoles;
            return View(paginatedList);
        }

        [HttpGet]
        public async Task<IActionResult> Export(string? searchTerm, string? sortOrder, string format = "xlsx")
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.Name != "en-US";
            var query = _userManager.Users.AsQueryable();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var lower = searchTerm.ToLower();
                query = query.Where(u =>
                    (u.UserName != null && u.UserName.ToLower().Contains(lower)) ||
                    (u.Email != null && u.Email.ToLower().Contains(lower)));
            }
            query = sortOrder switch
            {
                "name_asc"   => query.OrderBy(u => u.UserName),
                "name_desc"  => query.OrderByDescending(u => u.UserName),
                "email_asc"  => query.OrderBy(u => u.Email),
                "email_desc" => query.OrderByDescending(u => u.Email),
                "lang_asc"   => query.OrderBy(u => u.Language),
                "lang_desc"  => query.OrderByDescending(u => u.Language),
                _            => query.OrderBy(u => u.UserName),
            };
            var users = await query.AsNoTracking().ToListAsync();
            var allRoles = await _roleManager.Roles.ToListAsync();

            var now = _dateTime.Now;
            var req = new ExportRequest
            {
                Title = isZh ? "使用者管理" : "User Management",
                Headers = isZh
                    ? ["帳號", "Email", "語系", "角色"]
                    : ["Username", "Email", "Language", "Roles"],
                Rows = users.Select(u =>
                {
                    var roleNames = _userManager.GetRolesAsync(u).GetAwaiter().GetResult();
                    return new object?[] { u.UserName ?? "", u.Email ?? "", u.Language ?? "", string.Join(", ", roleNames) };
                })
            };
            return format switch
            {
                "csv"  => File(_exportService.ToCsv(req), "text/csv", Uri.EscapeDataString($"User_{now:yyyyMMdd}.csv")),
                "json" => File(_exportService.ToJson(req), "application/json", Uri.EscapeDataString($"User_{now:yyyyMMdd}.json")),
                _      => File(_exportService.ToExcel(req), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                               Uri.EscapeDataString($"User_{now:yyyyMMdd_HHmm}.xlsx"))
            };
        }

        public async Task<IActionResult> Create()
        {
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_UserForm", new ApplicationUser());
            return View("_UserForm", new ApplicationUser());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ApplicationUser user, string password, string[] selectedRoles)
        {
            if (ModelState.IsValid)
            {
                user.EmailConfirmed = true;
                var result = await _userManager.CreateAsync(user, password);
                if (result.Succeeded)
                {
                    if (selectedRoles?.Length > 0)
                        await _userManager.AddToRolesAsync(user, selectedRoles);

                    if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_UserCreated") });
                    return RedirectToAction(nameof(Index), new { searched = true });
                }
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
            }
            await PrepareFormDropdowns();
            ViewBag.IsEdit = false;
            if (IsAjax) return PartialView("_UserForm", user);
            return View("_UserForm", user);
        }

        public async Task<IActionResult> Edit(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            await PrepareFormDropdowns();
            ViewBag.UserRoles = await _userManager.GetRolesAsync(user);
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_UserForm", user);
            return View("_UserForm", user);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, ApplicationUser updatedUser, string[] selectedRoles)
        {
            if (id != updatedUser.Id) return NotFound();
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (ModelState.IsValid)
            {
                user.UserName   = updatedUser.UserName;
                user.Email      = updatedUser.Email;
                user.Language   = updatedUser.Language;
                user.TimeZoneId = updatedUser.TimeZoneId;

                var result = await _userManager.UpdateAsync(user);
                if (result.Succeeded)
                {
                    var currentRoles = await _userManager.GetRolesAsync(user);
                    selectedRoles ??= Array.Empty<string>();
                    await _userManager.AddToRolesAsync(user, selectedRoles.Except(currentRoles));
                    await _userManager.RemoveFromRolesAsync(user, currentRoles.Except(selectedRoles));
                    await RealignActiveRoleAsync(user, selectedRoles);
                    if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_UserUpdated") });
                    return RedirectToAction(nameof(Index), new { searched = true });
                }
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
            }

            await PrepareFormDropdowns();
            ViewBag.UserRoles = selectedRoles ?? Array.Empty<string>();
            ViewBag.IsEdit = true;
            if (IsAjax) return PartialView("_UserForm", updatedUser);
            return View("_UserForm", updatedUser);
        }

        /// <summary>
        /// 作用中角色被移除時，改指向第一個仍擁有的角色（都沒有就清空）。
        /// <para>不做的話 ActiveRoleId 會一直停在被移除的角色：2026-10-07 實測 dev 2 月被改成只有 Operator，
        /// ActiveRoleId 仍是 Admin，桌機與手機權限全部照 Admin 給，而使用者維護畫面上看不出任何異狀。</para>
        /// </summary>
        private async Task RealignActiveRoleAsync(ApplicationUser user, string[] roleNames)
        {
            if (user.ActiveRoleId is null) return;

            var activeRoleName = (await _roleManager.FindByIdAsync(user.ActiveRoleId))?.Name;
            if (activeRoleName != null && roleNames.Contains(activeRoleName)) return;

            var fallback = roleNames.Length > 0 ? await _roleManager.FindByNameAsync(roleNames[0]) : null;
            user.ActiveRoleId = fallback?.Id;
            await _userManager.UpdateAsync(user);
        }

        [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                if (IsAjax) return Json(new { success = false, message = CoreText.Get("Msg_UserNotFound") });
                return NotFound();
            }

            // ── 使用中檢查 ──
            var reasons = new List<string>();

            if (await _context.ApprovalRequests.IgnoreQueryFilters()
                    .AnyAsync(r => r.RequesterId == id && r.Status == ApprovalStatus.Pending))
                reasons.Add(CoreText.Get("Msg_UserInUse_Requested"));

            if (await _context.ApprovalRequests.IgnoreQueryFilters()
                    .AnyAsync(r => r.ActiveApproverId == id && r.Status == ApprovalStatus.Pending))
                reasons.Add(CoreText.Get("Msg_UserInUse_Approving"));

            if (reasons.Count > 0)
            {
                var msg = CoreText.Get("Msg_UserInUseFmt", string.Join(CoreText.Get("Msg_ListSeparator"), reasons));
                if (IsAjax) return Json(new { success = false, message = msg });
                return StatusCode(400);
            }

            await _userManager.DeleteAsync(user);
            if (IsAjax) return Json(new { success = true, message = CoreText.Get("Msg_UserDeleted") });
            return RedirectToAction(nameof(Index), new { searched = true });
        }

        private async Task PrepareFormDropdowns()
        {
            // 角色後面的括號顯示「組織單位｜可用廠別」：原本只顯示組織單位，但沒載入導覽屬性，
            // 幾乎每個角色都顯示成「(系統)」；廠別是指派角色時最需要看到的資訊（2026-11-11 驗收回饋）。
            ViewBag.AllRoles = await _roleManager.Roles.AsNoTracking()
                .Include(r => r.OrganizationUnit)
                .Include(r => r.PlantAssignments)
                .OrderBy(r => r.Name)
                .ToListAsync();
        }
    }
}
