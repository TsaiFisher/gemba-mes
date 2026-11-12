using Microsoft.AspNetCore.Identity;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Middleware
{
    /// <summary>
    /// 安全修正 M3：強制指定角色啟用兩階段驗證（2FA）。
    ///
    /// <para>被強制角色（預設 <c>Admin</c>，可經設定 <c>Security:TwoFactorRequiredRoles</c> 加列）
    /// 若 <c>TwoFactorEnabled == false</c>，一律重導向 <c>/TwoFactor/Setup</c>——比照
    /// <see cref="ForcePasswordChangeMiddleware"/> 的硬擋語意，未啟用即無法使用其他頁面。</para>
    ///
    /// <para>置於 <c>UseForcePasswordChange()</c> 之後：先強制改密、再強制 2FA。</para>
    ///
    /// <para><c>Security:TwoFactorEnforcementEnabled</c>（預設 <c>true</c>）設為 <c>false</c> 時整組強制
    /// 停用（開發/測試環境用）——JSON 設定的空陣列 <c>[]</c> 與「未設定」在 <see cref="IConfiguration"/>
    /// 無法區分（皆無子鍵），單靠 <c>TwoFactorRequiredRoles: []</c> 無法停用，故另立這個明確旗標；
    /// 停用僅影響「強制」，使用者仍可在 <c>/TwoFactor/Setup</c> 自助啟用 2FA。</para>
    /// </summary>
    public class RequireTwoFactorForAdminMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string[] _requiredRoles;

        // 允許通過（不強制重導）的路徑前綴。
        private static readonly string[] _allowedPaths =
        [
            "/TwoFactor/",                       // 設定/驗證/復原碼頁本身
            "/Account/ForceChangePassword",      // 先改密（若同時待改密）
            "/Identity/Account/Logout",
            "/Identity/Account/Login",
            "/UserSettings/SetLanguage",
            "/switch-role",
            "/favicon.ico",
            "/lib/",
            "/css/",
            "/js/",
            "/Error",
        ];

        public RequireTwoFactorForAdminMiddleware(RequestDelegate next, IConfiguration configuration)
        {
            _next = next;
            // 預設僅 Admin；可經設定加列其他角色名稱。現場共用終端角色不列入即豁免。
            var enforcementEnabled = configuration.GetValue("Security:TwoFactorEnforcementEnabled", true);
            var configured = configuration.GetSection("Security:TwoFactorRequiredRoles").Get<string[]>();
            _requiredRoles = ResolveRequiredRoles(enforcementEnabled, configured);
        }

        /// <summary>依「是否啟用強制」與設定角色清單，決出實際生效的強制角色清單（純函式，供測試）。</summary>
        public static string[] ResolveRequiredRoles(bool enforcementEnabled, string[]? configuredRoles)
            => !enforcementEnabled
                ? Array.Empty<string>()
                : (configuredRoles is { Length: > 0 } ? configuredRoles : new[] { "Admin" });

        public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager)
        {
            if (context.User.Identity?.IsAuthenticated == true
                && !IsAllowedPath(context.Request.Path.Value ?? string.Empty))
            {
                // 只有路徑非白名單時才查 DB（避免每個靜態資源都打 DB）。
                var user = await userManager.GetUserAsync(context.User);
                if (user is { TwoFactorEnabled: false })
                {
                    var roles = await userManager.GetRolesAsync(user);
                    if (RolesRequire2Fa(roles, _requiredRoles))
                    {
                        context.Response.Redirect("/TwoFactor/Setup");
                        return;
                    }
                }
            }

            await _next(context);
        }

        /// <summary>路徑是否在免強制白名單（純函式，供測試）。</summary>
        public static bool IsAllowedPath(string path)
            => _allowedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        /// <summary>使用者角色是否命中強制清單（大小寫不敏感，純函式，供測試）。</summary>
        public static bool RolesRequire2Fa(IEnumerable<string> userRoles, IEnumerable<string> requiredRoles)
            => userRoles.Any(r => requiredRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    public static class RequireTwoFactorForAdminMiddlewareExtensions
    {
        public static IApplicationBuilder UseRequireTwoFactorForAdmin(this IApplicationBuilder builder)
            => builder.UseMiddleware<RequireTwoFactorForAdminMiddleware>();
    }
}
