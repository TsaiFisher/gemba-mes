using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Modules.QCR.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR
{
    /// <summary>
    /// QCR（品質管理）模組。
    /// 涵蓋：檢驗規範(InspectionSpec)、不良項目(DefectCode)、檢驗儀器(Instrument)。
    /// 對應舊系統 PZ/QCR/ 模組（QNO01/QNI01/QIN01/QIT01）。
    /// </summary>
    public class QCRModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IQcrReturnService, QcrReturnService>();
            services.AddScoped<IIqcInspectionService, IqcInspectionService>();
            services.AddScoped<ISpcAnalysisService, SpcAnalysisService>();

            // NCR 簽核回寫（缺此註冊則核准後 NCR 永遠停在 PendingApproval；R15 守門）
            services.AddScoped<IApprovalSyncContributor, NcrApprovalSync>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.NcrApprovalSummary>();

            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/QCR/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/QCR/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}
