using SmartFactory.MES.Data;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Middleware
{
    public class UserPreferenceMiddleware
    {
        private readonly RequestDelegate _next;

        public UserPreferenceMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        /// <summary>403 回應的錯誤代碼：作用中廠別已不在角色可用的廠別內（手機據此提示切換廠別）。</summary>
        public const string PlantContextInvalidCode = "PLANT_CONTEXT_INVALID";

        /// <summary>403 回應的錯誤代碼：作用中角色已被移除。</summary>
        public const string ActiveRoleRevokedCode = "ACTIVE_ROLE_REVOKED";

        // /api/mobile/roles：手機的角色清單與切換（部署專案提供）。作用中角色被移除時，
        // 手機必須還能列出並切換到剩下的角色，否則只能回電腦處理。
        private static bool IsRecoveryPath(PathString path) =>
            new[] { "/UserSettings", "/Account", "/switch-role", "/Identity", "/TwoFactor", "/Error",
                    "/api/mobile/plants", "/api/mobile/roles", "/api/mobile/auth" }
                .Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

        private static bool IsPageNavigation(HttpRequest request) =>
            HttpMethods.IsGet(request.Method) &&
            !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            request.Headers.XRequestedWith != "XMLHttpRequest" &&
            request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

        public async Task InvokeAsync(HttpContext context, IUserContext userContext, ApplicationDbContext dbContext)
        {
            // 先非同步載入使用者：底下的屬性讀取才不會在每個請求裡同步查資料庫（見 UserContext.EnsureLoadedAsync）
            if (userContext is UserContext loadable)
                await loadable.EnsureLoadedAsync(context.RequestAborted);

            // Recovery routes remain available after an administrator narrows plant access.
            if (userContext is UserContext current && current.HasStaleAuthorization &&
                !IsRecoveryPath(context.Request.Path))
            {
                // 從未選過廠別不是授權失效：先補上角色預設廠別，補成功就照常往下走。
                if (!await current.TryInitializeDefaultPlantAsync(context.RequestAborted) ||
                    current.HasStaleAuthorization)
                {
                    // 瀏覽器直接開頁面時導到使用者設定重選角色／廠別，否則使用者只看到一張沒有出路的 403。
                    if (IsPageNavigation(context.Request))
                    {
                        context.Response.Redirect("/UserSettings");
                        return;
                    }

                    // API、AJAX 與寫入請求維持 403，但要**說出原因**：原本只回空白 403，
                    // 手機只能顯示通用的「權限不足」，使用者不知道問題其實是廠別失效、切換廠別就能恢復
                    //（2026-11-11 筆電驗收 A4）。格式同時相容手機 { error:{code,message} }
                    // 與桌機面板 { success:false, message }。
                    var plantInvalid = current.HasInvalidPlantContext;
                    var message = CoreText.Get(plantInvalid ? "Msg_PlantContextInvalid" : "Msg_ActiveRoleRevoked");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message,
                        error = new { code = plantInvalid ? PlantContextInvalidCode : ActiveRoleRevokedCode, message },
                    }, context.RequestAborted);
                    return;
                }
            }

            // 設定 DbContext 的當前廠別
            var plantId = userContext.PlantId;
            
            // 若使用者已登入但未設定 PlantId，記錄警告
            if (context.User?.Identity?.IsAuthenticated == true && string.IsNullOrEmpty(plantId))
            {
                // 可選：拋出例外或設定預設廠別
                // throw new InvalidOperationException("User must have a PlantId assigned");
                
                // 目前採用記錄警告的方式
                var logger = context.RequestServices.GetRequiredService<ILogger<UserPreferenceMiddleware>>();
                logger.LogWarning("User {UserId} is authenticated but has no PlantId assigned", userContext.UserId);
            }
            
            dbContext.CurrentPlantId = plantId;
            dbContext.CurrentRoleId = userContext.ActiveRoleId;
            dbContext.CurrentRolePath = userContext.ActiveRolePath;

            // 設定文化語言由 RequestLocalizationMiddleware 處理
            // 我們只需要確保此 Middleware 在它之後執行即可讀取到正確的 Culture
            
            await _next(context);
        }
    }

    public static class UserPreferenceMiddlewareExtensions
    {
        public static IApplicationBuilder UseUserPreference(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<UserPreferenceMiddleware>();
        }
    }
}
