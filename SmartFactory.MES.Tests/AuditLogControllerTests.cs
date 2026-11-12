using Microsoft.AspNetCore.Mvc;
using SmartFactory.MES.Controllers;
using SmartFactory.MES.Models;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SmartFactory.MES.Tests
{
    public class AuditLogControllerTests : TestBase
    {
        private AuditLogController _controller;

        public AuditLogControllerTests()
        {
            _controller = new AuditLogController(_context, GetMockUserContext().Object, GetMockExportService().Object, GetMockDateTimeService().Object);
            _controller.ControllerContext = GetControllerContext();
        }

        [Fact]
        public async Task Index_ReturnsNullModel_WhenNotSearched()
        {
            // Act
            var result = await _controller.Index(null, null, null, null, null, 1, 10, null, searched: false);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Null(viewResult.Model);
        }

        [Fact]
        public async Task Index_ReturnsLogs_WhenSearched()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { Action = "Modified", EntityType = "User", Timestamp = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, null, null, null, null, 1, 10, null, searched: true);

            // Assert
            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(viewResult.Model);
            Assert.Single(model);
        }
        [Fact]
        public async Task Index_ReturnsSortedLogs_WhenSortOrderIsProvided()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { UserName = "UserB", Action = "A", Timestamp = DateTime.UtcNow.AddMinutes(-10) });
            _context.AuditLogs.Add(new AuditLog { UserName = "UserA", Action = "B", Timestamp = DateTime.UtcNow.AddMinutes(-5) });
            await _context.SaveChangesAsync();

            // Act - Sort by User Asc
            var resultAsc = await _controller.Index(null, null, null, null, null, 1, 10, "user_asc", searched: true);
            var modelAsc = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)resultAsc).Model);
            Assert.Equal("UserA", modelAsc[0].UserName);

            // Act - Sort by Timestamp Asc
            var resultTime = await _controller.Index(null, null, null, null, null, 1, 10, "timestamp_asc", searched: true);
            var modelTime = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)resultTime).Model);
            Assert.Equal("UserB", modelTime[0].UserName); // UserB has earlier timestamp
        }

        [Fact]
        public async Task Index_FilterByLogin_ReturnsOnlyLoginLogs()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { Action = "Login", EntityType = "Authentication", UserName = "alice", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "Authentication", UserName = "bob", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "Modified", EntityType = "User", UserName = "carol", Timestamp = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, null, "Login", null, null, 1, 10, null, searched: true);

            // Assert
            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)result).Model);
            Assert.Single(model);
            Assert.Equal("Login", model[0].Action);
            Assert.Equal("alice", model[0].UserName);
        }

        [Fact]
        public async Task Index_FilterByLoginFailed_ReturnsOnlyFailedLogs()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { Action = "Login", EntityType = "Authentication", UserName = "alice", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "Authentication", UserName = "hacker", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "LoginFailed", EntityType = "Authentication", UserName = "hacker", Timestamp = DateTime.UtcNow.AddSeconds(-5) });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, null, "LoginFailed", null, null, 1, 10, null, searched: true);

            // Assert
            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)result).Model);
            Assert.Equal(2, model.TotalItems);
            Assert.All(model, l => Assert.Equal("LoginFailed", l.Action));
        }

        [Fact]
        public async Task Index_FilterByLogout_ReturnsOnlyLogoutLogs()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { Action = "Logout", EntityType = "Authentication", UserName = "alice", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "Login", EntityType = "Authentication", UserName = "alice", Timestamp = DateTime.UtcNow.AddMinutes(-1) });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, null, "Logout", null, null, 1, 10, null, searched: true);

            // Assert
            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)result).Model);
            Assert.Single(model);
            Assert.Equal("Logout", model[0].Action);
        }

        [Fact]
        public async Task Index_FilterByEntityType_Authentication_ReturnsAllAuthLogs()
        {
            // Arrange
            _context.AuditLogs.Add(new AuditLog { Action = "Login", EntityType = "Authentication", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "Logout", EntityType = "Authentication", Timestamp = DateTime.UtcNow });
            _context.AuditLogs.Add(new AuditLog { Action = "Modified", EntityType = "User", Timestamp = DateTime.UtcNow });
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Index(null, "Authentication", null, null, null, 1, 10, null, searched: true);

            // Assert
            var model = Assert.IsAssignableFrom<PaginatedList<AuditLog>>(((ViewResult)result).Model);
            Assert.Equal(2, model.TotalItems);
            Assert.All(model, l => Assert.Equal("Authentication", l.EntityType));
        }
    }
}
