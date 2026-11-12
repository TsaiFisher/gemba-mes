using System.Globalization;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services;

/// <summary>
/// 驗證 Role 顯示名稱集中化邏輯（CodeEntryDisplayExtensions 樣態的姊妹擴充）。
/// </summary>
public class RoleDisplayExtensionsTests
{
    private static IDisposable WithCulture(string cultureName)
    {
        var original = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
        return new CultureScope(original);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _original;
        public CultureScope(CultureInfo original) { _original = original; }
        public void Dispose() => CultureInfo.CurrentUICulture = _original;
    }

    [Fact]
    public void GetDisplayName_EnUS_WithNameEn_ReturnsNameEn()
    {
        using var _ = WithCulture("en-US");
        var role = new ApplicationRole { Name = "Admin", NameZh = "系統管理員", NameEn = "Administrator" };
        Assert.Equal("Administrator", role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_EnUS_MissingNameEn_FallsBackToNameZh()
    {
        using var _ = WithCulture("en-US");
        var role = new ApplicationRole { Name = "Admin", NameZh = "系統管理員", NameEn = null };
        Assert.Equal("系統管理員", role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_ZhTW_ReturnsNameZh()
    {
        using var _ = WithCulture("zh-TW");
        var role = new ApplicationRole { Name = "Admin", NameZh = "系統管理員", NameEn = "Administrator" };
        Assert.Equal("系統管理員", role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_ZhTW_MissingNameZh_FallsBackToName()
    {
        using var _ = WithCulture("zh-TW");
        var role = new ApplicationRole { Name = "Admin", NameZh = null, NameEn = "Administrator" };
        Assert.Equal("Admin", role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_OtherCulture_FollowsZhBranch()
    {
        // 規格：僅 en-US 走英文分支，其他語系都走中文分支
        using var _ = WithCulture("ja-JP");
        var role = new ApplicationRole { Name = "Admin", NameZh = "系統管理員", NameEn = "Administrator" };
        Assert.Equal("系統管理員", role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_AllNamesNull_ReturnsEmptyString()
    {
        using var _ = WithCulture("zh-TW");
        var role = new ApplicationRole { Name = null, NameZh = null, NameEn = null };
        Assert.Equal(string.Empty, role.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_NullRole_ReturnsEmptyString()
    {
        ApplicationRole? role = null;
        Assert.Equal(string.Empty, role!.GetDisplayName());
    }
}
