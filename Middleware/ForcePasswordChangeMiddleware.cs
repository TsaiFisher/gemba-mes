using Microsoft.AspNetCore.Identity;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Middleware
{
    /// <summary>
    /// 當使用者的 MustChangePassword == true 時，強制重導向至改密頁面。
    /// </summary>
    public class ForcePasswordChangeMiddleware
    {
        private readonly RequestDelegate _next;

        // 允許通過的路徑前綴（不強制重導向）
        private static readonly string[] _allowedPaths =
        [
            "/Account/ForceChangePassword",
            "/Identity/Account/Logout",
            "/Identity/Account/Login",
            "/UserSettings/SetLanguage",
            "/favicon.ico",
            "/lib/",
            "/css/",
            "/js/"
        ];

        public ForcePasswordChangeMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager)
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var path = context.Request.Path.Value ?? string.Empty;
                bool isAllowed = _allowedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    var user = await userManager.GetUserAsync(context.User);
                    if (user?.MustChangePassword == true)
                    {
                        context.Response.Redirect("/Account/ForceChangePassword");
                        return;
                    }
                }
            }

            await _next(context);
        }
    }

    public static class ForcePasswordChangeMiddlewareExtensions
    {
        public static IApplicationBuilder UseForcePasswordChange(this IApplicationBuilder builder)
            => builder.UseMiddleware<ForcePasswordChangeMiddleware>();
    }
}
