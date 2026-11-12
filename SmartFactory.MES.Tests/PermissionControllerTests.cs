using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class PermissionControllerTests : TestBase
    {
        private RoleManager<ApplicationRole> _roleManager;
        private Mock<IMenuService> _mockMenuService;
        private PermissionController _controller;

        public PermissionControllerTests()
        {
            _roleManager = GetRoleManager();
            _mockMenuService = new Mock<IMenuService>();
            _mockMenuService.Setup(m => m.GetFullMenu()).Returns(new List<MenuItem>());

            _controller = new PermissionController(_context, _roleManager, _mockMenuService.Object, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Index_ReturnsView_WithRoles()
        {
            // Arrange
            // Seeded roles (3) + we can add more if needed
            
            // Act
            var result = await _controller.Index(null, 1, 10, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<PaginatedList<ApplicationRole>>(viewResult.Model);
            Assert.NotEmpty(model);
            Assert.Contains(model, r => r.Name == "Admin");
        }

        [Fact]
        public async Task Edit_Get_ReturnsView_WithPermissions()
        {
            // Arrange
            var role = new ApplicationRole { Name = "TestRole" };
            await _roleManager.CreateAsync(role);
            var roleId = role.Id;

            var menus = new List<MenuItem> { new MenuItem { MenuKey = "M1", Title = "Menu1" } };
            _mockMenuService.Setup(m => m.GetFlattenedAccessibleMenus()).Returns(menus);

            var existingPermission = new MenuPermission { Id = "P1", RoleId = roleId, MenuKey = "M1", CanRead = true };
            _context.MenuPermissions.Add(existingPermission);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Edit(roleId);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<List<MenuPermission>>(viewResult.Model);
            Assert.Single(model);
            Assert.Equal("M1", model[0].MenuKey);
            Assert.Equal(roleId, (viewResult.ViewData["Role"] as ApplicationRole)?.Id);
            Assert.Equal(menus, viewResult.ViewData["Menus"]);
        }

        [Fact]
        public async Task Edit_Post_ValidModel_UpdatesPermissionsAndRedirects()
        {
            // Arrange
            var role = new ApplicationRole { Name = "TestRole2" };
            await _roleManager.CreateAsync(role);
            var roleId = role.Id;

            // Pre-existing permission to be removed
            var oldPermission = new MenuPermission { Id = "OldP", RoleId = roleId, MenuKey = "M1", CanRead = true };
            _context.MenuPermissions.Add(oldPermission);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            // New permissions
            var newPermissions = new List<MenuPermission>
            {
                new MenuPermission { MenuKey = "M1", CanRead = true, CanCreate = true }, // Should be added
                new MenuPermission { MenuKey = "M2", CanRead = false, CanCreate = false } // Should be ignored
            };

            // Act
            var result = await _controller.Edit(roleId, newPermissions);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(PermissionController.Index), redirectResult.ActionName);

            var savedPermissions = await _context.MenuPermissions.Where(p => p.RoleId == roleId).ToListAsync();
            Assert.Single(savedPermissions);
            Assert.Equal("M1", savedPermissions[0].MenuKey);
            Assert.True(savedPermissions[0].CanCreate);
        }
        [Fact]
        public async Task Edit_Get_ExcludesDefaultMenus()
        {
            // Arrange
            var role = new ApplicationRole { Name = "TestRole3" };
            await _roleManager.CreateAsync(role);
            var roleId = role.Id;

            var menus = new List<MenuItem> 
            { 
                new MenuItem { MenuKey = "Home", Title = "Home" },
                new MenuItem { MenuKey = "UserSettings", Title = "Settings" },
                new MenuItem { MenuKey = "RealModule", Title = "Module" }
            };
            _mockMenuService.Setup(m => m.GetFlattenedAccessibleMenus()).Returns(menus);

            // Act
            var result = await _controller.Edit(roleId);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var viewMenus = Assert.IsAssignableFrom<List<MenuItem>>(viewResult.ViewData["Menus"]);
            
            Assert.Single(viewMenus);
            Assert.Equal("RealModule", viewMenus[0].MenuKey);
        }
    }
}
