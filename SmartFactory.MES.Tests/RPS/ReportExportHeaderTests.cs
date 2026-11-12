using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;
using SmartFactory.MES.Tests.Conventions;
using Xunit;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 匯出欄頭的兩條守門（§16.1 S-5）。
///
/// <para><b>一、鍵要有值</b>：工具宣告的每一個 <c>HeaderKey</c>，
/// zh-TW 與 en-US 都必須查得到非空的值。查不到時 <c>IStringLocalizer</c>
/// **不會拋例外，它回傳鍵本身**——匯出檔的欄頭就變成 `PurchaseOverdue.OrderQty`，
/// 而那看起來只是「怪」，不像錯誤。</para>
///
/// <para><b>二、畫面匯出要走共用路徑</b>：2026-09-15 S-5 收尾之前，這一條比對的是
/// 「工具宣告的欄頭」與「畫面 <c>Export</c> 裡硬編的雙語陣列」逐欄相同——因為當時有兩份。
/// 現在十一支畫面都改走 <c>ReportToolExportExtensions.ToExportRequest</c>、硬編陣列已刪，
/// 那條比對的 probed 會掉到 0，照 #158 的交代拿掉了，**不是留著一條永遠綠的測試**。</para>
///
/// <para>但「只有一份」是**現在**的狀態，不是結構上的保證：下一個人完全可以在 <c>Export</c>
/// 裡重新寫回一組 <c>Headers = isZh ? [...] : [...]</c>，那一刻兩份又出現了，而且
/// <b>仍然不會有任何測試變紅</b>——排程與畫面各自都「對」。所以第二條改成守那件事本身：
/// 十一支工具對應的 Controller 必須呼叫共用路徑，且不得再出現硬編標題陣列。</para>
/// </summary>
public class ReportExportHeaderTests : TestBase
{
    /// <summary>
    /// 鍵要查得到值的語系，**包含中性語系**。
    ///
    /// <para>中性語系那一格不是湊數的：CI 就是在它底下跑的。`.zh-TW`／`.en-US` 兩份
    /// 之外若沒有一份無語系的 resx，`ResourceManager` 找不到任何 fallback，
    /// <c>IStringLocalizer</c> 會**靜默回傳鍵本身**，於是匯出檔的欄頭變成
    /// `InventoryValuation.ProductCode`。2026-09-15 本機（en-US）全綠、CI 紅在這一格，
    /// 所以中性 resx 與這一格是一起補的。</para>
    /// </summary>
    private static readonly string?[] KeyCultures = ["zh-TW", "en-US", null];

    private static List<IReportToolDescriptor> RegisteredTools(ApplicationDbContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        services.AddSingleton(new Mock<IDateTimeService>().Object);
        services.AddSingleton(new Mock<IPermissionService>().Object);
        services.AddSingleton(new Mock<IUserContext>().Object);
        // RPT 批次 2 的 service 需要這兩個；本測試只問「工具宣告了哪些欄位」，
        // 不執行查詢，所以替身即可——但**必須登錄**，否則工具連建構都建不起來。
        services.AddSingleton(new Mock<ISystemParamService>().Object);
        services.AddSingleton(new Mock<ICurrencyService>().Object);
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.AddLogging();
        // 幣別服務吃 IMemoryCache（模組登錄的是真實作，非 mock）；少了它會在解析
        // 注入 ICurrencyService 的工具時拋，而訊息指向 CurrencyService 不是那支工具。
        services.AddMemoryCache();

        var config = new ConfigurationBuilder().Build();
        // 模組清單是單一來源（含 BAS——RPT 財報吃它的 IAccountClassifier）：
        ReportToolKeys.RegisterToolModules(services, config);

        var sp = services.BuildServiceProvider();
        return sp.GetServices<IReportToolDescriptor>().ToList();
    }

