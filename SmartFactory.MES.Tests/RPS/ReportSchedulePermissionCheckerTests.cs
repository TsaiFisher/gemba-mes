using Microsoft.AspNetCore.Identity;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 排程路徑的權限檢查（§7.8 S1／S3）。
///
/// <para><b>這組測試守的是一條真實的洩漏路徑</b>：建立排程時是業務主管、半年後調職，
/// 排程仍在每月寄出銷售明細。它不會拋例外、不會有錯誤畫面，
/// 唯一會發現的方式是有人剛好注意到自己還在收不該收的報表。</para>
/// </summary>
public class ReportSchedulePermissionCheckerTests : TestBase
{
    private const string ToolKey = "SalesRanking";

    private readonly Mock<IReportToolCatalog> _catalog = new();

    public ReportSchedulePermissionCheckerTests()
    {
        var descriptor = new Mock<IReportToolDescriptor>();
        descriptor.SetupGet(d => d.ToolKey).Returns(ToolKey);
        descriptor.SetupGet(d => d.Permission).Returns(ReportToolPermission.CanRead);

        _catalog.Setup(c => c.Find(ToolKey)).Returns(descriptor.Object);
        _catalog.Setup(c => c.Find(It.Is<string>(k => k != ToolKey))).Returns((IReportToolDescriptor?)null);
    }

    private ReportSchedulePermissionChecker CreateChecker()
        => new(_context, _catalog.Object);

    private void SeedUser(string id, DateTimeOffset? lockoutEnd = null)
    {
        _context.Users.Add(new ApplicationUser { Id = id, UserName = id, LockoutEnd = lockoutEnd });
        _context.SaveChanges();
    }

    private void SeedRole(string roleId)
    {
        _context.Roles.Add(new ApplicationRole { Id = roleId, Name = roleId });
        _context.SaveChanges();
    }

    private void SeedUserRole(string userId, string roleId)
    {
        _context.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
        _context.SaveChanges();
    }

    private void SeedPermission(string roleId, bool canRead = true, bool canCreate = false)
    {
        _context.MenuPermissions.Add(new MenuPermission
        {
            RoleId = roleId, MenuKey = ToolKey, CanRead = canRead, CanCreate = canCreate,
        });
        _context.SaveChanges();
    }

    private void SeedHealthyOwner(string userId = "u1", string roleId = "r1")
    {
        SeedUser(userId);
        SeedRole(roleId);
        SeedUserRole(userId, roleId);
        SeedPermission(roleId);
    }

    [Fact]
    public async Task 一切正常時放行()
    {
        SeedHealthyOwner();

        var result = await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey);

        Assert.True(result.Granted);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task 工具已不存在時拒絕()
    {
        SeedHealthyOwner();

        var result = await CreateChecker().CheckOwnerAsync("u1", "r1", "RemovedTool");

        Assert.False(result.Granted);
        Assert.Contains("已不存在", result.Reason);
    }

    [Fact]
    public async Task 帳號已刪除時拒絕()
    {
        SeedRole("r1");
        SeedPermission("r1");

        var result = await CreateChecker().CheckOwnerAsync("ghost", "r1", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("已刪除", result.Reason);
    }

    [Fact]
    public async Task 帳號停用時拒絕()
    {
        // 本專案的「停用」是 Identity 的 LockoutEnd，沒有另外的 IsActive 欄位。
        SeedUser("u1", lockoutEnd: DateTimeOffset.UtcNow.AddYears(1));
        SeedRole("r1");
        SeedUserRole("u1", "r1");
        SeedPermission("r1");

        var result = await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("停用", result.Reason);
    }

    [Fact]
    public async Task 過期的鎖定不算停用()
    {
        // LockoutEnd 是過去的時間表示鎖定已解除，不該把人擋在外面。
        SeedUser("u1", lockoutEnd: DateTimeOffset.UtcNow.AddDays(-1));
        SeedRole("r1");
        SeedUserRole("u1", "r1");
        SeedPermission("r1");

        Assert.True((await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey)).Granted);
    }

