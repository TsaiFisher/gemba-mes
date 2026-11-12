using System.Security.Cryptography;

namespace SmartFactory.MES.Middleware
{
    /// <summary>
    /// 每個請求產生一個隨機 nonce，存入 HttpContext.Items["CspNonce"]。
    /// View 以 @(Context.Items["CspNonce"] as string) 取值，加在 &lt;script nonce="..."&gt; 上。
    /// 待所有含 inline &lt;script&gt; 的 cshtml 都補上 nonce 後，
    /// Program.cs 的 CSP 標頭即以 'nonce-{value}' 取代 'unsafe-inline'。
    /// </summary>
    public class CspNonceMiddleware(RequestDelegate next)
    {
        public const string ItemKey = "CspNonce";

        public async Task InvokeAsync(HttpContext context)
        {
            var bytes = RandomNumberGenerator.GetBytes(16);
            context.Items[ItemKey] = Convert.ToBase64String(bytes)
                                             .Replace('+', '-')
                                             .Replace('/', '_')
                                             .TrimEnd('=');
            await next(context);
        }
    }

    public static class CspNonceMiddlewareExtensions
    {
        public static IApplicationBuilder UseCspNonce(this IApplicationBuilder app)
            => app.UseMiddleware<CspNonceMiddleware>();
    }
}
