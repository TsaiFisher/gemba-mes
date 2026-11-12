using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

public class RolePlantBoundaryTests : TestBase
{
    private async Task<ApplicationUser> Seed(bool membership = true, string? activePlant = "B")
    {
        _context.Plants.AddRange(new Plant { Id = "A", Name = "A" }, new Plant { Id = "B", Name = "B" });
        var role = new ApplicationRole { Id = "limited", Name = "Limited" };
        role.PlantAssignments.Add(new RolePlantAssignment { RoleId = role.Id, PlantId = "A", IsDefault = true });
        _context.Roles.Add(role);
        var user = new ApplicationUser { Id = "review-user", UserName = "review", ActiveRoleId = role.Id, ActivePlantId = activePlant };
        _context.Users.Add(user);
        if (membership) _context.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task SwitchRole_CorrectsPlantAndRefreshesSignIn()
    {
        var user = await Seed();
        var users = GetMockUserManager();
        users.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        users.Setup(x => x.UpdateAsync(user)).Returns(async () => { await _context.SaveChangesAsync(); return IdentityResult.Success; });
        var signIn = GetMockSignInManager(users.Object);
        var service = new RoleSwitchService(_context, users.Object, signIn.Object);

        Assert.Equal(RoleSwitchOutcome.Switched, await service.TrySwitchAsync(new ClaimsPrincipal(), "limited"));
        _context.ChangeTracker.Clear();
        Assert.Equal("A", (await _context.Users.SingleAsync(x => x.Id == user.Id)).ActivePlantId);
        signIn.Verify(x => x.RefreshSignInAsync(user), Times.Once);
    }

    [Fact]
    public async Task SwitchRole_RejectsRevokedMembershipWithoutSaving()
    {
        var user = await Seed(membership: false);
        var users = GetMockUserManager();
        users.Setup(x => x.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        var service = new RoleSwitchService(_context, users.Object, GetMockSignInManager(users.Object).Object);
        Assert.Equal(RoleSwitchOutcome.NotAllowed, await service.TrySwitchAsync(new ClaimsPrincipal(), "limited"));
        Assert.Equal("B", user.ActivePlantId);
        users.Verify(x => x.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
        Assert.Empty((await new PlantSwitchService(_context).GetSwitchablePlantsAsync(user.Id)).Plants);
        Assert.Equal(PlantSwitchOutcome.NotAllowed, await new PlantSwitchService(_context).TrySwitchAsync(user.Id, "B"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Context_RejectsStalePlantWithoutMutatingTrackedUser(bool asyncLoad)
    {
        var user = await Seed();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "review"));
        var users = GetMockUserManager();
        users.Setup(x => x.Users).Returns(_context.Users);
        users.Setup(x => x.GetUserId(principal)).Returns(user.Id);
        var context = new UserContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } }, users.Object, _context);
        if (asyncLoad) await context.EnsureLoadedAsync();
        Assert.Null(context.PlantId);
        Assert.Null(context.ActiveRoleId);
        Assert.Equal("B", user.ActivePlantId);
        Assert.Equal(EntityState.Unchanged, _context.Entry(user).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Middleware_BlocksBusinessButKeepsRecoveryRoutes(bool membership)
    {
        var user = await Seed(membership);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "review"));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { User = principal, RequestServices = services };
        http.Request.Path = "/Organization/Create";
        var users = GetMockUserManager();
        users.Setup(x => x.Users).Returns(_context.Users);
        users.Setup(x => x.GetUserId(principal)).Returns(user.Id);
        var context = new UserContext(new HttpContextAccessor { HttpContext = http }, users.Object, _context);
        var executed = false;
        var middleware = new SmartFactory.MES.Middleware.UserPreferenceMiddleware(_ => { executed = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(http, context, _context);
        Assert.False(executed);
        Assert.Equal(403, http.Response.StatusCode);
        http.Request.Path = "/api/mobile/plants";
        await middleware.InvokeAsync(http, context, _context);
        Assert.True(executed);
        executed = false;
        http.Request.Path = "/api/mobile/roles/switch";
        await middleware.InvokeAsync(http, context, _context);
        Assert.True(executed);
    }
    private (DefaultHttpContext http, UserContext context) Request(ApplicationUser user, string path,
        string accept = "", ServiceProvider? services = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "review"));
        var http = new DefaultHttpContext { User = principal, RequestServices = services! };
        http.Request.Path = path;
        http.Request.Method = "GET";
        if (accept.Length > 0) http.Request.Headers.Accept = accept;
        var users = GetMockUserManager();
        users.Setup(x => x.Users).Returns(_context.Users);
        users.Setup(x => x.GetUserId(principal)).Returns(user.Id);
        return (http, new UserContext(new HttpContextAccessor { HttpContext = http }, users.Object, _context));
    }

    // 新建使用者沒有 ActivePlantId：不得被當成授權失效而全站 403，應補上角色預設廠別後放行。
    [Fact]
    public async Task Middleware_NeverSelectedPlant_InitializesRoleDefaultAndContinues()
    {
        var user = await Seed(activePlant: null);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var (http, context) = Request(user, "/Home/Index", services: services);
        var executed = false;
        var middleware = new SmartFactory.MES.Middleware.UserPreferenceMiddleware(_ => { executed = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(http, context, _context);

        Assert.True(executed);
        Assert.Equal("A", context.PlantId);
        Assert.Equal("limited", context.ActiveRoleId);
        Assert.Equal("A", _context.CurrentPlantId);
        _context.ChangeTracker.Clear();
        Assert.Equal("A", (await _context.Users.SingleAsync(x => x.Id == user.Id)).ActivePlantId);
    }

    // 已被移出角色時不補廠別：補了也不能放行，且不該寫入資料。
    [Fact]
    public async Task Middleware_NeverSelectedPlant_RevokedRole_StaysBlockedWithoutWrite()
    {
        var user = await Seed(membership: false, activePlant: null);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var (http, context) = Request(user, "/Home/Index", services: services);
        var executed = false;
        var middleware = new SmartFactory.MES.Middleware.UserPreferenceMiddleware(_ => { executed = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(http, context, _context);

        Assert.False(executed);
        Assert.Equal(403, http.Response.StatusCode);
        _context.ChangeTracker.Clear();
        Assert.Null((await _context.Users.SingleAsync(x => x.Id == user.Id)).ActivePlantId);
    }

    // 選過但已失效的廠別不靜默搬家；瀏覽器開頁面導向使用者設定，API／AJAX 維持 403。
    [Theory]
    [InlineData("/Home/Index", "text/html,application/xhtml+xml", 302)]
    [InlineData("/Home/Index", "", 403)]
    [InlineData("/api/mobile/stock", "text/html", 403)]
    public async Task Middleware_StaleSelectedPlant_RedirectsPagesOnly(string path, string accept, int status)
    {
        var user = await Seed();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var (http, context) = Request(user, path, accept, services);
        var middleware = new SmartFactory.MES.Middleware.UserPreferenceMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(http, context, _context);

        Assert.Equal(status, http.Response.StatusCode);
        if (status == 302) Assert.Equal("/UserSettings", http.Response.Headers.Location.ToString());
        _context.ChangeTracker.Clear();
        Assert.Equal("B", (await _context.Users.SingleAsync(x => x.Id == user.Id)).ActivePlantId);
    }
    // API／AJAX 的 403 要說出原因：手機原本只能顯示通用「權限不足」，不知道切換廠別就能恢復
    //（2026-11-11 筆電驗收 A4）。廠別失效與角色被移除各有代碼，訊息同時放兩種格式。
    [Theory]
    [InlineData(true, "PLANT_CONTEXT_INVALID")]
    [InlineData(false, "ACTIVE_ROLE_REVOKED")]
    public async Task Middleware_ApiForbidden_ExplainsReasonInBody(bool membership, string code)
    {
        var user = await Seed(membership);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var (http, context) = Request(user, "/api/mobile/machines/M1", services: services);
        var body = new MemoryStream();
        http.Response.Body = body;
        var middleware = new SmartFactory.MES.Middleware.UserPreferenceMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(http, context, _context);

        Assert.Equal(403, http.Response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(body.ToArray());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.NotEqual(code == "PLANT_CONTEXT_INVALID" ? "Msg_PlantContextInvalid" : "Msg_ActiveRoleRevoked", message);
        Assert.Equal(message, json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
