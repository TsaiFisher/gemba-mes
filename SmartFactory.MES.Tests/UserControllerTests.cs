using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class UserControllerTests : TestBase
    {
        private UserController _controller;
        private Mock<UserManager<ApplicationUser>> _mockUserManager;
        private Mock<RoleManager<ApplicationRole>> _mockRoleManager;

        public UserControllerTests()
        {
            _mockUserManager = GetMockUserManager();
            _mockRoleManager = GetMockRoleManager<ApplicationRole>();
            
            _controller = new UserController(_mockUserManager.Object, _mockRoleManager.Object, _context, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Create_Post_ValidModel_CreatesUserAndRedirects()
        {
            // Arrange
            var user = new ApplicationUser { UserName = "newuser", Email = "new@test.com" };
            var password = "Password123!";
            var selectedRoles = new[] { "Admin" };

            _mockUserManager.Setup(m => m.CreateAsync(user, password)).ReturnsAsync(IdentityResult.Success);
            _mockUserManager.Setup(m => m.AddToRolesAsync(user, selectedRoles)).ReturnsAsync(IdentityResult.Success);

            // Act
            var result = await _controller.Create(user, password, selectedRoles);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(UserController.Index), redirectResult.ActionName);
            
            _mockUserManager.Verify(m => m.CreateAsync(user, password), Times.Once);
            _mockUserManager.Verify(m => m.AddToRolesAsync(user, selectedRoles), Times.Once);
        }

        /// <summary>
        /// 作用中角色被移除時，ActiveRoleId 改指向剩下的第一個角色。
        /// 2026-10-07 實測：dev 被改成只有 Operator，ActiveRoleId 卻停在 Admin，權限全部照 Admin 給。
        /// </summary>
        [Theory]
        [InlineData(new[] { "Operator" }, "3")]
        [InlineData(new string[0],        null)]
        public async Task Edit_Post_移除作用中角色時ActiveRoleId跟著改(string[] selectedRoles, string? expected)
        {
            var user = new ApplicationUser { Id = "U1", UserName = "dev", ActiveRoleId = "1" };
            SetupEdit(user, currentRoles: new List<string> { "Admin" });
            _mockRoleManager.Setup(m => m.FindByIdAsync("1")).ReturnsAsync(new ApplicationRole { Id = "1", Name = "Admin" });
            _mockRoleManager.Setup(m => m.FindByNameAsync("Operator")).ReturnsAsync(new ApplicationRole { Id = "3", Name = "Operator" });

            await _controller.Edit("U1", new ApplicationUser { Id = "U1", UserName = "dev" }, selectedRoles);

            Assert.Equal(expected, user.ActiveRoleId);
            _mockUserManager.Verify(m => m.UpdateAsync(user), Times.Exactly(2));   // 基本資料一次＋對齊角色一次
        }

        [Fact]
        public async Task Edit_Post_作用中角色仍保留時不動()
        {
            var user = new ApplicationUser { Id = "U1", UserName = "dev", ActiveRoleId = "1" };
            SetupEdit(user, currentRoles: new List<string> { "Admin" });
            _mockRoleManager.Setup(m => m.FindByIdAsync("1")).ReturnsAsync(new ApplicationRole { Id = "1", Name = "Admin" });

            await _controller.Edit("U1", new ApplicationUser { Id = "U1", UserName = "dev" }, new[] { "Admin", "Operator" });

            Assert.Equal("1", user.ActiveRoleId);
            _mockUserManager.Verify(m => m.UpdateAsync(user), Times.Once);
        }

        private void SetupEdit(ApplicationUser user, List<string> currentRoles)
        {
            _mockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
            _mockUserManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(currentRoles);
            _mockUserManager.Setup(m => m.AddToRolesAsync(user, It.IsAny<IEnumerable<string>>())).ReturnsAsync(IdentityResult.Success);
            _mockUserManager.Setup(m => m.RemoveFromRolesAsync(user, It.IsAny<IEnumerable<string>>())).ReturnsAsync(IdentityResult.Success);
        }

        [Fact]
        public async Task Edit_Post_ValidModel_UpdatesUserAndRoles()
        {
            // Arrange
            var userId = "U1";
            var existingUser = new ApplicationUser { Id = userId, UserName = "oldu", Email = "old@test.com" };
            var updatedUser = new ApplicationUser { Id = userId, UserName = "newu", Email = "new@test.com" };
            var selectedRoles = new[] { "Operator" };
            var currentRoles = new List<string> { "Admin" };

            _mockUserManager.Setup(m => m.FindByIdAsync(userId)).ReturnsAsync(existingUser);
            _mockUserManager.Setup(m => m.UpdateAsync(existingUser)).ReturnsAsync(IdentityResult.Success);
            _mockUserManager.Setup(m => m.GetRolesAsync(existingUser)).ReturnsAsync(currentRoles);
            
            _mockUserManager.Setup(m => m.AddToRolesAsync(existingUser, It.IsAny<IEnumerable<string>>()))
                .ReturnsAsync(IdentityResult.Success);
            _mockUserManager.Setup(m => m.RemoveFromRolesAsync(existingUser, It.IsAny<IEnumerable<string>>()))
                .ReturnsAsync(IdentityResult.Success);

            // Act
            var result = await _controller.Edit(userId, updatedUser, selectedRoles);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(UserController.Index), redirectResult.ActionName);

            Assert.Equal("newu", existingUser.UserName);
            Assert.Equal("new@test.com", existingUser.Email);
            
            _mockUserManager.Verify(m => m.AddToRolesAsync(existingUser, It.Is<IEnumerable<string>>(r => r.Contains("Operator"))), Times.Once);
            _mockUserManager.Verify(m => m.RemoveFromRolesAsync(existingUser, It.Is<IEnumerable<string>>(r => r.Contains("Admin"))), Times.Once);
        }

        [Fact]
        public async Task Delete_Post_Confirmed_DeletesUser()
        {
            // Arrange
            var userId = "U1";
            var user = new ApplicationUser { Id = userId };
            _mockUserManager.Setup(m => m.FindByIdAsync(userId)).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.DeleteAsync(user)).ReturnsAsync(IdentityResult.Success);

            // Act
            var result = await _controller.DeleteConfirmed(userId);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(UserController.Index), redirectResult.ActionName);
            _mockUserManager.Verify(m => m.DeleteAsync(user), Times.Once);
        }
    }
}
