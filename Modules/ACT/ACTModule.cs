using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Modules.ACT.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ACT
{
    /// <summary>ACT（結帳 / IFRS）模組（Phase C）。</summary>
    public class ACTModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IDataAuditService, DataAuditService>();
            services.AddScoped<IDerivedFieldRepairService, DerivedFieldRepairService>(); // 資料自癒：累計欄位重算（人工核可）
            // 稽核描述的在地化：產生端存鍵＋參數，顯示端才組句（見 AuditMessages）。
            services.AddScoped<IAuditMessageFormatter, AuditMessageFormatter>();
            services.AddScoped<IDataAuditRunService, DataAuditRunService>();
            services.AddHostedService<Services.Jobs.DailyDataAuditJobService>();
            services.AddHostedService<Services.Jobs.AuditLogRetentionJobService>(); // M5：AuditLog 保留期清理
            services.AddHostedService<Services.Jobs.PasswordHistoryRetentionJobService>(); // L4：PasswordHistory 每人保留最近 K 筆
            services.AddHostedService<Services.Jobs.ApprovalHistoryRetentionJobService>(); // 簽核歷程 730 天（刪 Request，Log 由 CASCADE 帶走）
            services.AddHostedService<Services.Jobs.MrpRunRetentionJobService>(); // MRP 展開單 180 天（僅刪無下游引用者）
            services.AddScoped<IAccountingPeriodService, AccountingPeriodService>();
            services.AddScoped<IJournalEntryService, JournalEntryService>();
            services.AddScoped<INrvAssessmentService, NrvAssessmentService>();
            services.AddScoped<IFxRevaluationService, FxRevaluationService>();
            services.AddScoped<IEclService, EclService>();
            services.AddScoped<IMonthEndService, MonthEndService>();
            services.AddScoped<IPeriodEndAdjustmentService, PeriodEndAdjustmentService>();
            // 報表工具（§5.3）：畫面／排程／常用查詢共用同一份查詢邏輯。
            services.AddScoped<Reporting.TrialBalanceTool>();
            services.AddScoped<SmartFactory.MES.Modules.RPS.Contracts.IReportToolDescriptor>(
                sp => sp.GetRequiredService<Reporting.TrialBalanceTool>());


            services.Configure<RazorViewEngineOptions>(opt =>
            {
                opt.ViewLocationFormats.Add("/Modules/ACT/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                opt.ViewLocationFormats.Add("/Modules/ACT/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}