    [Fact]
    public async Task 角色已刪除時拒絕()
    {
        SeedUser("u1");

        var result = await CreateChecker().CheckOwnerAsync("u1", "gone", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("角色已刪除", result.Reason);
    }

    [Fact]
    public async Task 已不屬於該角色時拒絕()
    {
        // ⭐ 這就是「調職」的形態：帳號還在、角色還在，只是人不在那個角色裡了。
        SeedUser("u1");
        SeedRole("r1");
        SeedPermission("r1");
        // 刻意不建 UserRole

        var result = await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("已不屬於該角色", result.Reason);
    }

    [Fact]
    public async Task 角色失去該報表權限時拒絕()
    {
        SeedUser("u1");
        SeedRole("r1");
        SeedUserRole("u1", "r1");
        SeedPermission("r1", canRead: false);

        var result = await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("無 SalesRanking 的執行權限", result.Reason);
    }

    [Fact]
    public async Task 動詞依工具宣告_CanCreate的工具不吃CanRead()
    {
        // 2026-08-14 裁決後真實工具全都宣告 CanRead，所以這裡用 mock descriptor：
        // 測的是「照工具自己宣告的動詞查」這個機制，不是任何一支工具的現況。
        // 把判定寫死成 CanRead 會讓日後某支畫面另立閘門變成無痕放寬
        // （CanRead 恆為 CanCreate 的真超集，改動不會有任何錯誤訊息）。
        var descriptor = new Mock<IReportToolDescriptor>();
        descriptor.SetupGet(d => d.ToolKey).Returns(ToolKey);
        descriptor.SetupGet(d => d.Permission).Returns(ReportToolPermission.CanCreate);
        _catalog.Setup(c => c.Find(ToolKey)).Returns(descriptor.Object);

        SeedUser("u1");
        SeedRole("r1");
        SeedUserRole("u1", "r1");
        SeedPermission("r1", canRead: true, canCreate: false);

        Assert.False((await CreateChecker().CheckOwnerAsync("u1", "r1", ToolKey)).Granted);

        SeedPermission("r2", canRead: false, canCreate: true);
        SeedRole("r2");
        SeedUserRole("u1", "r2");

        Assert.True((await CreateChecker().CheckOwnerAsync("u1", "r2", ToolKey)).Granted);
    }

    [Fact]
    public async Task 缺少擁有者或角色時拒絕()
    {
        var checker = CreateChecker();

        Assert.False((await checker.CheckOwnerAsync("", "r1", ToolKey)).Granted);
        Assert.False((await checker.CheckOwnerAsync("u1", "", ToolKey)).Granted);
    }

    [Fact]
    public async Task 收件人以自己的角色判定_任一角色有權即可()
    {
        // 用 owner 的角色去判定收件人，等於讓 owner 的權限傳染給收件人——那正是 §八 要防的事。
        SeedUser("recipient");
        SeedRole("noPerm");
        SeedRole("hasPerm");
        SeedUserRole("recipient", "noPerm");
        SeedUserRole("recipient", "hasPerm");
        SeedPermission("hasPerm");

        Assert.True((await CreateChecker().CheckRecipientAsync("recipient", ToolKey)).Granted);
    }

    [Fact]
    public async Task 收件人所有角色都無權時拒絕()
    {
        SeedUser("recipient");
        SeedRole("noPerm");
        SeedUserRole("recipient", "noPerm");

        var result = await CreateChecker().CheckRecipientAsync("recipient", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("無 SalesRanking 的檢視權限", result.Reason);
    }

    [Fact]
    public async Task 收件人帳號停用時拒絕()
    {
        SeedUser("recipient", lockoutEnd: DateTimeOffset.UtcNow.AddYears(1));
        SeedRole("r1");
        SeedUserRole("recipient", "r1");
        SeedPermission("r1");

        var result = await CreateChecker().CheckRecipientAsync("recipient", ToolKey);

        Assert.False(result.Granted);
        Assert.Contains("停用", result.Reason);
    }
}
