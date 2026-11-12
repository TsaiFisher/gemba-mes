using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using IdentitySignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFactory.MES.Areas.Identity.Pages.Account;
using SmartFactory.MES.Models;
using System.Security.Claims;

namespace SmartFactory.MES.Tests
{
    public class LoginAuditTests : TestBase
    {
        private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;
        private readonly Mock<SignInManager<ApplicationUser>> _mockSignInManager;

        public LoginAuditTests()
        {
            _mockUserManager = GetMockUserManager();
            _mockSignInManager = GetMockSignInManager(_mockUserManager.Object);

            // PasswordSignInAsync is virtual — default to Failed unless overridden per test
            _mockSignInManager
                .Setup(s => s.GetExternalAuthenticationSchemesAsync())
                .ReturnsAsync(new List<AuthenticationScheme>());
        }

        private LoginModel BuildLoginModel(string inputEmail = "testuser", string inputPassword = "Password123!")
        {
            var httpContext = new DefaultHttpContext();
            var model = new LoginModel(_mockSignInManager.Object, NullLogger<LoginModel>.Instance, _context)
            {
                PageContext = new PageContext(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor())),
                Input = new LoginModel.InputModel { Email = inputEmail, Password = inputPassword, RememberMe = false }
            };
            var urlHelper = new Mock<IUrlHelper>();
            urlHelper.Setup(u => u.Content(It.IsAny<string>())).Returns("/");
            model.Url = urlHelper.Object;
            return model;
        }

        // ── LoginModel ──────────────────────────────────────────────────────────

