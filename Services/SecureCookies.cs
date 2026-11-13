using Microsoft.AspNetCore.CookiePolicy;

namespace SmartFactory.MES.Services;

public static class SecureCookies
{
    /// <summary>
    /// HTTPS 請求寫出的每一個 cookie 都補上 Secure。
    /// 登入 cookie 自己有設 SecurePolicy，但防偽（antiforgery）、TempData、語系偏好各有各的寫入點，
    /// 預設都不帶 Secure；逐一設定的話，日後多一種 cookie 就會漏一個。這裡在管線上統一處理。
    /// 用 SameAsRequest 而不是 Always：HTTP 請求（本機開發、區網 HTTP 測試）維持不帶，
    /// 否則非 localhost 的 HTTP 環境收不到防偽 cookie，所有表單送出都會 400。
    /// 經反向代理終止 TLS 時，須排在 UseForwardedHeaders 之後——那時請求的 scheme 才是 https。
    /// </summary>
    public static IApplicationBuilder UseMesSecureCookies(this IApplicationBuilder app) =>
        app.UseCookiePolicy(new CookiePolicyOptions
        {
            Secure = CookieSecurePolicy.SameAsRequest,
            // 只補 Secure；SameSite／HttpOnly 維持各 cookie 自己的設定
            MinimumSameSitePolicy = SameSiteMode.Unspecified,
            HttpOnly = HttpOnlyPolicy.None,
        });
}
