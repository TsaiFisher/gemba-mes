using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

public class PermissionSnapshotTests : TestBase
{
    [Fact]
    public async Task Snapshot_IsPerRoleAndDoesNotSurviveNextRequest()
    {
        var user = new Mock<IUserContext>();
        var role = "r1";
        user.Setup(x => x.ActiveRoleId).Returns(() => role);
        var permission = new MenuPermission { RoleId = role, MenuKey = "review", CanRead = true, CanPrint = true };
        _context.MenuPermissions.Add(permission);
        await _context.SaveChangesAsync();
        PermissionService NewRequest() => new(_context, user.Object, new FallbackSecurityContext(new LicenseContext()));
        var request = NewRequest();
        Assert.True(await request.CanRead("review"));
        Assert.True(await request.CanPrint("review"));
        Assert.False(await request.CanCreate("review"));
        role = "r2";
        Assert.False(await request.CanRead("review"));
        permission.CanRead = false;
        await _context.SaveChangesAsync();
        role = "r1";
        Assert.False(await NewRequest().CanRead("review"));
    }
}
