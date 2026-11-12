using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;
using Xunit;

namespace SmartFactory.MES.Tests.Conventions;

/// <summary>
/// 每一種送審單據類型（<see cref="ApprovalDocumentLinks.Templates"/>）在簽核畫面都要有中英名稱。
/// 缺名稱不會報錯，只會在畫面上露出代碼（PurchaseOrder…），中英介面都一樣——2026-10-27 回報的就是這個。
/// 用真的 resx，不 mock localizer。
/// </summary>
public class ApprovalDocumentTypeLabelCoverageTests
{
    private static readonly Regex Cjk = new(@"\p{IsCJKUnifiedIdeographs}");

    private static IApprovalDocumentTypeLabels Build()
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(o => o.ResourcesPath = "Resources")
            .BuildServiceProvider();
        return new ApprovalDocumentTypeLabels(
            new[] { new ApprovalDocumentTypeLabelContributor(sp.GetRequiredService<IStringLocalizer<ProjectZSharedResource>>()) },
            sp.GetRequiredService<IStringLocalizer<SharedResource>>());
    }

    public static IEnumerable<object[]> DocTypes() => ApprovalDocumentLinks.Templates.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(DocTypes))]
    public void 每種送審單據類型都有中英名稱(string docType)
    {
        var labels = Build();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("zh-TW");
            var zh = labels.Get(docType);
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var en = labels.Get(docType);

            Assert.NotEqual(docType, zh);
            Assert.Matches(Cjk, zh);
            Assert.NotEqual(docType, en);
            Assert.DoesNotMatch(Cjk, en);
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }
}
