using Microsoft.AspNetCore.Mvc.Razor;

namespace SmartFactory.MES.Modules.ORD
{
    /// <summary>
    /// ORD（訂單管理）模組。
    /// 涵蓋：銷售訂單(SO)、出貨單(GS)、銷退單(GR)、內部訂單(NO/NR)。
    /// </summary>
    public class ORDModule : SmartFactory.MES.Services.IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            // 簽核中央同步擴充點（v1.2+）
            // 手機簽核摘要（IApprovalSummaryProvider）。沒有 provider 的單別在手機上
            // 會顯示「請用電腦簽核」而不是一顆空白核准鈕，見該介面註解。
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.SalesOrderApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.GoodsShipmentApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.SalesReturnApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.InternalOrderApprovalSummary>();

            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.SalesOrderApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.SalesReturnApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.GoodsShipmentApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.InternalOrderApprovalSync>();

            // Phase 0-B 報表工具（§5.3）：畫面／排程／未來的 LLM 共用同一份查詢邏輯。
            // 同時以 IReportToolDescriptor 登錄，供 ReportToolCatalog 列舉。
            services.AddScoped<Reporting.SalesRankingTool>();
            services.AddScoped<Reporting.OrderOverdueTool>();
            services.AddScoped<Reporting.ReturnRateTool>();
            services.AddScoped<Reporting.DeliveryRateTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.SalesRankingTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.OrderOverdueTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.ReturnRateTool>());
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.DeliveryRateTool>());

            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/ORD/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/ORD/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}
