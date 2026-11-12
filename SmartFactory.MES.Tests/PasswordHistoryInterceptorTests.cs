using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// L4：PasswordHistory 攔截器 — 只在密碼雜湊「值」實際變動時記錄。
    /// 迴歸背景：Identity UserStore.UpdateAsync 走 Context.Update()，所有屬性一律標記
    /// IsModified；若攔截器只看 IsModified，任何個人資料編輯（語系、廠別…）都會誤寫
    /// 一列重複雜湊，把驗證器「最近 N 筆」視窗擠滿同一雜湊、削弱重用檢查。
    /// </summary>
    public class PasswordHistoryInterceptorTests
    {
        private static ApplicationDbContext NewDb()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"pwdhist-icpt-{Guid.NewGuid()}")
                .AddInterceptors(new PasswordHistoryInterceptor())
                .Options;
            return new ApplicationDbContext(options);
        }

        private static ApplicationUser NewUser(string? hash = "HASH-0") => new()
        {
            UserName = "icpt-user",
            Email = "icpt@test.local",
            PasswordHash = hash,
        };

        private static int HistoryCount(ApplicationDbContext db, string userId)
            => db.Set<PasswordHistory>().Count(h => h.UserId == userId);

        [Fact]
        public async Task AddedUser_WithInitialHash_RecordsOneRow()
        {
            using var db = NewDb();
            var user = NewUser();
            db.Users.Add(user);
            await db.SaveChangesAsync();

            Assert.Equal(1, HistoryCount(db, user.Id));
        }

        [Fact]
        public async Task AddedUser_WithoutHash_RecordsNothing()
        {
            using var db = NewDb();
            var user = NewUser(hash: null);
            db.Users.Add(user);
            await db.SaveChangesAsync();

            Assert.Equal(0, HistoryCount(db, user.Id));
        }

        [Fact]
        public async Task ProfileEdit_MarksAllModified_HashValueUnchanged_RecordsNothing()
        {
            using var db = NewDb();
            var user = NewUser();
            db.Users.Add(user);
            await db.SaveChangesAsync();

            // 比照 Identity UserStore.UpdateAsync：Context.Update() 全屬性標記已修改，
            // 但 PasswordHash 值未變 → 不得再記一列。
            user.Language = "en-US";
            db.Update(user);
            await db.SaveChangesAsync();

            Assert.Equal(1, HistoryCount(db, user.Id));
        }

        [Fact]
        public async Task PasswordChange_HashValueChanged_RecordsNewRow()
        {
            using var db = NewDb();
            var user = NewUser();
            db.Users.Add(user);
            await db.SaveChangesAsync();

            user.PasswordHash = "HASH-1";
            db.Update(user);
            await db.SaveChangesAsync();

            Assert.Equal(2, HistoryCount(db, user.Id));
            Assert.Contains(db.Set<PasswordHistory>().Where(h => h.UserId == user.Id),
                h => h.PasswordHash == "HASH-1");
        }
    }
}
