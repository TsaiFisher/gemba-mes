using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>
    /// 手機版的角色切換（2026-11-11 筆電驗收後使用者要求）。
    ///
    /// <para>原本只有桌機能切角色（<c>/switch-role</c>、使用者設定）。現場人員多半只有手機，
    /// 角色一換就要找電腦；作用中角色被管理員移除時更糟——手機上所有功能都被擋，卻沒有出路。</para>
    ///
    /// <para>規則與桌機同一份：<see cref="IRoleSwitchService"/>（只能切到自己所屬的角色，
    /// 新角色不含目前廠別時改到該角色的預設廠別）。路徑 <c>/api/mobile/roles</c> 列在
    /// <c>UserPreferenceMiddleware</c> 的復原路徑內，角色失效時仍到得了。</para>
    ///
    /// <para><b>沒有 <c>[ValidateAntiForgeryToken]</c></b>，理由同 <see cref="MobilePlantController"/>.Switch：
    /// 認證 Cookie 是 SameSite=Lax，跨站 POST 帶不上它。</para>
    /// </summary>
    [Route("api/mobile/roles")]
    [ApiController]
    [Authorize(Policy = "AuthenticatedUser")]
    public class MobileRoleController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IRoleSwitchService _roleSwitch;

        public MobileRoleController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
            IRoleSwitchService roleSwitch)
        {
            _db = db;
            _userManager = userManager;
            _roleSwitch = roleSwitch;
        }

        /// <summary>這個使用者所屬的角色，以及目前用的是哪一個。</summary>
        [HttpGet]
        [PermissionGateExempt(
            "只列呼叫者自己所屬的角色（AspNetUserRoles），不含別人的資料；與桌機使用者設定的角色清單同一類。"
            + "必須在任何權限狀態下都到得了：作用中角色被移除時，這裡是手機唯一的出路。")]
        public async Task<IActionResult> GetRoles()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            var roles = await _db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == user.Id)
                .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { r.Id, r.Name, r.NameZh, r.NameEn })
                .OrderBy(r => r.Name)
                .ToListAsync();

            // 作用中角色若已不在所屬清單內（被管理員移除），回 null，畫面就不會把它標成「目前」
            var current = roles.Any(r => r.Id == user.ActiveRoleId) ? user.ActiveRoleId : null;

            return Ok(new
            {
                currentRoleId = current,
                roles = roles.Select(r => new { r.Id, name = r.NameZh ?? r.Name, nameEn = r.NameEn ?? r.Name }),
            });
        }

        /// <summary>切換作用中角色。</summary>
        [HttpPost("switch")]
        [PermissionGateExempt(
            "本端點本身就是權限閘門：IRoleSwitchService 只允許切到呼叫者所屬的角色（與桌機 /switch-role 同一份規則），"
            + "不屬於的回 403。")]
        public async Task<IActionResult> Switch([FromBody] RoleSwitchRequest req)
        {
            var outcome = await _roleSwitch.TrySwitchAsync(User, req?.RoleId ?? string.Empty);

            if (outcome is RoleSwitchOutcome.NotAllowed or RoleSwitchOutcome.UserNotFound)
                return Forbid();
            if (outcome == RoleSwitchOutcome.SaveFailed)
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = new { code = "ROLE_SWITCH_FAILED", message = CoreText.Get("Msg_SwitchRoleFailedRetry") } });

            // 換角色可能連帶換廠（新角色不含目前廠別時改到其預設廠）。沒有個人語系的帳號跟著廠別走，
            // 回傳新廠的語系讓手機端更新 localStorage.mobileLang（與 MobilePlantController.Switch 同規則）。
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();
            var followsPlant = !LanguagePreference.HasPersonal(user);
            var language = await LanguagePreference.ResolveForUserAsync(_db, user, user.ActivePlantId);
            if (followsPlant) LanguagePreference.AppendCookie(Response, language);
            return Ok(new { roleId = user.ActiveRoleId, plantId = user.ActivePlantId, language, followsPlant });
        }
    }

    /// <summary>切換角色的請求內容。</summary>
    public record RoleSwitchRequest(string? RoleId);
}
