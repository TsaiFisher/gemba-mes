using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.Mobile
{
    /// <summary>
    /// 手機切換角色（/api/mobile/roles）與廠別失效旗標（/api/mobile/plants/current 的 invalid）。
    ///
    /// <para>2026-11-11 筆電驗收後使用者要求「手機需要能切換角色」；同次驗收 A4：
    /// 廠別失效時手機只顯示「權限不足」，不知道要切換廠別。</para>
    ///
    /// <para>走真登入＋真 PG：重點在 <c>UserPreferenceMiddleware</c> 的復原路徑——角色或廠別失效時，
    /// 這兩支端點必須還到得了，否則手機沒有出路。專屬角色／廠別／使用者，跑前跑後都清。</para>
    /// </summary>
    [Collection("Integration")]
    public class MobileRoleSwitchTests : IAsyncLifetime
    {
        private const string UserName = "itest-mobile-role";
        private const string Password = "Password123!";
        private const string RoleBoth = "ITEST-MROLE-BOTH";    // 甲＋乙廠
        private const string RoleA    = "ITEST-MROLE-A";       // 只有甲廠（預設甲）
        private const string RoleOther = "ITEST-MROLE-OTHER";  // 使用者不屬於
        private const string PlantA   = "ITMROLE-A";
        private const string PlantB   = "ITMROLE-B";

        private readonly TestWebApplicationFactory _factory;

        public MobileRoleSwitchTests(TestWebApplicationFactory factory) => _factory = factory;

        public async Task InitializeAsync()
        {
            await CleanupAsync();
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            db.Plants.AddRange(new Plant { Id = PlantA, Name = PlantA }, new Plant { Id = PlantB, Name = PlantB });
            await db.SaveChangesAsync();
            await roles.CreateAsync(new ApplicationRole { Id = RoleBoth, Name = RoleBoth, NameZh = "兩廠角色", NameEn = "Both plants" });
            await roles.CreateAsync(new ApplicationRole { Id = RoleA, Name = RoleA, NameZh = "甲廠角色", NameEn = "Plant A only" });
            await roles.CreateAsync(new ApplicationRole { Id = RoleOther, Name = RoleOther });
            db.RolePlantAssignments.AddRange(
                new RolePlantAssignment { RoleId = RoleBoth, PlantId = PlantA, IsDefault = true },
                new RolePlantAssignment { RoleId = RoleBoth, PlantId = PlantB },
                new RolePlantAssignment { RoleId = RoleA, PlantId = PlantA, IsDefault = true });
            await db.SaveChangesAsync();

            var user = new ApplicationUser { UserName = UserName, Email = $"{UserName}@test.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, RoleBoth);
            await users.AddToRoleAsync(user, RoleA);
            user.ActiveRoleId = RoleBoth;
            user.ActivePlantId = PlantB;
            await users.UpdateAsync(user);
        }

        public Task DisposeAsync() => CleanupAsync();

        [Fact]
        public async Task 列出所屬角色_切換後連帶改到新角色的預設廠()
        {
            var client = await LoginAsync();

            var list = await client.GetFromJsonAsync<JsonElement>("/api/mobile/roles");
            Assert.Equal(RoleBoth, list.GetProperty("currentRoleId").GetString());
            var ids = list.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
            Assert.Equal(new[] { RoleA, RoleBoth }.OrderBy(x => x), ids.OrderBy(x => x));
            Assert.DoesNotContain(RoleOther, ids);

            // 新角色只有甲廠、目前在乙廠 → 換到甲廠（與桌機 /switch-role 同一份規則）
            var res = await client.PostAsJsonAsync("/api/mobile/roles/switch", new { roleId = RoleA });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PlantA, body.GetProperty("plantId").GetString());

            var (role, plant) = await ActiveAsync();
            Assert.Equal(RoleA, role);
            Assert.Equal(PlantA, plant);
        }

        [Fact]
        public async Task 不屬於的角色回403_而且沒有動到使用者()
        {
            var client = await LoginAsync();
            var res = await client.PostAsJsonAsync("/api/mobile/roles/switch", new { roleId = RoleOther });
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal((RoleBoth, PlantB), await ActiveAsync());
        }

        [Fact]
        public async Task 角色被移除時手機仍能列出並切換到剩下的角色()
        {
            var client = await LoginAsync();
            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByNameAsync(UserName);
                await users.RemoveFromRoleAsync(user!, RoleBoth);
            }

            // 業務 API 被擋，而且說得出原因
            var blocked = await client.GetAsync("/api/mobile/modules");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            var reason = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ACTIVE_ROLE_REVOKED", reason.GetProperty("error").GetProperty("code").GetString());

            // 復原路徑：清單到得了、被移除的角色不會被標成目前
            var list = await client.GetFromJsonAsync<JsonElement>("/api/mobile/roles");
            Assert.Equal(JsonValueKind.Null, list.GetProperty("currentRoleId").ValueKind);

            var res = await client.PostAsJsonAsync("/api/mobile/roles/switch", new { roleId = RoleA });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/mobile/modules")).StatusCode);
        }

        [Fact]
        public async Task 目前廠別被移出角色時_current回invalid_而不是未設定()
        {
            var client = await LoginAsync();
            var before = await client.GetFromJsonAsync<JsonElement>("/api/mobile/plants/current");
            Assert.Equal(PlantB, before.GetProperty("plantId").GetString());
            Assert.False(before.GetProperty("invalid").GetBoolean());

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.RolePlantAssignments.Where(x => x.RoleId == RoleBoth && x.PlantId == PlantB).ExecuteDeleteAsync();
            }

            var after = await client.GetFromJsonAsync<JsonElement>("/api/mobile/plants/current");
            Assert.Equal(JsonValueKind.Null, after.GetProperty("plantId").ValueKind);
            Assert.True(after.GetProperty("invalid").GetBoolean());

            var blocked = await client.GetAsync("/api/mobile/modules");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            var reason = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("PLANT_CONTEXT_INVALID", reason.GetProperty("error").GetProperty("code").GetString());
        }

        private async Task<HttpClient> LoginAsync()
        {
            // ProjectZ 的登入 cookie 是 SecurePolicy.Always：用 https 才帶得回去
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
            var login = await client.PostAsJsonAsync("/api/mobile/auth/login", new { Username = UserName, Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return client;
        }

        private async Task<(string? Role, string? Plant)> ActiveAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.UserName == UserName);
            return (u.ActiveRoleId, u.ActivePlantId);
        }

        private async Task CleanupAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userIds = db.Users.Where(u => u.UserName == UserName).Select(u => u.Id);
            await db.UserRoles.Where(ur => userIds.Contains(ur.UserId)).ExecuteDeleteAsync();
            await db.Users.Where(u => u.UserName == UserName).ExecuteDeleteAsync();
            await db.RolePlantAssignments.Where(x => x.RoleId == RoleBoth || x.RoleId == RoleA).ExecuteDeleteAsync();
            await db.Roles.Where(r => r.Id == RoleBoth || r.Id == RoleA || r.Id == RoleOther).ExecuteDeleteAsync();
            await db.Plants.IgnoreQueryFilters().Where(p => p.Id == PlantA || p.Id == PlantB).ExecuteDeleteAsync();
        }
    }
}
