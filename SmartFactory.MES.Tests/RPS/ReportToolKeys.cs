using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Tests.RPS;

/// <summary>
/// 已註冊報表工具的 <c>ToolKey</c> 母體，**由 DI 解析而來，不是手抄清單**。
///
/// <para>為什麼要有這個：原本 <c>SavedQueryServiceTests</c> 與 <c>ReportToolCatalogTests</c>
/// 各自抄了一份「當時有哪幾支」。手抄清單守不住新增——工具從六支長到十七支的過程中，
/// 沒有任何一支測試變紅，而那十一支新工具的「我的常用查詢」在畫面上根本不存在。
/// 母體一旦來自 DI，漏登錄就會當場被抓到。</para>
/// </summary>
internal static class ReportToolKeys
{
    private static readonly Lazy<string[]> Keys = new(Resolve);

    /// <summary>依 Ordinal 排序的全部 ToolKey。</summary>
    public static string[] All => Keys.Value;

    private static string[] Resolve()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("report-tool-keys-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var ctx = new ApplicationDbContext(options);

        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        services.AddSingleton(new Mock<IDateTimeService>().Object);
        services.AddSingleton(new Mock<IPermissionService>().Object);
        services.AddSingleton(new Mock<IUserContext>().Object);
        // RPT 的工具鏈（ContributionMarginService 等）另外吃系統參數服務。
        // GetAllAsync 必須回空字典而非 Mock 預設的 null——正式實作回的是空字典，
        // 回 null 會讓「參數尚未設定」在測試裡變成 NRE，那是假的失敗。
        var sysParam = new Mock<ISystemParamService>();
        sysParam.Setup(s => s.GetAllAsync()).ReturnsAsync(new Dictionary<string, string?>());
        services.AddSingleton(sysParam.Object);
        services.AddLogging();
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        // 幣別服務吃 IMemoryCache（模組登錄的是真實作）。少了它，任何注入
        // ICurrencyService 的工具都會在解析時拋，而訊息指向 CurrencyService
        // 不是那支工具——2026-09-23 加委外價格分析時實際撞過。
        services.AddMemoryCache();

        var config = new ConfigurationBuilder().Build();
        // 有登錄 IReportToolDescriptor 的模組都要在這裡——漏一個，母體就少一截，
        // 而「母體少一截」的症狀正好是規則靜默失效。
        RegisterToolModules(services, config);

        using var sp = services.BuildServiceProvider();
        return sp.GetServices<IReportToolDescriptor>()
                 .Select(t => t.ToolKey)
                 .OrderBy(k => k, StringComparer.Ordinal)
                 .ToArray();
    }

    /// <summary>
    /// 註冊所有「有登錄 <c>IReportToolDescriptor</c>」的模組。
    ///
    /// <para><b>單一來源。</b>這份清單原本被三支守門各抄一份
    /// （<c>ReportToolCatalogTests</c>、<c>ReportExportHeaderTests</c>、本檔），
    /// 而它**已經漏過一次**——<c>RPTModule</c> 缺席期間，那幾支自稱「全部工具」的斷言
    /// 一支 RPT 工具都沒看過，而且全綠。</para>
    ///
    /// <para>BAS 必須在內：RPT 的財報服務吃 BAS 的 <c>IAccountClassifier</c>。
    /// 少了它容器解不出任何 <c>IReportToolDescriptor</c>，症狀是母體整個變空。</para>
    /// </summary>
    internal static void RegisterToolModules(IServiceCollection services, IConfiguration config)
    {
        new SmartFactory.MES.Modules.BAS.BASModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.RPS.RPSModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.ORD.ORDModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.INV.INVModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.PUR.PURModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.MFG.MFGModule().RegisterServices(services, config);
        new SmartFactory.MES.Modules.RPT.RPTModule().RegisterServices(services, config);
        // ACT：2026-09-23 的試算表是本模組第一支報表工具。漏了它的症狀與當年漏 RPTModule
        // 一模一樣——母體少一支，而「全部工具」的斷言照樣全綠。
        new SmartFactory.MES.Modules.ACT.ACTModule().RegisterServices(services, config);
    }
}