    private static IStringLocalizer<ProjectZReportHeaderResource> Localizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();          // ResourceManagerStringLocalizerFactory 需要 ILoggerFactory
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        return services.BuildServiceProvider()
                       .GetRequiredService<IStringLocalizer<ProjectZReportHeaderResource>>();
    }

    private static ReportRequestBase DefaultRequest(IReportToolDescriptor tool, string? groupBy = null)
    {
        var request = (ReportRequestBase)Activator.CreateInstance(tool.RequestType)!;
        request.PlantId = "P001";
        request.GroupBy = groupBy;
        return request;
    }

    /// <summary>
    /// 每一支已登錄工具宣告的每一個欄頭鍵，兩個語系都要查得到非空、且**不等於鍵本身**的值。
    /// </summary>
    [Fact]
    public void 每個欄頭鍵在兩個語系都有值()
    {
        var tools     = RegisteredTools(_context);
        var localizer = Localizer();
        var missing   = new List<string>();
        var probed    = 0;

        foreach (var culture in KeyCultures)
        {
            var previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = culture is null
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(culture);
            try
            {
                foreach (var tool in tools)
                foreach (var column in tool.GetExportColumns(DefaultRequest(tool)))
                {
                    probed++;
                    var value = localizer[column.HeaderKey];
                    if (value.ResourceNotFound || string.IsNullOrWhiteSpace(value.Value))
                        missing.Add($"{culture ?? "（中性語系）"}：{column.HeaderKey}");
                }
            }
            finally { CultureInfo.CurrentUICulture = previous; }
        }

        Assert.True(probed >= 150,
            $"只探測到 {probed} 個欄頭鍵——工具解析或欄位宣告已失效，規則形同關閉");
        Assert.True(missing.Count == 0,
            "下列欄頭鍵在該語系查不到值（IStringLocalizer 會靜默回傳鍵本身，" +
            "匯出檔的欄頭會變成 `Tool.Column` 這種字串）：\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// 十一支工具對應的畫面 <c>Export</c> 必須走共用路徑，且不得自己再排一次欄頭。
    ///
    /// <para>守的是「只有一份欄位宣告」這個結構，不是某一組字串的值。硬編陣列復活時
    /// 排程與畫面會各自輸出一份「都對但不一樣」的檔案——那種漂移在使用者拿兩份檔案
    /// 並排之前沒有任何徵兆，正是 S-5 要消滅的東西。</para>
    /// </summary>
    [Fact]
    public void 畫面匯出一律走工具宣告的欄位()
    {
        var tools    = RegisteredTools(_context);
        var root     = ConventionScan.RepoRoot();
        var problems = new List<string>();
        var probed   = 0;

        foreach (var tool in tools)
        {
            var file = Directory.EnumerateFiles(
                    Path.Combine(root, "Modules"), $"{tool.ToolKey}Controller.cs",
                    SearchOption.AllDirectories)
                .SingleOrDefault();

            Assert.True(file is not null,
                $"找不到工具 {tool.ToolKey} 對應的 Controller 原始碼（命名須為 {tool.ToolKey}Controller.cs）");

            probed++;
            var source = File.ReadAllText(file!);

            if (!source.Contains("ToExportRequest("))
                problems.Add($"{tool.ToolKey}Controller：Export 沒有走 ToExportRequest(...)");

            if (HardCodedHeaders.IsMatch(source))
                problems.Add(
                    $"{tool.ToolKey}Controller：Export 裡又出現硬編的雙語標題陣列" +
                    "（欄頭只能有一份，見 IReportToolDescriptor.GetExportColumns）");
        }

        Assert.True(probed == tools.Count && probed >= 11,
            $"只檢查到 {probed} 支 Controller（工具共 {tools.Count} 支）——規則形同關閉");
        Assert.True(problems.Count == 0,
            "畫面匯出未走共用欄位宣告：\n  " + string.Join("\n  ", problems));
    }

    /// <summary>S-5 之前的形狀：<c>Headers = isZh ? [...] : [...]</c>。復活即紅。</summary>
    private static readonly Regex HardCodedHeaders = new(
        @"Headers\s*=\s*isZh\s*\?", RegexOptions.Compiled);
}