        [Fact]
        public async Task OnPostAsync_SuccessfulLogin_WritesLoginAuditLog()
        {
            // Arrange
            var user = new ApplicationUser { Id = "uid-1", UserName = "testuser" };
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.Success);
            _mockUserManager.Setup(m => m.FindByNameAsync("testuser")).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.FindByEmailAsync("testuser")).ReturnsAsync((ApplicationUser?)null);

            // Act
            await BuildLoginModel().OnPostAsync();

            // Assert
            var log = _context.AuditLogs.SingleOrDefault(l => l.Action == "Login");
            Assert.NotNull(log);
            Assert.Equal("Authentication", log.EntityType);
            Assert.Equal("uid-1", log.UserId);
            Assert.Equal("testuser", log.UserName);
            Assert.Null(log.Changes); // success has no failure reason
        }

        [Fact]
        public async Task OnPostAsync_InvalidCredentials_WritesLoginFailedAuditLog()
        {
            // Arrange
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.Failed);

            // Act
            await BuildLoginModel().OnPostAsync();

            // Assert
            var log = _context.AuditLogs.SingleOrDefault(l => l.Action == "LoginFailed");
            Assert.NotNull(log);
            Assert.Equal("Authentication", log.EntityType);
            Assert.Equal("testuser", log.UserName);
            Assert.Null(log.UserId); // unknown user on failure
            Assert.NotNull(log.Changes); // reason is recorded
        }

        // 桌機登入失敗必須累計帳號鎖定：每 IP 限流已為共用 NAT 放寬，鎖定是對單一帳號暴力猜測的主防線。
        [Fact]
        public async Task OnPostAsync_CountsFailuresTowardAccountLockout()
        {
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .ReturnsAsync(IdentitySignInResult.Failed);

            await BuildLoginModel().OnPostAsync();

            _mockSignInManager.Verify(s => s.PasswordSignInAsync("testuser", "Password123!", false, true), Times.Once);
            _mockSignInManager.Verify(s => s.PasswordSignInAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), false), Times.Never);
        }

        [Fact]
        public async Task OnPostAsync_LockedOut_WritesLoginFailedAuditLog_WithLockoutReason()
        {
            // Arrange
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.LockedOut);

            // Act
            await BuildLoginModel().OnPostAsync();

            // Assert
            var log = _context.AuditLogs.SingleOrDefault(l => l.Action == "LoginFailed");
            Assert.NotNull(log);
            Assert.Contains("鎖定", log.Changes ?? "");
        }

        [Fact]
        public async Task OnPostAsync_SuccessfulLogin_DoesNotWriteLoginFailed()
        {
            // Arrange
            var user = new ApplicationUser { Id = "uid-2", UserName = "testuser" };
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.Success);
            _mockUserManager.Setup(m => m.FindByNameAsync("testuser")).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.FindByEmailAsync("testuser")).ReturnsAsync((ApplicationUser?)null);

            // Act
            await BuildLoginModel().OnPostAsync();

            // Assert
            Assert.DoesNotContain(_context.AuditLogs, l => l.Action == "LoginFailed");
        }

        [Fact]
        public async Task OnPostAsync_InvalidCredentials_DoesNotWriteLogin()
        {
            // Arrange
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.Failed);

            // Act
            await BuildLoginModel().OnPostAsync();

            // Assert
            Assert.DoesNotContain(_context.AuditLogs, l => l.Action == "Login");
        }

        // ── 登入失敗訊息在地化 ───────────────────────────────────────────────
        // 原本硬寫英文 "Invalid login attempt."，zh-TW 使用者也只看得到英文。

        [Theory]
        [InlineData("zh-TW", "帳號或密碼錯誤，請重新輸入。")]
        [InlineData("en-US", "Invalid login attempt.")]
        public async Task OnPostAsync_InvalidCredentials_ErrorMessageFollowsUiCulture(
            string culture, string expected)
        {
            _mockSignInManager
                .Setup(s => s.PasswordSignInAsync("testuser", "Password123!", false, true))
                .ReturnsAsync(IdentitySignInResult.Failed);

            var original = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture =
                    new System.Globalization.CultureInfo(culture);

                var model = BuildLoginModel();
                await model.OnPostAsync();

                var messages = model.ModelState[string.Empty]!.Errors
                    .Select(e => e.ErrorMessage).ToList();
                Assert.Contains(expected, messages);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = original;
            }
        }
    }

    public class LogoutAuditTests : TestBase
    {
        [Fact]
        public async Task OnPost_WritesLogoutAuditLog()
        {
            // Arrange
            const string userId = "uid-out";
            const string userName = "logoutUser";

            var mockUserManager = GetMockUserManager();
            mockUserManager
                .Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>()))
                .Returns(userId);

            var mockSignInManager = GetMockSignInManager(mockUserManager.Object);
            mockSignInManager.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

            var httpContext = new DefaultHttpContext();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userName)
            };
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

            var model = new LogoutModel(mockSignInManager.Object, _context)
            {
                PageContext = new PageContext(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()))
            };
            var urlHelper = new Mock<IUrlHelper>();
            urlHelper.Setup(u => u.Content(It.IsAny<string>())).Returns("/");
            model.Url = urlHelper.Object;

            // Act
            await model.OnPost();

            // Assert
            var log = _context.AuditLogs.SingleOrDefault(l => l.Action == "Logout");
            Assert.NotNull(log);
            Assert.Equal("Authentication", log.EntityType);
            Assert.Equal(userId, log.UserId);
            Assert.Equal(userName, log.UserName);
        }

        [Fact]
        public async Task OnPost_SignsOutBeforeWritingLog()
        {
            // Arrange — verifies SignOutAsync is called
            var mockUserManager = GetMockUserManager();
            mockUserManager
                .Setup(m => m.GetUserId(It.IsAny<ClaimsPrincipal>()))
                .Returns("uid-x");

            var mockSignInManager = GetMockSignInManager(mockUserManager.Object);
            mockSignInManager.Setup(s => s.SignOutAsync()).Returns(Task.CompletedTask);

            var httpContext = new DefaultHttpContext();
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "anyuser")
            }, "TestAuth"));

            var model = new LogoutModel(mockSignInManager.Object, _context)
            {
                PageContext = new PageContext(new ActionContext(httpContext, new RouteData(), new PageActionDescriptor()))
            };
            var urlHelper = new Mock<IUrlHelper>();
            urlHelper.Setup(u => u.Content(It.IsAny<string>())).Returns("/");
            model.Url = urlHelper.Object;

            // Act
            await model.OnPost();

            // Assert
            mockSignInManager.Verify(s => s.SignOutAsync(), Times.Once);
        }
    }
}
