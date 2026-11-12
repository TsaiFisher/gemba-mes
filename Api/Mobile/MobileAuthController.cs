using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Api.Mobile
{
    /// <summary>手機版認證 API — 登入 / 登出 / 目前使用者資訊</summary>
    /// <remarks>
    /// 目前手機 API 與桌機共用 Cookie 認證；待 PWA 推進時將切換至 JWT
    /// （參見 .claude/specifications/mobile-pwa.md §4）。在 JWT 上線前，本控制器
    /// 的 Login 已套用與桌機相同的 rate limit (loginRateLimit)。
    /// </remarks>
    [Route("api/mobile/auth")]
    [ApiController]
    public class MobileAuthController : ControllerBase
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;
        private readonly IUserContext _userContext;

        public MobileAuthController(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext db,
            IUserContext userContext)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _db = db;
            _userContext = userContext;
        }

        /// <summary>手機版登入（與桌面版共用 Cookie）</summary>
        [HttpPost("login")]
        [AllowAnonymous]
        [EnableRateLimiting("loginRateLimit")]   // H2：與桌機 Razor Pages 套用相同 rate limit
        public async Task<IActionResult> Login([FromBody] MobileLoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { message = CoreText.Get("Mobile_CredentialsRequired") });

            var user = await _userManager.FindByNameAsync(req.Username);
            if (user == null)
            {
                await WriteAuditLog("Mobile.Auth", AuditAction.LoginFailed, null, null,
                    $"登入失敗（帳號不存在）：{req.Username}");
                return Unauthorized(new { message = CoreText.Get("Mobile_InvalidCredentials") });
            }

            var result = await _signInManager.PasswordSignInAsync(user, req.Password,
                isPersistent: false, lockoutOnFailure: true);

            if (result.RequiresTwoFactor)
            {
                await WriteAuditLog("Mobile.Auth", AuditAction.Login2faRequired, user.Id, user.ActivePlantId,
                    "手機版登入待輸入兩階段驗證碼");
                // 200（非 401）：apiCall 的 401 攔截器會清空 session 並強制導回登入頁，
                // 導致前端看不到 requiresTwoFactor 旗標，誤以為登入失敗又跳回帳密輸入畫面。
                return Ok(new { success = false, requiresTwoFactor = true, message = CoreText.Get("Mobile_EnterCode") });
            }

            if (!result.Succeeded)
            {
                var reason = result.IsLockedOut ? "帳號已鎖定" : "密碼錯誤";
                await WriteAuditLog("Mobile.Auth", AuditAction.LoginFailed, user.Id, user.ActivePlantId,
                    $"登入失敗（{reason}）");
                return Unauthorized(new { message = result.IsLockedOut ? CoreText.Get("Mobile_LockedOut") : CoreText.Get("Mobile_InvalidCredentials") });
            }

            await WriteAuditLog("Mobile.Auth", AuditAction.Login, user.Id, user.ActivePlantId, "手機版登入成功");
            return Ok(new { success = true, user = await BuildUserPayloadAsync(user) });
        }

        /// <summary>手機版兩階段驗證（TOTP）——密碼正確但帳號啟用 2FA 時的第二步</summary>
        [HttpPost("login-2fa")]
        [AllowAnonymous]
        [EnableRateLimiting("loginRateLimit")]
        public async Task<IActionResult> LoginTwoFactor([FromBody] MobileTwoFactorLoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Code))
                return BadRequest(new { message = CoreText.Get("Mobile_EnterCode") });

            // 讀取 PasswordSignInAsync 觸發 2FA 時寫入的暫存識別 Cookie（IdentityConstants.TwoFactorUserIdScheme）。
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null)
                return Unauthorized(new { message = CoreText.Get("Mobile_TwoFactorExpired") });

            var code = req.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(
                code, isPersistent: false, rememberClient: false);

            if (!result.Succeeded)
            {
                var reason = result.IsLockedOut ? "帳號已鎖定" : "驗證碼錯誤";
                await WriteAuditLog("Mobile.Auth", AuditAction.LoginFailed, user.Id, user.ActivePlantId,
                    $"登入失敗（{reason}）");
                return Unauthorized(new { message = result.IsLockedOut ? CoreText.Get("Mobile_LockedOut") : CoreText.Get("Mobile_InvalidCode") });
            }

            await WriteAuditLog("Mobile.Auth", AuditAction.Login, user.Id, user.ActivePlantId, "手機版登入成功（兩階段驗證）");
            return Ok(new { success = true, user = await BuildUserPayloadAsync(user) });
        }

        /// <summary>登出</summary>
        [HttpPost("logout")]
        [Authorize(Policy = "AuthenticatedUser")]
        public async Task<IActionResult> Logout()
        {
            await WriteAuditLog("Mobile.Auth", AuditAction.Logout,
                _userContext.UserId, _userContext.PlantId, "手機版登出");
            await _signInManager.SignOutAsync();
            return Ok(new { success = true });
        }

        /// <summary>
        /// 手機切換語系：寫 culture cookie，並寫回帳號偏好（推播、下次登入的預設語系讀這個）。
        /// JSON POST——跨站表單送不出 application/json，與 logout 同一層保護。
        /// </summary>
        [HttpPost("language")]
        [Authorize(Policy = "AuthenticatedUser")]
        [PermissionGateExempt("只寫呼叫者自己的語系偏好（WHERE Id = 目前登入者）")]
        public async Task<IActionResult> SetLanguage([FromBody] MobileLanguageRequest req)
        {
            if (req?.Culture == null || !LanguagePreference.Supported.Contains(req.Culture))
                return BadRequest(new { message = CoreText.Get("Msg_UnsupportedLanguage") });

            LanguagePreference.AppendCookie(Response, req.Culture);
            await LanguagePreference.PersistAsync(_db, _userManager.GetUserId(User), req.Culture);
            return Ok(new { success = true });
        }

        /// <summary>取得目前登入使用者資訊</summary>
        [HttpGet("me")]
        [Authorize(Policy = "AuthenticatedUser")]
        public async Task<IActionResult> Me()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            return Ok(await BuildUserPayloadAsync(user));
        }

        private async Task<object> BuildUserPayloadAsync(ApplicationUser user)
        {
            var roles = await _userManager.GetRolesAsync(user);
            return new
            {
                id = user.Id,
                name = user.UserName,
                displayName = user.UserName,
                activeRoleId = user.ActiveRoleId,
                plantId = user.ActivePlantId,
                // 個人 → 廠別預設 → zh-TW，與桌機登入同一份規則（LanguagePreference.Resolve）
                language = await LanguagePreference.ResolveForUserAsync(_db, user),
                roles
            };
        }

        private async Task WriteAuditLog(string entityType, string action, string? userId, string? plantId, string remark)
        {
            try
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    EntityType = entityType,
                    Action = action,
                    EntityId = userId,
                    UserId = userId,
                    PlantId = plantId,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Request.Headers["User-Agent"].ToString(),
                    Changes = System.Text.Json.JsonSerializer.Serialize(new { Remark = remark })
                });
                await _db.SaveChangesAsync();
            }
            catch (Exception)
            {
                // 審計日誌寫入失敗不影響主要操作
            }
        }
    }

    public record MobileLoginRequest(string Username, string Password);
    public record MobileLanguageRequest(string Culture);

    public record MobileTwoFactorLoginRequest(string Code);
}
