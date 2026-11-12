using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// L4：密碼歷史驗證器 — 禁止重用最近 N 次密碼。
    /// </summary>
    public class PasswordHistoryValidatorTests
    {
        private static readonly IPasswordHasher<ApplicationUser> _hasher = new PasswordHasher<ApplicationUser>();

        private static ApplicationDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"pwdhist-{Guid.NewGuid()}")
                .Options;
            return new ApplicationDbContext(options);
        }

        private static UserManager<ApplicationUser> ManagerWithHasher()
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            var mgr = new Mock<UserManager<ApplicationUser>>(
                store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            mgr.Object.PasswordHasher = _hasher;
            return mgr.Object;
        }

        private static PasswordHistoryValidator Validator(ApplicationDbContext db, int count = 5)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:PasswordHistoryCount"] = count.ToString()
                })
                .Build();
            return new PasswordHistoryValidator(db, config);
        }

        private static void Seed(ApplicationDbContext db, ApplicationUser user, DateTime at, string password)
        {
            db.Set<PasswordHistory>().Add(new PasswordHistory
            {
                UserId = user.Id,
                PasswordHash = _hasher.HashPassword(user, password),
                CreatedAt = at
            });
            db.SaveChanges();
        }

        [Fact]
        public async Task ReusedPassword_IsRejected()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "OldPass123");

            var result = await Validator(db).ValidateAsync(ManagerWithHasher(), user, "OldPass123");

            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, e => e.Code == "PasswordRecentlyUsed");
        }

        [Fact]
        public async Task NewPassword_IsAccepted()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "OldPass123");

            var result = await Validator(db).ValidateAsync(ManagerWithHasher(), user, "BrandNew456");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task PasswordBeyondHistoryWindow_IsAccepted()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            // 三筆歷史，N=2 → 最舊的一筆（Oldest123）落在視窗外，應可再用
            Seed(db, user, DateTime.UtcNow.AddDays(-3), "Oldest123");
            Seed(db, user, DateTime.UtcNow.AddDays(-2), "Middle123");
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "Newest123");

            var result = await Validator(db, count: 2).ValidateAsync(ManagerWithHasher(), user, "Oldest123");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task WithinHistoryWindow_IsRejected()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            Seed(db, user, DateTime.UtcNow.AddDays(-3), "Oldest123");
            Seed(db, user, DateTime.UtcNow.AddDays(-2), "Middle123");
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "Newest123");

            // N=2 → Middle123 仍在視窗內，應被拒
            var result = await Validator(db, count: 2).ValidateAsync(ManagerWithHasher(), user, "Middle123");

            Assert.False(result.Succeeded);
        }

        [Fact]
        public async Task Disabled_WhenCountZero_AllowsReuse()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "OldPass123");

            var result = await Validator(db, count: 0).ValidateAsync(ManagerWithHasher(), user, "OldPass123");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task NoHistory_IsAccepted()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "brand-new", UserName = "new" };

            var result = await Validator(db).ValidateAsync(ManagerWithHasher(), user, "AnyPass123");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task EmptyPassword_IsAccepted()
        {
            using var db = NewDb();
            var user = new ApplicationUser { Id = "u1", UserName = "u1" };
            Seed(db, user, DateTime.UtcNow.AddDays(-1), "OldPass123");

            var result = await Validator(db).ValidateAsync(ManagerWithHasher(), user, "");

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task DifferentUserHistory_DoesNotCrossMatch()
        {
            using var db = NewDb();
            var u1 = new ApplicationUser { Id = "u1", UserName = "u1" };
            var u2 = new ApplicationUser { Id = "u2", UserName = "u2" };
            Seed(db, u1, DateTime.UtcNow.AddDays(-1), "U1Secret123");

            // u2 用 u1 的密碼字串 → 不應命中（歷史依 UserId 隔離）
            var result = await Validator(db).ValidateAsync(ManagerWithHasher(), u2, "U1Secret123");

            Assert.True(result.Succeeded);
        }
    }
}
