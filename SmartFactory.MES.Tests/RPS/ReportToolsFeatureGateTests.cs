using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.RPS;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 報表工具開關（<c>Features:ReportTools:Enabled</c>）與三支畫面的授權守門。
///
/// <para>這兩件事失效時都不會有錯誤訊息：開關失效只是「選單多了三項」，
/// 授權寫錯只是「某個角色多看得到別人的排程」——兩者都要靠測試才看得出來。</para>
/// </summary>
public class ReportToolsFeatureGateTests
{
    private const string ScheduleKey = "ReportSchedule";
    private const string InboxKey = "ReportInbox";
    private const string HistoryKey = "ReportRunHistory";

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [ReportToolsFeature.ConfigKey] = value })
            .Build();

    private static List<MenuItem> Menu(IConfiguration configuration)
    {
        var localizer = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()])
                 .Returns((string k) => new LocalizedString(k, k));
        return new ProjectZMenuContributor(localizer.Object, configuration).GetMenuItems();
    }

    private static IEnumerable<string> AllMenuKeys(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item.MenuKey;
            foreach (var key in AllMenuKeys(item.SubItems)) yield return key;
        }
    }

    [Fact]
    public void 開關未設定時預設關閉()
    {
        Assert.False(ReportToolsFeature.IsEnabled(Config(null)));
        Assert.False(ReportToolsFeature.IsEnabled(null));
    }

    [Fact]
    public void 開關關閉時三支畫面完全不出現在選單()
    {
        var keys = AllMenuKeys(Menu(Config("false"))).ToHashSet();

        // 沒有選單就沒有 MenuPermission 種子，直接打 URL 也會被 default-deny 擋下。
        Assert.DoesNotContain(ScheduleKey, keys);
        Assert.DoesNotContain(InboxKey, keys);
        Assert.DoesNotContain(HistoryKey, keys);

        // 其餘選單不受影響——開關罩的只有 Phase 0-B 新增的部分。
        Assert.Contains("Dashboard", keys);
        Assert.Contains("RptManagement", keys);
    }

    [Fact]
    public void 開關開啟時三支畫面掛在報表群組下()
    {
        var menu = Menu(Config("true"));
        var rpt = menu.SelectMany(m => m.SubItems.Prepend(m))
                      .First(m => m.MenuKey == "RptManagement");
        var subKeys = rpt.SubItems.Select(s => s.MenuKey).ToList();

        Assert.Contains(ScheduleKey, subKeys);
        Assert.Contains(InboxKey, subKeys);
        Assert.Contains(HistoryKey, subKeys);
    }

    [Theory]
    [InlineData(InboxKey, new[] { "Read" })]
    [InlineData(HistoryKey, new[] { "Read" })]
    [InlineData(ScheduleKey, new[] { "Read", "Create", "Update", "Delete" })]
    public void 支援動詞照實宣告(string menuKey, string[] expected)
    {
        var item = AllItems(Menu(Config("true"))).First(i => i.MenuKey == menuKey);

        // 多宣告的動詞會在權限維護畫面長出永遠沒有作用的勾選框，
        // 也會讓「MGR 全站唯讀」這類跨畫面不變式被一筆假的寫入權打破。
        Assert.Equal(expected, item.SupportedActions);
    }

    private static IEnumerable<MenuItem> AllItems(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var sub in AllItems(item.SubItems)) yield return sub;
        }
    }

    [Fact]
    public void 收件匣全角色可讀_排程與執行紀錄限管理視角()
    {
        var screens = RolePermissionSeeder.CollectScreens(new IMenuContributor[]
        {
            new ProjectZMenuContributor(Localizer(), Config("true")),
        });

        // 收件匣是個人工具：查詢層已限「我是 owner 或收件人」，下載時再依目標報表的
        // MenuKey 檢查一次（§7.8 S5），所以對所有角色開放不構成越權。
        foreach (var role in new[] { "OP", "SALES", "WH", "PUR", "FIN", "MGR", "HQMGR" })
            Assert.True(Perm(role, screens, InboxKey)?.CanRead,
                $"{role} 應該看得到自己的報表收件匣");

        // 排程維護與執行紀錄看得到別人的排程與收件人＝看得到「誰在定期收哪些機密報表」。
        Assert.Null(Perm("OP", screens, ScheduleKey));
        Assert.Null(Perm("SALES", screens, HistoryKey));

        Assert.True(Perm("HQMGR", screens, ScheduleKey)?.CanCreate);
        Assert.True(Perm("MGR", screens, ScheduleKey)?.CanRead);
        Assert.False(Perm("MGR", screens, ScheduleKey)?.CanCreate);
    }

    private static IStringLocalizer<ProjectZSharedResource> Localizer()
    {
        var localizer = new Mock<IStringLocalizer<ProjectZSharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()])
                 .Returns((string k) => new LocalizedString(k, k));
        return localizer.Object;
    }

    private static MenuPermission? Perm(
        string roleId, IReadOnlyList<RolePermissionSeeder.ScreenInfo> screens, string menuKey)
        => RolePermissionSeeder.ComputePermissions(roleId, screens)
            .FirstOrDefault(p => p.MenuKey == menuKey);
}
