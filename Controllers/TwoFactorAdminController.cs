using SmartFactory.MES.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Controllers
{
    /// <summary>
    /// 安全修正 M3：管理員重置他人 2FA（遺失驗證器救援）。
    ///
    /// <para>D3 定案：復原碼自救 + 本 ProjectZ 管理畫面（不改 Core UserController）。
    /// 存取以 <b>Admin 角色</b>顯式把關（除選單權限外的縱深防護）；重置＝停用 2FA
    /// 並重置 authenticator 金鑰（連帶使舊金鑰/復原碼失效），使用者下次登入需重新綁定。</para>
    /// </summary>
    [Authorize(Policy = "AuthenticatedUser")]
    public class TwoFactorAdminController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public TwoFactorAdminController(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public class UserRow
        {
            public string Id { get; set; } = "";
            public string UserName { get; set; } = "";
            public string? Email { get; set; }
            public bool TwoFactorEnabled { get; set; }
        }

        private async Task<bool> CallerIsAdminAsync()
        {
            // 看作用中角色而不是所屬角色（見 AdminAccess）：切成其他角色後不應還能重置他人 2FA
            return await AdminAccess.IsActiveAdminAsync(HttpContext);
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search)
        {
            if (!await CallerIsAdminAsync()) return Forbid();

            var query = _userManager.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                query = query.Where(u =>
                    (u.UserName != null && u.UserName.Contains(s)) ||
                    (u.Email != null && u.Email.Contains(s)));
            }

            // 管理救援用途，避免全量載入。⚠️ 上限一定要讓使用者看得見：
            // 在這之前它是**靜默**截斷的——超過 200 個帳號時，後面的人就是查不到，
            // 畫面上卻沒有任何跡象。多取一筆用來判斷有沒有被截到。
            const int rowCap = 200;
            var rows = await query
                .OrderBy(u => u.UserName)
                .Take(rowCap + 1)
                .Select(u => new UserRow
                {
                    Id = u.Id,
                    UserName = u.UserName ?? "",
                    Email = u.Email,
                    TwoFactorEnabled = u.TwoFactorEnabled,
                })
                .ToListAsync();

            ViewBag.RowCap    = rowCap;
            ViewBag.Truncated = rows.Count > rowCap;
            if (rows.Count > rowCap) rows = rows.Take(rowCap).ToList();

            ViewBag.Search = search;
            return View(rows);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reset(string userId)
        {
            if (!await CallerIsAdminAsync()) return Forbid();

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound();

            await _userManager.SetTwoFactorEnabledAsync(user, false);
            await _userManager.ResetAuthenticatorKeyAsync(user);

            var isEn = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("en");
            TempData["SuccessMessage"] = isEn
                ? $"Two-factor authentication has been reset for '{user.UserName}'. They must re-enroll on next sign-in."
                : $"已重置「{user.UserName}」的兩階段驗證，該使用者下次登入須重新綁定。";

            return RedirectToAction(nameof(Index));
        }
    }
}
