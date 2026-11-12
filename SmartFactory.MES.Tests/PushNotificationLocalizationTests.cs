using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 系統推播在背景服務或「別人的」請求中送出，當下執行緒的語系不是收件人的。
    /// 內容必須在每位收件人自己的語系（ApplicationUser.Language）下產生，且不得改動呼叫端的語系。
    /// </summary>
    public class PushNotificationLocalizationTests
    {
        private static (ApplicationDbContext Db, PushNotificationService Svc) Build()
        {
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"push-l10n-{Guid.NewGuid()}").Options);
            db.Users.AddRange(
                new ApplicationUser { Id = "U-en", UserName = "en", Language = "en-US" },
                new ApplicationUser { Id = "U-zh", UserName = "zh", Language = "zh-TW" },
                new ApplicationUser { Id = "U-null", UserName = "none", Language = null });
            foreach (var id in new[] { "U-en", "U-zh", "U-null" })
                db.PushSubscriptions.Add(new PushSubscriptionRecord { UserId = id, Endpoint = $"https://push.invalid/{id}", P256dhKey = "k", AuthKey = "a" });
            db.UserRoles.AddRange(
                new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "U-en", RoleId = "R" },
                new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "U-zh", RoleId = "R" },
                new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "U-null", RoleId = "R" });
            db.SaveChanges();
            // 未設定 VAPID 金鑰：實際送出會失敗並被記錄，不影響這裡要驗的「內容在哪個語系下產生」
            return (db, new PushNotificationService(db, new ConfigurationBuilder().Build(), NullLogger<PushNotificationService>.Instance));
        }

        [Fact]
        public async Task SendToRole_ComposesOncePerRecipientLanguage_AndRestoresCallerCulture()
        {
            var (_, svc) = Build();
            var cultures = new List<string>();
            var caller = CultureInfo.CurrentUICulture;

            await svc.SendToRoleAsync("R", () =>
            {
                cultures.Add(CultureInfo.CurrentUICulture.Name);
                return new PushMessage("t", "b");
            });

            // en 一組、zh-TW 一組（未設定語系者比照預設 zh-TW 併入）
            Assert.Equal(new[] { "en-US", "zh-TW" }, cultures.OrderBy(c => c));
            Assert.Equal(caller, CultureInfo.CurrentUICulture);
        }

        [Theory]
        [InlineData("U-en", "en-US")]
        [InlineData("U-zh", "zh-TW")]
        [InlineData("U-null", "zh-TW")]
        public async Task SendToUser_ComposesInRecipientLanguage(string userId, string expected)
        {
            var (_, svc) = Build();
            string? seen = null;
            await svc.SendToUserAsync(userId, () => { seen = CultureInfo.CurrentUICulture.Name; return new PushMessage("t", "b"); });
            Assert.Equal(expected, seen);
        }
    }
}
