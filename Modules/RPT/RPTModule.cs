using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Modules.RPT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPT
{
    public class RPTModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IProfitLossService,   ProfitLossService>();
            services.AddScoped<IGrossMarginService,  GrossMarginService>();
            services.AddScoped<IBalanceSheetService, BalanceSheetService>();
            services.AddScoped<IDashboardService,    DashboardService>();
            services.AddScoped<IOutsourcePerformanceService, OutsourcePerformanceService>();
            services.AddScoped<IMarginAnalysisService, MarginAnalysisService>();
            services.AddScoped<IContributionMarginService, ContributionMarginService>();
            services.AddScoped<IDataAuditReportService, DataAuditReportService>();

            // 現場遙測：上報（/api/telemetry/events）、儀表板、保留期清理
            services.AddMemoryCache();
            services.AddScoped<IClientTelemetryService, ClientTelemetryService>();
            services.AddScoped<IFieldTelemetryReportService, FieldTelemetryReportService>();
            services.AddHostedService<Services.Jobs.ClientTelemetryRetentionJobService>();

            // 報表工具（RPT 批次 2）。這六支的查詢早就在 service 層，
            // 工具只做期間解算與欄位宣告；同時以 IReportToolDescriptor 登錄供 ReportToolCatalog 列舉。
            services.AddScoped<Reporting.FiscalPeriodMapper>();
            services.AddScoped<Reporting.GrossMarginTool>();
            services.AddScoped<Reporting.MarginAnalysisTool>();
            services.AddScoped<Reporting.OutsourcePerformanceTool>();
            services.AddScoped<Reporting.ContributionMarginTool>();
            services.AddScoped<Reporting.ProfitLossTool>();
            services.AddScoped<Reporting.BalanceSheetTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.GrossMarginTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.MarginAnalysisTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.OutsourcePerformanceTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.ContributionMarginTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.ProfitLossTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.BalanceSheetTool>());

            services.Configure<RazorViewEngineOptions>(opt =>
            {
                opt.ViewLocationFormats.Add("/Modules/RPT/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}
