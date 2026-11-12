using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV
{
    /// <summary>
    /// INV（庫存）模組的 IAppModule 實作。
    /// Core 的 Program.cs 會自動掃描並載入，無需手動修改 Program.cs。
    /// </summary>
    public class INVModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IInventoryService, InventoryService>();
            services.AddScoped<IInventoryCostLayerService, InventoryCostLayerService>();
            services.AddScoped<IInventoryPostingService, InventoryPostingService>();
            services.AddScoped<IContainerPackService, ContainerPackService>();
            services.AddScoped<IInventoryReservationService, InventoryReservationService>();
            services.AddScoped<IMaterialStagingService, MaterialStagingService>();
            services.AddScoped<ILandedCostService, LandedCostService>();

            // 簽核中央同步擴充點（v1.2+）
            services.AddScoped<IApprovalSyncContributor, StockTransferApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, CrossPlantTransferApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, ScrapDocApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, LoanDocApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, SampleDocApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, ReworkMaterialDocApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, OutsourceMaterialDocApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, StockAdjustmentApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, MaterialStagingOrderApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, LandedCostAllocationApprovalSync>();

            // Phase 0-B 報表工具（§5.3）：畫面／排程／未來的 LLM 共用同一份查詢邏輯。
            // 同時以 IReportToolDescriptor 登錄，供 ReportToolCatalog 列舉。
            services.AddScoped<Reporting.InventoryValuationTool>();
            services.AddScoped<Reporting.StockShortageTool>();
            services.AddScoped<Reporting.SlowMovingInventoryTool>();
            services.AddScoped<RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.InventoryValuationTool>());
            services.AddScoped<RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.StockShortageTool>());
            services.AddScoped<RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.SlowMovingInventoryTool>());

            // INV 模組 Razor View 路徑
            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/INV/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/INV/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app)
        {
            // INV 模組目前無需額外 Middleware 設定
        }
    }
}
