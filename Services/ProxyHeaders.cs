using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SmartFactory.MES.Services;

public static class ProxyHeaders
{
    public static IServiceCollection AddMesProxyHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Retain the framework's loopback defaults; trust additional proxies only by explicit IP.
            foreach (var proxy in configuration.GetSection("Security:KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
    }
}
