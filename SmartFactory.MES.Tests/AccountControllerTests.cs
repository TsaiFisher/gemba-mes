using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using System.Security.Claims;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class AccountControllerTests : TestBase
    {
        private Mock<UserManager<ApplicationUser>> _mockUserManager;
        private Mock<SignInManager<ApplicationUser>> _mockSignInManager;
        private Mock<IRoleSwitchService> _roleSwitch;
        private AccountController _controller;

        public AccountControllerTests()
        {
            _mockUserManager = GetMockUserManager();
            _mockSignInManager = GetMockSignInManager(_mockUserManager.Object);
            _roleSwitch = new Mock<IRoleSwitchService>();
            _controller = new AccountController(_mockUserManager.Object, _mockSignInManager.Object, _roleSwitch.Object);
            
            // Setup default ControllerContext
            var httpContext = new DefaultHttpContext();
            _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        }

        [Fact]
        public async Task SwitchRole_UpdatesActiveRoleId_Redirects()
        {
            // Arrange
            var user = new ApplicationUser { Id = "User1", UserName = "testuser" };
            _mockUserManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);
            _mockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

            _roleSwitch.Setup(x => x.TrySwitchAsync(It.IsAny<ClaimsPrincipal>(), "1"))
                .ReturnsAsync(RoleSwitchOutcome.Switched);
            var result = await _controller.SwitchRole("1");

            // Assert
            _roleSwitch.Verify(x => x.TrySwitchAsync(It.IsAny<ClaimsPrincipal>(), "1"), Times.Once);
            var redirectResult = Assert.IsType<LocalRedirectResult>(result);
            Assert.Equal("/", redirectResult.Url);
        }

        /// <summary>
        /// 不屬於的角色不准切。這支原本只取角色清單、沒比對就寫入——任何登入者 POST roleId=1 就成了 Admin。
        /// </summary>
        [Theory]
        [InlineData("1")]          // 存在但不屬於
        [InlineData("no-such")]    // 不存在
        public async Task SwitchRole_不屬於的角色_Forbid且不寫入(string roleId)
        {
            var user = new ApplicationUser { Id = "User1", UserName = "dev", ActiveRoleId = "3" };
            _mockUserManager.Setup(m => m.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(false);

            _roleSwitch.Setup(x => x.TrySwitchAsync(It.IsAny<ClaimsPrincipal>(), roleId))
                .ReturnsAsync(RoleSwitchOutcome.NotAllowed);
            var result = await _controller.SwitchRole(roleId);

            Assert.IsType<ForbidResult>(result);
            Assert.Equal("3", user.ActiveRoleId);
            _mockUserManager.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
        }
    }
}
