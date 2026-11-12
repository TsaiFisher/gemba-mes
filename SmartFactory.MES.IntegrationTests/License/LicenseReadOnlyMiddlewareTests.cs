using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Middleware;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.License;

/// <summary>
/// 授權唯讀 middleware 在真的 HTTP 管線上的行為（SECURITY-OPERATIONS.md §18.8）。
/// 用最小的 TestServer，不啟動整個 app——不碰共用測試庫，也不需要授權檔。
/// 放行判定的完整矩陣在單元測試 LicenseReadOnlyRulesTests。
/// </summary>
public class LicenseReadOnlyMiddlewareTests
{
    private sealed class FixedState(LicenseState s) : ILicenseStateService
    {
        public LicenseStatus Current => new(s, "測試", null, null, null);
        public void SetLicense(LicenseContext license) { }
        public void SetRevoked(DateTimeOffset revokedAt, string reason) { }
        public void SetSunset() { }
    }

    private static async Task<HttpClient> Client(LicenseState state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILicenseStateService>(new FixedState(state));
        var app = builder.Build();
        app.UseLicenseReadOnly();
        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "DELETE"], () => Results.Text("reached"));
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task ReadOnly_GetPasses()
    {
        var c = await Client(LicenseState.ReadOnly);
        Assert.Equal("reached", await c.GetStringAsync("/PUR/Index"));
    }

    [Fact]
    public async Task ReadOnly_AjaxPost_Gets403Json_InPanelFormat()
    {
        var c = await Client(LicenseState.ReadOnly);
        var req = new HttpRequestMessage(HttpMethod.Post, "/PUR/Save");
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var res = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("False", body!["success"].ToString());
        Assert.Equal("ReadOnly", body["licenseState"].ToString());
    }

    [Fact]
    public async Task ReadOnly_ApiPut_Gets403()
    {
        var c = await Client(LicenseState.ReadOnly);
        var res = await c.PutAsync("/api/mobile/material/1", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_FormPost_Gets403Page_NotReached()
    {
        var c = await Client(LicenseState.ReadOnly);
        var res = await c.PostAsync("/PUR/Save", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.DoesNotContain("reached", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadOnly_LoginPost_Passes()
    {
        var c = await Client(LicenseState.ReadOnly);
        var res = await c.PostAsync("/Identity/Account/Login", new FormUrlEncodedContent([]));
        Assert.Equal("reached", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Disabled_PageRequest_RedirectsToLicenseStatus()
    {
        var c = await Client(LicenseState.Disabled);
        var res = await c.GetAsync("/PUR/Index");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(LicenseReadOnlyMiddleware.StatusPath, res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Normal_PostPasses()
    {
        var c = await Client(LicenseState.Normal);
        var res = await c.PostAsync("/PUR/Save", new FormUrlEncodedContent([]));
        Assert.Equal("reached", await res.Content.ReadAsStringAsync());
    }
}
