using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class OrganizationControllerTests : TestBase
    {
        private OrganizationController _controller;
        private Mock<IPermissionService> _mockPermissionService;

        public OrganizationControllerTests()
        {
            _mockPermissionService = new Mock<IPermissionService>();
            _controller = new OrganizationController(_context, _mockPermissionService.Object, GetMockExportService().Object, GetMockDateTimeService().Object);
            
            // Setup default ControllerContext
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Index_ReturnsView_WithOrganizationUnits()
        {
            // Arrange
            var unit1 = new OrganizationUnit { Id = "Dep1", Name = "Development", SortOrder = 1 };
            var unit2 = new OrganizationUnit { Id = "Dep2", Name = "HR", SortOrder = 2 };
            _context.OrganizationUnits.AddRange(unit1, unit2);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<List<OrganizationUnit>>(viewResult.Model);
            Assert.Equal(2, model.Count);
        }

        [Fact]
        public async Task Create_Post_ValidModel_RedirectsToIndex()
        {
            // Arrange
            var unit = new OrganizationUnit { Id = "NewDep", Name = "New Department" };

            // Act
            var result = await _controller.Create(unit);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirectResult.ActionName);
            
            var savedUnit = await _context.OrganizationUnits.FindAsync("NewDep");
            Assert.NotNull(savedUnit);
            Assert.Equal("New Department", savedUnit.Path); // logic sets Path = Name if ParentId is null
        }

        [Fact]
        public async Task Edit_Post_ValidModel_UpdatesAndRedirects()
        {
            // Arrange
            var unit = new OrganizationUnit { Id = "Dep1", Name = "Development", Path="Development" };
            _context.OrganizationUnits.Add(unit);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear(); // Clear tracking to avoid conflict

            unit.Name = "R&D";

            // Act
            var result = await _controller.Edit("Dep1", unit);

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirectResult.ActionName);

            var updatedUnit = await _context.OrganizationUnits.FindAsync("Dep1");
            Assert.Equal("R&D", updatedUnit.Name);
            Assert.Equal("R&D", updatedUnit.Path);
        }

        [Fact]
        public async Task Delete_Post_Confirmed_DeletesAndRedirects()
        {
            // Arrange
            var unit = new OrganizationUnit { Id = "Dep1", Name = "Development" };
            _context.OrganizationUnits.Add(unit);
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.DeleteConfirmed("Dep1");

            // Assert
            var redirectResult = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirectResult.ActionName);

            var deletedUnit = await _context.OrganizationUnits.FindAsync("Dep1");
            Assert.Null(deletedUnit);
        }
    }
}
