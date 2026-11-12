using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;
using System.Security.Claims;
using System.Linq;

namespace SmartFactory.MES.ViewComponents
{
    public class SidebarMenuViewComponent : ViewComponent
    {
        private readonly Services.IMenuService _menuService;
        private readonly Data.ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public SidebarMenuViewComponent(Services.IMenuService menuService, Data.ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _menuService = menuService;
            _context = context;
            _userManager = userManager;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var menuItems = _menuService.GetFullMenu();
            
            var userId = _userManager.GetUserId(HttpContext.User);
            if (string.IsNullOrEmpty(userId)) return View(new List<Services.MenuItem>());

            var appUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (appUser == null) return View(new List<Services.MenuItem>());

            var activeRoleId = appUser.ActiveRoleId;
            
            // 偵錯用：將角色 ID 注入第一個選單項 (對副本進行操作)
            // if (menuItems.Any()) {
            //     menuItems[0] = CloneItem(menuItems[0]);
            //     menuItems[0].Title = $"[RID:{activeRoleId}] " + menuItems[0].Title;
            // }

            // 已被移出的角色比照「沒有 ActiveRole」：ActiveRoleId 不會因角色異動自己消失，
            // 不修正的話選單會照舊角色（例如 Admin）整份長出來（見 UserContext.EnsureUserLoaded）。
            if (!string.IsNullOrEmpty(activeRoleId) &&
                !await _context.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == activeRoleId))
                activeRoleId = null;

            // 如果沒有 ActiveRole，嘗試自動初始化
            if (string.IsNullOrEmpty(activeRoleId))
            {
                var roles = await _userManager.GetRolesAsync(appUser);
                if (roles.Any())
                {
                    var firstRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == roles.First());
                    if (firstRole != null)
                    {
                        // 寫回要用追蹤中的那一份（FindByIdAsync 會先找 change tracker）：appUser 是 AsNoTracking，
                        // UserContext 在同一請求已載入同一使用者時，拿 appUser 去 UpdateAsync 會撞
                        // "another instance with the same key value is already being tracked"。
                        var tracked = await _userManager.FindByIdAsync(userId);
                        if (tracked != null)
                        {
                            tracked.ActiveRoleId = firstRole.Id;
                            await _userManager.UpdateAsync(tracked);
                        }
                        activeRoleId = firstRole.Id;
                    }
                }
            }

            // 強制過濾邏輯：即使是 Admin，切換身份後也應遵循該身份權限
            // 如果連基本角色都沒有，則顯示空選單
            if (string.IsNullOrEmpty(activeRoleId))
            {
                return View(new List<Services.MenuItem>());
            }

            // 取得該角色的權限設定
            var permissions = await _context.MenuPermissions
                .Where(p => p.RoleId == activeRoleId && p.CanRead)
                .Select(p => p.MenuKey)
                .ToListAsync();

            // 只限 Admin 的項目看作用中角色，不看權限矩陣（與 [RequireActiveAdmin] 同一份規則，見 AdminAccess）
            var isAdmin = await _context.Roles.AsNoTracking()
                .AnyAsync(r => r.Id == activeRoleId && r.Name == Services.AdminAccess.RoleName);

            // 遞迴過濾選單
            var filteredMenu = FilterMenu(menuItems, permissions, isAdmin);

            return View(filteredMenu);
        }

        private List<Services.MenuItem> FilterMenu(List<Services.MenuItem> items, List<string> allowedKeys, bool isAdmin)
        {
            var result = new List<Services.MenuItem>();
            foreach (var item in items)
            {
                // 1. 基礎權限檢查 (Home 與 UserSettings 始終可看)
                // 只限 Admin 的項目：權限矩陣勾了也不算——原本選單照矩陣顯示、程式卻只認 Admin，
                // 點進去就是 403（2026-11-11 筆電驗收）。
                bool isExplicitlyAllowed = Services.AdminAccess.IsRoleRestricted(item) ? isAdmin :
                                           allowedKeys.Contains(item.MenuKey) || 
                                           item.MenuKey == "Home" || 
                                           item.MenuKey == "UserSettings";

                if (item.SubItems.Any())
                {
                    // 2. 遞迴過濾子選單
                    var filteredSub = FilterMenu(item.SubItems, allowedKeys, isAdmin);
                    
                    // 3. 如果子選單有東西，則保留父選單
                    if (filteredSub.Any())
                    {
                        var newItem = CloneItem(item);
                        newItem.SubItems = filteredSub;
                        result.Add(newItem);
                    }
                }
                else if (isExplicitlyAllowed)
                {
                    // 4. 沒有子選單且獲得授權的可見
                    result.Add(item);
                }
            }
            return result;
        }

        private Services.MenuItem CloneItem(Services.MenuItem item)
        {
            return new Services.MenuItem
            {
                Title = item.Title,
                Controller = item.Controller,
                Action = item.Action,
                Page = item.Page,
                Icon = item.Icon,
                MenuKey = item.MenuKey
            };
        }
    }
}
