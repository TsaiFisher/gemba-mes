using Microsoft.EntityFrameworkCore;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests
{
    public class PermissionServiceTests
    {
        private ApplicationDbContext GetDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            return new ApplicationDbContext(options);
        }

        [Fact]
        public async Task CanViewData_SameRole_ReturnsTrue()
        {
            // Arrange
            var db = GetDbContext();
            var roleId = "role1";
            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(u => u.ActiveRoleId).Returns(roleId);

            var service = new PermissionService(db, mockUserContext.Object, new FallbackSecurityContext(new LicenseContext()));

            // Act
            var result = await service.CanViewData(roleId);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task CanViewData_SupervisorHigherLevel_ReturnsTrue()
        {
            // Arrange
            var db = GetDbContext();
            var dept = new OrganizationUnit { Id = "Dept1", Name = "Dept1", Path = "Root/Dept1" };
            var section = new OrganizationUnit { Id = "Sec1", Name = "Sec1", Path = "Root/Dept1/Sec1", ParentId = "Dept1" };
            
            var supervisorRole = new ApplicationRole { Id = "SupRole", OrganizationUnitId = "Dept1" };
            var staffRole = new ApplicationRole { Id = "StaffRole", OrganizationUnitId = "Sec1" };

            db.OrganizationUnits.AddRange(dept, section);
            db.Roles.AddRange(supervisorRole, staffRole);
            await db.SaveChangesAsync();

            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(u => u.ActiveRoleId).Returns("SupRole");

            var service = new PermissionService(db, mockUserContext.Object, new FallbackSecurityContext(new LicenseContext()));

            // Act
            var result = await service.CanViewData("StaffRole");

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task CanViewData_DifferentBranches_ReturnsFalse()
        {
            // Arrange
            var db = GetDbContext();
            var dept1 = new OrganizationUnit { Id = "Dept1", Name = "Dept1", Path = "Root/Dept1" };
            var dept2 = new OrganizationUnit { Id = "Dept2", Name = "Dept2", Path = "Root/Dept2" };
            
            var role1 = new ApplicationRole { Id = "Role1", OrganizationUnitId = "Dept1" };
            var role2 = new ApplicationRole { Id = "Role2", OrganizationUnitId = "Dept2" };

            db.OrganizationUnits.AddRange(dept1, dept2);
            db.Roles.AddRange(role1, role2);
            await db.SaveChangesAsync();

            var mockUserContext = new Mock<IUserContext>();
            mockUserContext.Setup(u => u.ActiveRoleId).Returns("Role1");

            var service = new PermissionService(db, mockUserContext.Object, new FallbackSecurityContext(new LicenseContext()));

            // Act
            var result = await service.CanViewData("Role2");

            // Assert
            Assert.False(result);
        }
    }
}
