using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 廠別「預設語言」（Plant.DefaultLanguage）：個人語系優先，個人沒設定時用所在廠別的預設語系，再沒有才 zh-TW。
    /// 2026-10-27 前這個欄位只被存起來顯示，沒有任何地方讀它。
    /// </summary>
    public class PlantDefaultLanguageTests
    {
        [Theory]
        [InlineData("en-US", "zh-TW", "en-US")]   // 個人優先
        [InlineData(null,    "en-US", "en-US")]   // 個人沒設定 → 廠別
        [InlineData("",      "en-US", "en-US")]
        [InlineData("zh_TW", "en-US", "en-US")]   // 舊資料自由輸入的無效值 → 視同未設定
        [InlineData(null,    null,    "zh-TW")]   // 都沒有 → zh-TW
        [InlineData(null,    "ja-JP", "zh-TW")]   // 廠別值不支援 → zh-TW
        public void Resolve_PersonalThenPlantThenFallback(string? personal, string? plant, string expected)
            => Assert.Equal(expected, LanguagePreference.Resolve(personal, plant));

        private static ApplicationDbContext NewDb()
        {
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"plantlang-{Guid.NewGuid()}").Options);
            db.Plants.AddRange(
                new Plant { Id = "TW", Name = "台灣廠", DefaultLanguage = "zh-TW" },
                new Plant { Id = "US", Name = "US Plant", DefaultLanguage = "en-US" });
            db.SaveChanges();
            return db;
        }

        [Fact]
        public async Task ResolveForUser_UsesActivePlant_OrGivenPlant()
        {
            using var db = NewDb();
            var noPersonal = new ApplicationUser { Id = "u1", ActivePlantId = "US" };
            var personal   = new ApplicationUser { Id = "u2", ActivePlantId = "US", Language = "zh-TW" };

            Assert.Equal("en-US", await LanguagePreference.ResolveForUserAsync(db, noPersonal));
            Assert.Equal("zh-TW", await LanguagePreference.ResolveForUserAsync(db, noPersonal, "TW"));   // 切換目標廠別
            Assert.Equal("zh-TW", await LanguagePreference.ResolveForUserAsync(db, personal));
            Assert.Equal("zh-TW", await LanguagePreference.ResolveForUserAsync(db, null));
        }

        private static (UserSettingsController Ctrl, HttpContext Http) BuildSwitch(ApplicationDbContext db, ApplicationUser user)
        {
            var store = new Mock<IUserStore<ApplicationUser>>();
            var um = new Mock<UserManager<ApplicationUser>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
            um.Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>())).Returns(user.Id);
            um.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
            var sw = new Mock<IPlantSwitchService>();
            sw.Setup(s => s.TrySwitchAsync(user.Id, It.IsAny<string?>())).ReturnsAsync(PlantSwitchOutcome.Switched);

            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) }, "Test"))
            };
            var ctrl = new UserSettingsController(um.Object, null!, db, sw.Object, null!, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
            return (ctrl, http);
        }

        private static string SetCookie(HttpContext http) => Uri.UnescapeDataString(http.Response.Headers["Set-Cookie"].ToString());

        [Fact]
        public async Task SwitchPlant_NoPersonalLanguage_FollowsTargetPlant()
        {
            using var db = NewDb();
            var (ctrl, http) = BuildSwitch(db, new ApplicationUser { Id = "u1", ActivePlantId = "TW" });

            await ctrl.SwitchPlant("US");

            Assert.Contains("c=en-US", SetCookie(http));
        }

        [Fact]
        public async Task SwitchPlant_PersonalLanguage_IsKept()
        {
            using var db = NewDb();
            var (ctrl, http) = BuildSwitch(db, new ApplicationUser { Id = "u2", ActivePlantId = "TW", Language = "zh-TW" });

            await ctrl.SwitchPlant("US");

            Assert.DoesNotContain(".AspNetCore.Culture", SetCookie(http));   // 個人優先：不改語系
        }
    
        // ── 登入成功時寫語系 cookie（密碼／兩步驟驗證／復原碼共同終點）────────────

        private static async Task<string> SignedInCookieAsync(string path, string userId)
        {
            var db = NewDb();
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, ActivePlantId = "US" });
            db.SaveChanges();
            var http = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddSingleton(db).BuildServiceProvider()
            };
            http.Request.Path = path;
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
            var ctx = new CookieSignedInContext(http,
                new AuthenticationScheme("Identity.Application", null, typeof(CookieAuthenticationHandler)),
                principal, new AuthenticationProperties(), new CookieAuthenticationOptions());

            await SignInLanguage.OnSignedInAsync(ctx);
            return SetCookie(http);
        }

        [Theory]
        [InlineData("/Identity/Account/Login")]
        [InlineData("/Identity/Account/LoginWith2fa")]           // Identity 內建頁面，原本完全沒寫語系
        [InlineData("/Identity/Account/LoginWithRecoveryCode")]
        public async Task OnSignedIn_LoginPages_WriteResolvedLanguage(string path)
            => Assert.Contains("c=en-US", await SignedInCookieAsync(path, "u1"));

        [Theory]
        [InlineData("/UserSettings/SwitchRole")]                 // RefreshSignInAsync 也會觸發，不可蓋掉使用者剛選的語言
        [InlineData("/TwoFactor/Enable")]
        [InlineData("/api/mobile/auth/login")]                   // 手機語系由登入回應的 language 決定
        public async Task OnSignedIn_OtherPaths_DoNotTouchLanguage(string path)
            => Assert.DoesNotContain(".AspNetCore.Culture", await SignedInCookieAsync(path, "u1"));
}
}
