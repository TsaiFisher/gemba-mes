using SmartFactory.MES.Services;

namespace SmartFactory.MES.Middleware
{
    /// <summary>
    /// 授權唯讀／停用的 HTTP 層強制（SECURITY-OPERATIONS.md §18.8）。
    ///
    /// <list type="bullet">
    ///   <item><b>唯讀</b>：GET／HEAD／OPTIONS 照常；其餘方法除白名單外一律拒絕。
    ///         AJAX 與 API 回 <c>{ success:false, message }</c>（面板既有的錯誤呈現格式），
    ///         一般表單送出則回 403 頁。</item>
    ///   <item><b>停用</b>：除白名單外一律導向授權狀態頁，API 回 403。</item>
    /// </list>
    ///
    /// <para>資料庫層的 <c>LicenseReadOnlyInterceptor</c> 是最後保證；這一層的作用是在使用者
    /// 填完表單之前、或在背景服務之外的入口，給出看得懂的訊息。</para>
    /// </summary>
    public class LicenseReadOnlyMiddleware(RequestDelegate next)
    {
        /// <summary>授權狀態頁（第 6 步 C 段：狀態、換發匯入）。</summary>
        public const string StatusPath = "/License";

        // 唯讀與停用時都要能用的路徑：登入登出、強制改密與 2FA、語系、授權頁本身
        private static readonly string[] _alwaysAllowed =
        [
            StatusPath,
            "/Identity/Account/Login",
            "/Identity/Account/Logout",
            "/Identity/Account/LoginWith2fa",
            "/Identity/Account/LoginWithRecoveryCode",
            "/Account/ForceChangePassword",
            "/TwoFactor/",
            "/UserSettings/SetLanguage",
            "/switch-role",
            "/api/mobile/auth/",
        ];

        // 停用時額外放行的靜態資源與健康檢查（唯讀時 GET 本來就放行）
        private static readonly string[] _staticPrefixes =
        [
            "/lib/", "/css/", "/js/", "/images/", "/favicon.ico", "/health", "/Error",
        ];

        public async Task InvokeAsync(HttpContext context, ILicenseStateService state)
        {
            var status = state.Current;
            if (!status.IsWriteBlocked)
            {
                await next(context);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;
            var decision = Decide(status.State, context.Request.Method, path);

            if (decision == Decision.Allow)
            {
                await next(context);
                return;
            }

            // 所有使用者都會看到這句（不只管理員），走資源檔；到期日等細節在授權狀態頁
            var message = CoreText.Get(status.State == LicenseState.Disabled
                ? "Msg_LicenseDisabledBlocked" : "Msg_LicenseReadOnlyBlocked");

            if (decision == Decision.RedirectToStatus && !IsAjaxOrApi(context.Request))
            {
                context.Response.Redirect(StatusPath);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            if (IsAjaxOrApi(context.Request))
            {
                await context.Response.WriteAsJsonAsync(new { success = false, message, licenseState = status.State.ToString() });
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            var title = System.Net.WebUtility.HtmlEncode(CoreText.Get("Lbl_LicenseBlockedTitle"));
            await context.Response.WriteAsync(
                $"<!doctype html><meta charset=\"utf-8\"><title>{title}</title>" +
                "<div style=\"font-family:sans-serif;max-width:40rem;margin:4rem auto;line-height:1.6\">" +
                $"<h1>{title}</h1><p>{System.Net.WebUtility.HtmlEncode(message)}</p>" +
                $"<p><a href=\"{StatusPath}\">{System.Net.WebUtility.HtmlEncode(CoreText.Get("Lbl_LicenseStatusLink"))}</a> · " +
                $"<a href=\"/\">{System.Net.WebUtility.HtmlEncode(CoreText.Get("Lbl_LicenseGoBack"))}</a></p></div>");
        }

        public enum Decision { Allow, Block, RedirectToStatus }

        /// <summary>判定（純函式，供測試）。</summary>
        public static Decision Decide(LicenseState state, string method, string path)
        {
            if (state is not (LicenseState.ReadOnly or LicenseState.Disabled)) return Decision.Allow;
            if (StartsWithAny(path, _alwaysAllowed)) return Decision.Allow;

            if (state == LicenseState.Disabled)
                return StartsWithAny(path, _staticPrefixes) ? Decision.Allow : Decision.RedirectToStatus;

            return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
                ? Decision.Allow
                : Decision.Block;
        }

        private static bool StartsWithAny(string path, string[] prefixes)
            => prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        private static bool IsAjaxOrApi(HttpRequest r)
            => r.Headers.XRequestedWith == "XMLHttpRequest"
               || r.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
               || r.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    public static class LicenseReadOnlyMiddlewareExtensions
    {
        public static IApplicationBuilder UseLicenseReadOnly(this IApplicationBuilder app)
            => app.UseMiddleware<LicenseReadOnlyMiddleware>();
    }
}
