using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests;

/// <summary>Exercise the real middleware without booting MES or opening a database.</summary>
public class LoginRateLimitingTests
{
    [Fact]
    public async Task CredentialLimits_ArePartitioned_AndDoNotCountPageViews()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:LoginIpPermitLimit"] = "2" }).Build();
        using var host = await new HostBuilder().UseEnvironment("Production")
            // WebHost reads ASPNETCORE_ENVIRONMENT separately from the generic host.
            // Pin both so CI's Development environment cannot relax this production-policy test.
            .ConfigureWebHost(web => web.UseEnvironment(Environments.Production).UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    Assert.True(context.HostingEnvironment.IsProduction());
                    services.AddMesLoginRateLimiting(config, context.HostingEnvironment);
                })
                .Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-IP"].ToString());
                        return next(context);
                    });
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/Identity/Account/Login", () => "ok").RequireRateLimiting("loginRateLimit");
                        endpoints.MapPost("/Identity/Account/Login", () => "ok").RequireRateLimiting("loginRateLimit");
                        endpoints.MapPost("/Identity/Account/LoginWith2fa", () => "ok").RequireRateLimiting("loginRateLimit");
                        endpoints.MapPost("/Identity/Account/Manage", () => "ok").RequireRateLimiting("loginRateLimit");
                    });
                })).StartAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-IP", "198.51.100.1");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Identity/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Identity/Account/Login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Identity/Account/LoginWith2fa", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/Identity/Account/Login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Identity/Account/Manage", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-IP");
        client.DefaultRequestHeaders.Add("X-Test-IP", "198.51.100.2");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/Identity/Account/Login", null)).StatusCode);
    }
}
