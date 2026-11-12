using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Services;

/// <summary>
/// 簽核畫面的單據類型名稱：原本直接顯示代碼（PurchaseOrder、CodeEntry），中英介面都一樣。
/// 用真的 resx 驗證——mock 掉 localizer 的話，驗到的是測試自己給的字串。
/// </summary>
public class ApprovalDocumentTypeLabelsTests
{
    private sealed class FakeContributor : IApprovalDocumentTypeLabelContributor
    {
        public string? GetLabel(string documentType) => documentType switch
        {
            "PurchaseOrder" => CultureInfo.CurrentUICulture.Name == "en-US" ? "Purchase Order" : "採購單",
            "CodeEntry"     => "contributor-wins",
            _               => null
        };
    }

    private static IApprovalDocumentTypeLabels Build(params IApprovalDocumentTypeLabelContributor[] contributors)
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(o => o.ResourcesPath = "Resources")
            .BuildServiceProvider();
        return new ApprovalDocumentTypeLabels(contributors, sp.GetRequiredService<IStringLocalizer<SharedResource>>());
    }

    private static T InCulture<T>(string culture, Func<T> f)
    {
        var original = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try { return f(); }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [Theory]
    [InlineData("zh-TW", "代碼維護")]
    [InlineData("en-US", "Code Maintenance")]
    public void Core自己的單據類型_依語系取SharedResource(string culture, string expected)
        => Assert.Equal(expected, InCulture(culture, () => Build().Get("CodeEntry")));

    [Theory]
    [InlineData("zh-TW", "採購單")]
    [InlineData("en-US", "Purchase Order")]
    public void 部署專案的單據類型_由contributor提供(string culture, string expected)
        => Assert.Equal(expected, InCulture(culture, () => Build(new FakeContributor()).Get("PurchaseOrder")));

    [Fact]
    public void contributor優先於Core()
        => Assert.Equal("contributor-wins", InCulture("zh-TW", () => Build(new FakeContributor()).Get("CodeEntry")));

    [Fact]
    public void 沒有任何名稱_顯示代碼原樣而不是空白()
        => Assert.Equal("NewDocType", InCulture("zh-TW", () => Build(new FakeContributor()).Get("NewDocType")));
}
