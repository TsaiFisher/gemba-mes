using SmartFactory.MES.Models;
using SmartFactory.MES.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Moq;
using SmartFactory.MES.Controllers;
using System.Reflection;

namespace SmartFactory.MES.Tests
{
    /// <summary>
    /// 語言切換必須在「未登入」狀態下也能用——登入頁導覽列就有語言下拉，
    /// 若 SetLanguage 受 [Authorize] 保護，匿名者點了只會被導回登入頁、
    /// Cookie 永遠寫不進去（症狀＝「登入畫面沒有中英版」）。
    /// </summary>
    public class UserSettingsLanguageTests
    {
        private static MethodInfo SetLanguage =>
            typeof(UserSettingsController).GetMethod(nameof(UserSettingsController.SetLanguage))!;

        [Fact]
        public void SetLanguage_IsAllowAnonymous()
        {
            Assert.NotNull(SetLanguage.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        [Fact]
        public void Controller_ItselfStaysAuthorized()
        {
            // 只放行語言切換，其餘動作仍須登入
            Assert.NotNull(typeof(UserSettingsController)
                .GetCustomAttribute<AuthorizeAttribute>());
        }

        private static UserSettingsController BuildController(ApplicationDbContext? db = null, string? userId = null)
        {
            // 語言切換不碰廠別也不碰時區，後兩個相依（IPlantSwitchService／IUserContext）
            // 與這幾支測試無關，傳 null 即可。匿名情境連 DbContext 也傳 null——碰到就會丟例外，
            // 正好證明匿名切換不寫資料庫。
            var http = new DefaultHttpContext();
            if (userId != null)
                http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId) }, "Test"));
            var controller = new UserSettingsController(null!, null!, db!, null!, null!, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
            var url = new Mock<IUrlHelper>();
            url.Setup(u => u.IsLocalUrl(It.IsAny<string>()))
               .Returns((string? s) => !string.IsNullOrEmpty(s) && s.StartsWith('/') && !s.StartsWith("//"));
            controller.Url = url.Object;
            return controller;
        }

        [Theory]
        [InlineData("zh-TW")]
        [InlineData("en-US")]
        public void SetLanguage_WritesCultureCookie_AndRedirectsBack(string culture)
        {
            var controller = BuildController();

            var result = controller.SetLanguage(culture, "/Identity/Account/Login?ReturnUrl=%2FHome");

            var redirect = Assert.IsType<LocalRedirectResult>(result);
            Assert.Equal("/Identity/Account/Login?ReturnUrl=%2FHome", redirect.Url);

            var setCookie = controller.Response.Headers["Set-Cookie"].ToString();
            Assert.Contains(
                Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName,
                setCookie);
            Assert.Contains(culture, Uri.UnescapeDataString(setCookie));
        }

        [Fact]
        public void SetLanguage_RejectsUnsupportedCulture()
        {
            Assert.IsType<BadRequestObjectResult>(BuildController().SetLanguage("ja-JP", "/"));
        }

        private static ApplicationDbContext NewDb(string? language)
        {
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"setlang-{Guid.NewGuid()}").Options);
            db.Users.Add(new ApplicationUser { Id = "U1", UserName = "u1", Language = language });
            db.SaveChanges();
            return db;
        }

        /// <summary>
        /// GET 不得改狀態（部署專案 R26）：就算已登入，GET 也只寫 cookie。
        /// 這裡傳入真的 DbContext 與已登入身分，證明 GET 沒有寫回帳號。
        /// </summary>
        [Fact]
        public void SetLanguage_Get_Authenticated_DoesNotTouchAccount()
        {
            using var db = NewDb("zh-TW");
            BuildController(db, "U1").SetLanguage("en-US", "/");
            Assert.Equal("zh-TW", db.Users.AsNoTracking().Single(u => u.Id == "U1").Language);
        }

        /// <summary>
        /// 推播、手機登入預設語系讀的是 ApplicationUser.Language；只寫 cookie 的話，
        /// 使用者自己切了英文，推播仍是管理員當初設的中文。已登入者的切換走 POST 寫回。
        /// </summary>
        [Fact]
        public async Task SetLanguage_Post_PersistsPreferenceToAccount_AndWritesCookie()
        {
            using var db = NewDb("zh-TW");
            var controller = BuildController(db, "U1");
            Assert.IsType<LocalRedirectResult>(await controller.SetLanguagePost("en-US", "/Home"));
            Assert.Equal("en-US", db.Users.AsNoTracking().Single(u => u.Id == "U1").Language);
            Assert.Contains("en-US", Uri.UnescapeDataString(controller.Response.Headers["Set-Cookie"].ToString()));
        }

        [Fact]
        public async Task SetLanguage_Post_UnsupportedCulture_DoesNotTouchAccount()
        {
            using var db = NewDb("zh-TW");
            Assert.IsType<BadRequestObjectResult>(await BuildController(db, "U1").SetLanguagePost("ja-JP", "/"));
            Assert.Equal("zh-TW", db.Users.AsNoTracking().Single(u => u.Id == "U1").Language);
        }

        [Fact]
        public void SetLanguage_Post_RequiresAntiForgery_AndSharesActionName()
        {
            var post = typeof(UserSettingsController).GetMethod(nameof(UserSettingsController.SetLanguagePost))!;
            Assert.NotNull(post.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.Null(post.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Equal(nameof(UserSettingsController.SetLanguage), post.GetCustomAttribute<ActionNameAttribute>()?.Name);
        }

        [Fact]
        public void MobileSetLanguage_IsAuthenticatedJsonPost()
        {
            var m = typeof(SmartFactory.MES.Api.Mobile.MobileAuthController).GetMethod("SetLanguage")!;
            Assert.Equal("language", m.GetCustomAttribute<HttpPostAttribute>()?.Template);
            Assert.NotNull(m.GetCustomAttribute<AuthorizeAttribute>());
        }

        [Theory]
        [InlineData("https://evil.example.com/")]
        [InlineData("//evil.example.com/")]
        [InlineData(null)]
        public void SetLanguage_NonLocalReturnUrl_FallsBackToRoot(string? returnUrl)
        {
            // 匿名可達 → returnUrl 不可信；不得變成開放轉址，也不得丟例外變 500
            var result = BuildController().SetLanguage("zh-TW", returnUrl!);

            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.Equal("/", redirect.Url);
        }
    }
}
