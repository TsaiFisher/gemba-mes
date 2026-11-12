using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.Extensions.Configuration;
using Moq;
using SmartFactory.MES.Models;
using SmartFactory.MES.Pages;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests;

/// <summary>
/// 隱私權政策頁的組態與擴充點。渲染結果另由整合測試 PrivacyPageTests 驗證；
/// 這裡驗整合測試碰不到的分支（有設定組態、部署專案有補充段落）。
/// </summary>
public class PrivacyModelTests
{
    private static PrivacyModel Run(string culture, Dictionary<string, string?> settings, params string[] existingViews) =>
        Run(culture, settings, new Dictionary<string, string?>(), existingViews);

    private static PrivacyModel Run(string culture, Dictionary<string, string?> settings,
        Dictionary<string, string?> systemParams, params string[] existingViews)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var engine = new Mock<ICompositeViewEngine>();
        engine.Setup(e => e.FindView(It.IsAny<ActionContext>(), It.IsAny<string>(), false))
              .Returns((ActionContext _, string name, bool _) => existingViews.Contains(name)
                  ? ViewEngineResult.Found(name, Mock.Of<IView>())
                  : ViewEngineResult.NotFound(name, Array.Empty<string>()));

        var paramService = new Mock<ISystemParamService>();
        paramService.Setup(p => p.GetAllAsync()).ReturnsAsync(systemParams);
        // 不得走 GetCompanyNameAsync：查無值時回傳軟體名稱，會把供應商寫成資料管理者
        paramService.Setup(p => p.GetCompanyNameAsync(It.IsAny<bool>())).ReturnsAsync("SmartFactory");

        var model = new PrivacyModel(config, engine.Object, paramService.Object) { PageContext = new PageContext() };
        var saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            model.OnGetAsync().GetAwaiter().GetResult();
        }
        finally
        {
            CultureInfo.CurrentUICulture = saved;
        }
        return model;
    }

    [Fact]
    public void 設定了部署單位與聯絡窗口_頁面取得去頭尾空白的值()
    {
        var model = Run("zh-TW", new()
        {
            [PrivacyModel.OrganizationKey] = "  範例股份有限公司 ",
            [PrivacyModel.ContactKey] = "privacy@example.com",
        });

        Assert.Equal("範例股份有限公司", model.Organization);
        Assert.Equal("privacy@example.com", model.Contact);
    }

    [Fact]
    public void 組態留空或只有空白_視為未設定_交由頁面退回通用寫法()
    {
        var model = Run("zh-TW", new()
        {
            [PrivacyModel.OrganizationKey] = "",
            [PrivacyModel.ContactKey] = "   ",
        });

        Assert.Null(model.Organization);
        Assert.Null(model.Contact);
    }

    [Theory]
    [InlineData("zh-TW", true)]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", false)]
    [InlineData("ja-JP", false)]   // 其餘語系一律英文版
    public void 語系判斷(string culture, bool expectChinese)
    {
        Assert.Equal(expectChinese, Run(culture, new()).IsChinese);
    }

    [Fact]
    public void 部署專案沒有補充段落時_不顯示()
    {
        Assert.Null(Run("zh-TW", new()).ExtraPartial);
    }

    [Theory]
    [InlineData("zh-TW", "_PrivacyExtra.zh-TW")]
    [InlineData("en-US", "_PrivacyExtra.en-US")]
    public void 部署專案提供了該語系的補充段落_就接上(string culture, string expected)
    {
        var model = Run(culture, new(), "_PrivacyExtra.zh-TW", "_PrivacyExtra.en-US");
        Assert.Equal(expected, model.ExtraPartial);
    }

    [Fact]
    public void 只提供中文補充段落時_英文頁不會誤用中文那份()
    {
        var model = Run("en-US", new(), "_PrivacyExtra.zh-TW");
        Assert.Null(model.ExtraPartial);
    }

    // ── 部署單位名稱：組態 → 系統參數公司名稱 → 通用寫法 ───────────────────────

    private static Dictionary<string, string?> Company(string? zh, string? en) => new()
    {
        [SystemParamKeys.CompanyNameZh] = zh,
        [SystemParamKeys.CompanyNameEn] = en,
    };

    [Theory]
    [InlineData("zh-TW", "範例股份有限公司")]
    [InlineData("en-US", "Example Co., Ltd.")]
    public void 組態未設定_改用系統參數中對應語系的公司名稱(string culture, string expected)
    {
        var model = Run(culture, new(), Company("範例股份有限公司", "Example Co., Ltd."));
        Assert.Equal(expected, model.Organization);
    }

    [Theory]
    [InlineData("zh-TW", null, "Example Co., Ltd.", "Example Co., Ltd.")]
    [InlineData("en-US", "範例股份有限公司", "  ", "範例股份有限公司")]
    public void 該語系的公司名稱沒填_用另一語系的(string culture, string? zh, string? en, string expected)
    {
        Assert.Equal(expected, Run(culture, new(), Company(zh, en)).Organization);
    }

    [Fact]
    public void 組態有明確指定_優先於系統參數()
    {
        var model = Run("zh-TW",
            new() { [PrivacyModel.OrganizationKey] = "隱私權專責公司" },
            Company("範例股份有限公司", "Example Co., Ltd."));
        Assert.Equal("隱私權專責公司", model.Organization);
    }

    [Fact]
    public void 系統參數也沒有公司名稱_維持null交由頁面顯示通用寫法_不會冒出軟體名稱()
    {
        var model = Run("zh-TW", new(), Company(null, ""));
        Assert.Null(model.Organization);
    }
}
