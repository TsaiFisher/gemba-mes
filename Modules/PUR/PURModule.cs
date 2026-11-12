using Microsoft.AspNetCore.Mvc.Razor;

namespace SmartFactory.MES.Modules.PUR
{
    /// <summary>
    /// PUR（採購）模組的 IAppModule 實作。
    /// Core 的 Program.cs 會自動掃描並載入，無需手動修改 Program.cs。
    /// </summary>
    public class PURModule : SmartFactory.MES.Services.IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            // PUR 模組服務
            services.AddScoped<SmartFactory.MES.Services.IPurchaseService, SmartFactory.MES.Services.PurchaseService>();
            services.AddScoped<Services.IOutsourceReceiptSettlementService, Services.OutsourceReceiptSettlementService>();
            services.AddScoped<Services.IOutsourceReceiptService, Services.OutsourceReceiptService>();
            // 在外原料的單一讀取點：委外發料有兩個入口（委外領退料單／發退料單），
            // 下游只認一個的後果見該介面註解（W2-F12）
            services.AddScoped<Services.IOutsourceMaterialFlowReader, Services.OutsourceMaterialFlowReader>();

            // 簽核中央同步擴充點（v1.2+）
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.PurchaseRequestApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.PurchaseOrderApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.GoodsReceiptApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.GoodsReturnApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.VendorEvaluationApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, Services.OutsourceReceiptApprovalSync>();

            // 手機簽核摘要（IApprovalSummaryProvider）。沒有 provider 的單別在手機上
            // 會顯示「請用電腦簽核」而不是一顆空白核准鈕，見該介面註解。
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.PurchaseOrderApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.PurchaseRequestApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.GoodsReceiptApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.GoodsReturnApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.OutsourceReceiptApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.VendorEvaluationApprovalSummary>();

            // Phase G+ SRM：OTD 事實來源（§5.3 報表與 §6 評分卡共用口徑）＋自動分數
            services.AddScoped<Services.IOtdFactService, Services.OtdFactService>();
            services.AddScoped<Services.IVendorEvaluationScoreService, Services.VendorEvaluationScoreService>();

            // Phase 0-B 報表工具（§5.3）：畫面／排程／未來的 LLM 共用同一份查詢邏輯。
            // 同時以 IReportToolDescriptor 登錄，供 ReportToolCatalog 列舉。
            services.AddScoped<Reporting.PurchaseOverdueTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.PurchaseOverdueTool>());

            services.AddScoped<Reporting.OutsourcePriceTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.OutsourcePriceTool>());

            services.AddScoped<Reporting.SupplierDeliveryRateTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.SupplierDeliveryRateTool>());

            services.AddScoped<Reporting.PurchaseAmountTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.PurchaseAmountTool>());

            services.AddScoped<Reporting.PurchasePriceTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.PurchasePriceTool>());

            // PUR 模組 Razor View 路徑
            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/PUR/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/PUR/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app)
        {
            // PUR 模組目前無需額外 Middleware 設定
        }
    }
}
