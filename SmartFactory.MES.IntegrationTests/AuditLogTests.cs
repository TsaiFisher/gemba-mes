using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.IntegrationTests
{
    [Collection("Integration")]
    public class AuditLogIntegrationTests
    {
        private readonly TestWebApplicationFactory _factory;

        public AuditLogIntegrationTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Updating_User_Creates_AuditLog_With_User_EntityType()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var testUserName = "auditTestUser";
            var user = await userManager.FindByNameAsync(testUserName);
            if (user == null)
            {
                user = new ApplicationUser { UserName = testUserName, Email = "audit@test.com" };
                await userManager.CreateAsync(user, "Password123!");
            }

            user.Language = "en-US";
            await userManager.UpdateAsync(user);

            var log = await context.AuditLogs
                .OrderByDescending(l => l.Timestamp)
                .FirstOrDefaultAsync(l => l.EntityId == user.Id && l.Action == "Modified");

            Assert.NotNull(log);
            Assert.Equal("User", log.EntityType);
            Assert.Contains("Language", log.Changes ?? "");
        }
            /// <summary>
        /// 資料庫產生的主鍵（identity）在存檔前是 EF 給的暫時負數；稽核原本就在那個時間點寫，
        /// 於是 EntityId 記成 -2147482645 之類，查不回是哪一筆（10/21 實測穩定版庫數千筆）。
        /// AspNetRoleClaims 的 Id 就是 identity，Core 裡最容易拿來驗的一張。
        /// </summary>
        [Fact]
        public async Task 資料庫產生的主鍵_稽核記的是存檔後的真實值而非暫時負數()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roleId = await context.Roles.Select(r => r.Id).FirstAsync();
            var claim = new IdentityRoleClaim<string> { RoleId = roleId, ClaimType = "audit-test", ClaimValue = Guid.NewGuid().ToString() };
            context.RoleClaims.Add(claim);
            try
            {
                await context.SaveChangesAsync();
                Assert.True(claim.Id > 0);

                var log = await context.AuditLogs.IgnoreQueryFilters()
                    .SingleAsync(l => l.EntityType == "RolePermission" && l.Changes!.Contains(claim.ClaimValue!));
                Assert.Equal(claim.Id.ToString(), log.EntityId);
                Assert.Contains($"\"Id\":{claim.Id}", log.Changes);
                Assert.Equal(AuditAction.Added, log.Action);
            }
            finally
            {
                context.RoleClaims.Remove(claim);
                await context.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task 外層交易回滾_延後補寫的稽核一併回滾()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roleId = await context.Roles.Select(r => r.Id).FirstAsync();
            var marker = Guid.NewGuid().ToString();

            await using (var tx = await context.Database.BeginTransactionAsync())
            {
                context.RoleClaims.Add(new IdentityRoleClaim<string> { RoleId = roleId, ClaimType = "audit-test", ClaimValue = marker });
                await context.SaveChangesAsync();
                await tx.RollbackAsync();
            }
            context.ChangeTracker.Clear();

            Assert.False(await context.RoleClaims.AnyAsync(c => c.ClaimValue == marker));
            Assert.False(await context.AuditLogs.IgnoreQueryFilters().AnyAsync(l => l.Changes!.Contains(marker)));
        }

        [Fact]
        public async Task 存檔失敗_暫存的稽核不會被下一次成功的存檔帶出去()
        {
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var marker = Guid.NewGuid().ToString();

            // 指向不存在的角色：外鍵違規，存檔失敗。一筆是 identity 主鍵（稽核延後補寫）、
            // 一筆是字串複合主鍵（稽核當下就排入）——兩條路徑都不得留下稽核。
            var bad = new IdentityRoleClaim<string> { RoleId = "no-such-role-" + marker, ClaimType = "audit-test", ClaimValue = marker };
            var badAssign = new RolePlantAssignment { RoleId = "no-such-role-" + marker, PlantId = "no-such-plant" };
            context.RoleClaims.Add(bad);
            context.RolePlantAssignments.Add(badAssign);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.Entry(bad).State = EntityState.Detached;
            context.Entry(badAssign).State = EntityState.Detached;

            var user = new ApplicationUser { UserName = "audit-ok-" + marker[..8], Email = "a@b.c" };
            context.Users.Add(user);
            try
            {
                await context.SaveChangesAsync();
                Assert.False(await context.AuditLogs.IgnoreQueryFilters().AnyAsync(l => l.Changes!.Contains(marker)));
            }
            finally
            {
                context.Users.Remove(user);
                await context.SaveChangesAsync();
            }
        }
    }
}
