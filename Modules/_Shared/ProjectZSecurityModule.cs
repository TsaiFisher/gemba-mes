using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules._Shared;

public sealed class ProjectZSecurityModule : IAppModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
            services.AddRateLimiter(options => options.AddPolicy("pushRateLimit", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                        ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }
    public void Configure(WebApplication app) { }
}
