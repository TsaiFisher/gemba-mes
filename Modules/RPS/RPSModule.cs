using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS;

/// <summary>
/// RPS＝Report Scheduling。Phase 0-B 的報表工具層／常用查詢／排程。
///
/// 註冊走 <c>IAppModule</c>，由 <c>Program.cs</c> 的組件掃描自動載入——**不改 Program.cs**。
/// 掃描用的是 <c>AppDomain.CurrentDomain.GetAssemblies()</c>（只掃已載入的組件），
/// 所以本模組必須留在主組件內；拆成獨立 csproj 會因 .NET 延遲載入而靜默不註冊（§17.11）。
/// </summary>
public class RPSModule : IAppModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IRelativePeriodResolver, RelativePeriodResolver>();

        // 目錄本身是 scoped：它注入 IEnumerable<IReportToolDescriptor>，而各工具是 scoped
        // （相依 ApplicationDbContext）。註冊成 singleton 會 captive dependency 抓住第一個
        // request 的 DbContext。
        services.AddScoped<IReportToolCatalog, ReportToolCatalog>();

        // L1 權限過濾的批次版（§8.4、§17.9 缺口 G1）。與 catalog 同為 scoped、同理由。
        // 無條件註冊：Phase 2 的 schema 組裝依賴它，而那條路徑掛在 QaAssistant 開關下，
        // 不是 ReportTools 開關——兩個開關的組合不該讓 DI 解析失敗。
        services.AddScoped<IReportToolAccessBatch, ReportToolAccessBatch>();

        services.AddScoped<ISavedQueryService, SavedQueryService>();

        // ── 排程段（§七）──────────────────────────────────────────────────
        services.AddScoped<IReportToolRunner, ReportToolRunner>();
        services.AddScoped<IReportSchedulePermissionChecker, ReportSchedulePermissionChecker>();

        // 檔案儲存只讀設定、無狀態，singleton 即可（也讓根目錄只解析一次）。
        services.AddSingleton<IReportFileStore, ReportFileStore>();

        // 派工器與保留期清理是 BackgroundService，**必須自己開 scope**取 DbContext——
        // 直接注入 scoped 服務會在啟動時就拋 InvalidOperationException。
        //
        // 兩者都掛在 Features:ReportTools:Enabled 之下（預設 false，見 ReportToolsFeature）：
        // 每分鐘輪詢一次的背景服務不該在沒人用排程的部署裡默默跑著。
        // 關閉時改註冊一支只講一句話的通知服務，讓「排程不會執行」這件事看得見。
        if (ReportToolsFeature.IsEnabled(configuration))
        {
            services.AddHostedService<ReportDispatcherService>();
            services.AddHostedService<ReportRetentionJobService>();
        }
        else
        {
            services.AddHostedService<ReportSchedulingDisabledNotice>();
        }

        services.Configure<RazorViewEngineOptions>(opt =>
        {
            opt.ViewLocationFormats.Add("/Modules/RPS/Views/{1}/{0}" + RazorViewEngine.ViewExtension);

            // Shared 這一條是必要的，不是對稱美感：_SavedQueries 是由 ORD／INV 的報表畫面
            // 引用的共用 partial，解析時 {1} 會是**呼叫端的 controller**（SalesRanking…），
            // 只有上面那條的話會去找 /Modules/RPS/Views/SalesRanking/_SavedQueries.cshtml。
            // 缺了它是執行期才炸的錯（建置照樣過），且只有真的打開報表畫面才看得到。
            opt.ViewLocationFormats.Add("/Modules/RPS/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
        });
    }

    public void Configure(WebApplication app) { }
}
