using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class RoleControllerTests : TestBase
    {
        private RoleController _controller;
        private RoleManager<ApplicationRole> _roleManager;

        public RoleControllerTests()
        {
            _roleManager = GetRoleManager();
            _controller = new RoleController(_roleManager, _context, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Index_ReturnsView_WithRoles()
        {
            // Arrange
            var r1 = new ApplicationRole { Id = "R1", Name = "Admin", NormalizedName = "ADMIN" };
            var r2 = new ApplicationRole { Id = "R2", Name = "User", NormalizedName = "USER" };
            await _roleManager.CreateAsync(r1);
            await _roleManager.CreateAsync(r2);

            // Act
            var result = await _controller.Index(null, 1, 10, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<PaginatedList<ApplicationRole>>(viewResult.Model);
            Assert.Equal(5, model.Count); // 3 seeded + 2 created
        }

        [Fact]
        public async Task Create_Post_ValidModel_RedirectsToIndex()
        {
            // Arrange
            var role = new ApplicationRole { Name = "NewRole" };

            // Act
            var result = await _controller.Create(role, null, null);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(RoleController.Index), redirectResult.ActionName);
            
            var savedRole = await _roleManager.FindByNameAsync("NewRole");
            Assert.NotNull(savedRole);
        }

        [Fact]
        public async Task Edit_Post_ValidModel_UpdatesAndRedirects()
        {
            // Arrange
            var role = new ApplicationRole { Name = "OldName" };
            await _roleManager.CreateAsync(role);

            // Detach to avoid tracking issues when updating
            _context.Entry(role).State = EntityState.Detached;

            var updatedRole = new ApplicationRole { Id = role.Id, Name = "NewName", OrganizationUnitId = "Org1" };

            // Act
            var result = await _controller.Edit(role.Id, updatedRole, null, null);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(RoleController.Index), redirectResult.ActionName);
            
            var dbRole = await _roleManager.FindByIdAsync(role.Id);
            Assert.Equal("NewName", dbRole.Name);
        }

        [Fact]
        public async Task Delete_Post_Confirmed_DeletesRoleAndRelatedData()
        {
            // Arrange
            var role = new ApplicationRole { Name = "ToDelete" };
            await _roleManager.CreateAsync(role);
            var roleId = role.Id;
            
            // Setup related data
            var user = new ApplicationUser { Id = "U1", ActiveRoleId = roleId };
            var permission = new MenuPermission { Id = "P1", RoleId = roleId, MenuKey = "M1" };
            _context.Users.Add(user);
            _context.MenuPermissions.Add(permission);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // Act
            var result = await _controller.DeleteConfirmed(roleId);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(RoleController.Index), redirectResult.ActionName);

            // Verify Deletion
            var deletedRole = await _roleManager.FindByIdAsync(roleId);
            Assert.Null(deletedRole);

            // Verify InMemory Data Updates
            var updatedUser = await _context.Users.FindAsync("U1");
            Assert.Null(updatedUser.ActiveRoleId); // Should be cleared

            var deletedPermission = await _context.MenuPermissions.FindAsync("P1");
            Assert.Null(deletedPermission); // Should be deleted
        }
    }
}
