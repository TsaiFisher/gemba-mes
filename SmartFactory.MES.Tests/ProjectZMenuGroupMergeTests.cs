using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

/// <summary>
/// ProjectZ 把畫面掛進 Core 的「管理系統」群組（2026-10-27：各廠參數覆寫一覽由基本資料搬來）。
/// 靠的是 MenuService 合併同 MenuKey 的群組；合併失效時側邊欄會多出第二個「管理系統」，
/// 而 R39 只管有畫面的選單鍵、看不到重複的群組標題，所以在這裡釘住。
/// </summary>
public class ProjectZMenuGroupMergeTests
{
    private static List<MenuItem> BuildMenu(bool qaEnabled)
    {
        var core = new Mock<IStringLocalizer<SharedResource>>();
        core.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        var pz = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        pz.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:QaAssistant:Enabled"] = qaEnabled ? "true" : "false" })
            .Build();
        return new MenuService(new IMenuContributor[]
        {
            new CoreMenuContributor(core.Object),
            new ProjectZMenuContributor(pz.Object, config),
            new UserSettingsMenuContributor(core.Object),
        }).GetFullMenu();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // QA 開啟時另掛「擷取物一覽」，以前會長出第二個管理系統群組
    public void AdminManagement_IsSingleGroup_ContainingCoreAndProjectZScreens(bool qaEnabled)
    {
        var menu = BuildMenu(qaEnabled);

        var admin = Assert.Single(menu, m => m.MenuKey == "AdminManagement");
        Assert.Contains(admin.SubItems, s => s.MenuKey == "SystemParam");
        Assert.Contains(admin.SubItems, s => s.MenuKey == "PlantParamOverview");
        Assert.Equal(qaEnabled, admin.SubItems.Any(s => s.MenuKey == "QaExtract"));
    }

    [Fact]
    public void PlantParamOverview_NoLongerUnderBasicData()
    {
        var bas = Assert.Single(BuildMenu(false), m => m.MenuKey == "BasManagement");
        Assert.DoesNotContain(bas.SubItems, s => s.MenuKey == "PlantParamOverview");
    }
}
