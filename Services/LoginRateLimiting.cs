using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartFactory.MES.Services;

public static class LoginRateLimiting
{
    public static bool IsCredentialAttempt(HttpRequest request) => HttpMethods.IsPost(request.Method) &&
        new[] { "/Identity/Account/Login", "/Identity/Account/LoginWith2fa",
                "/Identity/Account/LoginWithRecoveryCode", "/api/mobile/auth/login", "/api/mobile/auth/login-2fa" }
            .Contains(request.Path.Value?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddMesLoginRateLimiting(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var limit = Math.Clamp(configuration.GetValue("Security:LoginIpPermitLimit", 1200), 1, 100000);
        var minutes = Math.Clamp(configuration.GetValue("Security:LoginWindowMinutes", 15), 1, 60);
        var concurrency = Math.Clamp(configuration.GetValue("Security:LoginConcurrencyLimit", 16), 1, 256);
        return services.AddRateLimiter(options =>
        {
            options.AddPolicy("loginRateLimit", context =>
            {
                if (!IsCredentialAttempt(context.Request)) return RateLimitPartition.GetNoLimiter("other");
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = environment.IsDevelopment() ? Math.Max(1200, limit) : limit,
                    Window = TimeSpan.FromMinutes(minutes), QueueLimit = 0, AutoReplenishment = true
                });
            });
            // Bound expensive password verification across all IPs. Identity also locks each account.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsCredentialAttempt(context.Request)
                    ? RateLimitPartition.GetConcurrencyLimiter("credentials", _ => new ConcurrencyLimiterOptions
                        { PermitLimit = concurrency, QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter("other"));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) =>
            {
                var alerts = context.HttpContext.RequestServices.GetService<Security.ISecurityAlertService>();
                if (alerts != null)
                    _ = alerts.RaiseAsync("LoginRateLimitExceeded", "Credential request rate limit exceeded",
                        context.HttpContext.Connection.RemoteIpAddress?.ToString());
                return ValueTask.CompletedTask;
            };
        });
    }
}
