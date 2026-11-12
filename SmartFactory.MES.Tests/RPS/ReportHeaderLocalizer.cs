using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 匯出欄頭用的**真** resx localizer（§16.1 S-5）。
///
/// <para>畫面匯出與排程匯出的欄頭都由 <c>IReportToolDescriptor.GetExportColumns</c>
/// 宣告的資源鍵解析，換成 mock localizer 會讓每個鍵都回傳可預期的假字串——
/// 於是「鍵不存在時 <c>IStringLocalizer</c> 靜默回傳鍵本身」這一類問題
/// **在測試裡看不出來**，而那正是欄頭最容易壞的方式。</para>
///
/// <para>住在獨立檔案而不是 <c>TestBase</c>：<c>TestBase.cs</c> 是 Core 既有檔，
/// 就地新增成員會被 <c>scripts/check-core-drift.sh</c> 擋下（核心區域只能新增檔案）。</para>
/// </summary>
internal static class ReportHeaderLocalizer
{
    // ServiceProvider 建一次就好：resx 查找本身有快取，每個測試各建一份只是白花時間。
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddLogging()
        .AddLocalization(o => o.ResourcesPath = "Resources")
        .BuildServiceProvider();

    internal static IStringLocalizer<ProjectZReportHeaderResource> Create()
        => Services.GetRequiredService<IStringLocalizer<ProjectZReportHeaderResource>>();
}
