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

        // ── 最後一位管理員保護（2026-11-16）──────────────────────────────────────────
        // 帳號／角色／權限設定只限作用中角色為 Admin；唯一的管理員被取消角色或刪除後沒有人進得去。

        [Fact]
        public async Task Edit_Post_唯一的管理員不能被取消Admin角色()
        {
            var user = new ApplicationUser { Id = "U1", UserName = "dev", ActiveRoleId = "1" };
            SetupEdit(user, currentRoles: new List<string> { "Admin" });
            _mockUserManager.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);
            _controller.ControllerContext.HttpContext.Request.Headers["X-Requested-With"] = "XMLHttpRequest";

            var result = await _controller.Edit("U1", new ApplicationUser { Id = "U1", UserName = "dev" }, new[] { "Operator" });

            var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
            Assert.Contains("\"success\":false", json);
            _mockUserManager.Verify(m => m.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
            _mockUserManager.Verify(m => m.RemoveFromRolesAsync(It.IsAny<ApplicationUser>(), It.IsAny<IEnumerable<string>>()), Times.Never);
        }

        [Fact]
        public async Task Edit_Post_還有其他管理員時可以取消Admin角色()
        {
            var user = new ApplicationUser { Id = "U1", UserName = "dev", ActiveRoleId = "1" };
            SetupEdit(user, currentRoles: new List<string> { "Admin" });
            _mockUserManager.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);
            _mockRoleManager.Setup(m => m.FindByIdAsync("1")).ReturnsAsync(new ApplicationRole { Id = "1", Name = "Admin" });
            _mockRoleManager.Setup(m => m.FindByNameAsync("Operator")).ReturnsAsync(new ApplicationRole { Id = "3", Name = "Operator" });
            await SeedAnotherAdminAsync();

            await _controller.Edit("U1", new ApplicationUser { Id = "U1", UserName = "dev" }, new[] { "Operator" });

            _mockUserManager.Verify(m => m.RemoveFromRolesAsync(user, It.IsAny<IEnumerable<string>>()), Times.Once);
        }

        [Theory]
        [InlineData(false, false)]   // 唯一的管理員：擋
        [InlineData(true,  true)]    // 還有別人：照刪
        public async Task Delete_唯一的管理員不能被刪除(bool anotherAdmin, bool expectDeleted)
        {
            var user = new ApplicationUser { Id = "U1", UserName = "dev" };
            _mockUserManager.Setup(m => m.FindByIdAsync("U1")).ReturnsAsync(user);
            _mockUserManager.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);
            _mockUserManager.Setup(m => m.DeleteAsync(user)).ReturnsAsync(IdentityResult.Success);
            if (anotherAdmin) await SeedAnotherAdminAsync();

            await _controller.DeleteConfirmed("U1");

            _mockUserManager.Verify(m => m.DeleteAsync(user), expectDeleted ? Times.Once : Times.Never);
        }

        private async Task SeedAnotherAdminAsync()
        {
            // TestBase 可能已種了 Id="1" 的角色；要的是「有一個名為 Admin 的角色、U2 屬於它」
            var role = await _context.Roles.FindAsync("1");
            if (role == null) _context.Roles.Add(new ApplicationRole { Id = "1", Name = "Admin", NormalizedName = "ADMIN" });
            else role.Name = "Admin";
            _context.UserRoles.Add(new IdentityUserRole<string> { UserId = "U2", RoleId = "1" });
            await _context.SaveChangesAsync();
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
