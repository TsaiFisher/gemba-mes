using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.Conventions
{
    /// <summary>
    /// 用真實的 contributor 組出完整選單樹，給慣例規則使用。
    ///
    /// <para><b>為什麼不用 regex 掃原始碼</b>：選單是三支 contributor 合出來的，
    /// 而且其中一段是<b>由設定開關決定的</b>（報表工具三頁）。掃字串看不到
    /// 「同一個鍵被兩支 contributor 各登錄一次」這種跨檔的形狀，
    /// 也分不出「群組標題」與「可授權畫面」——R39 的例外正好就落在那條界線上。</para>
    ///
    /// <para>設定與 <c>MenuServiceTests</c> 一致：報表工具開關<b>打開</b>，
    /// 讓那三支畫面也進入守門範圍。關著的話它們的鍵永遠掃不到。</para>
    /// </summary>
    internal static class ConventionMenu
    {
        public static List<MenuItem> Build()
        {
            var core = new Mock<IStringLocalizer<SharedResource>>();
            core.Setup(l => l[It.IsAny<string>()])
                .Returns((string key) => new LocalizedString(key, key));

            var pz = new Mock<IStringLocalizer<ProjectZSharedResource>>();
            pz.Setup(l => l[It.IsAny<string>()])
                .Returns((string key) => new LocalizedString(key, key));

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SmartFactory.MES.Modules.RPS.ReportToolsFeature.ConfigKey] = "true",
                })
                .Build();

            var contributors = new IMenuContributor[]
            {
                new CoreMenuContributor(core.Object),
                new ProjectZMenuContributor(pz.Object, config),
                new UserSettingsMenuContributor(core.Object)
            };

            return new MenuService(contributors).GetFullMenu();
        }
    }
}
