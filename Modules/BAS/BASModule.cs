using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Filters;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS
{
    /// <summary>
    /// BAS（基礎資料）模組的 IAppModule 實作。
    /// Core 的 Program.cs 會自動掃描並載入，無需手動修改 Program.cs。
    /// </summary>
    public class BASModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            // BAS 模組選單
            services.AddScoped<IMenuContributor, ProjectZMenuContributor>();
            services.AddScoped<IApprovalDocumentTypeLabelContributor, SmartFactory.MES.Modules._Shared.ApprovalDocumentTypeLabelContributor>();

            // BAS 模組服務
            services.AddScoped<ICurrencyService, CurrencyService>();
            services.AddScoped<IDocNumberService, DocNumberService>();
            services.AddScoped<IUomService, UomService>();
            services.AddScoped<IContainerUnitService, ContainerUnitService>();

            // 畫面顯示用的小數位數（View 直接 @inject，不必每支 controller 補 ViewBag）
            services.AddScoped<IDisplayPrecision, DisplayPrecision>();
            // 列印單代碼配名稱（品號→品名、廠商、單位、幣別…），由各 Print.cshtml 注入
            services.AddScoped<SmartFactory.MES.Modules._Shared.Printing.PrintNameService>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.Printing.PrintSignatureService>();
            // 科目性質查表：財報用它決定分錄落在哪一段（原本各自讀科目代碼首碼）。
            services.AddScoped<IAccountClassifier, AccountClassifier>();

            // 倉庫／儲位預設值的廠別收斂（全域系統參數存的代碼只屬於某一廠，
            // 在別廠必須退回「未設定」而非靜默指向他廠倉）
            services.AddScoped<IPlantDefaultLocationResolver, PlantDefaultLocationResolver>();

            // 廠別參數解析（本廠覆寫 > 全域值 > 內建預設；plant-scoped-system-params.md §3）
            services.AddScoped<IPlantParamService, PlantParamService>();

            // 覆蓋 Core 預設的 CodeEntry 引用檢查（加入 BAS 模組引用）
            services.AddScoped<ICodeEntryReferenceChecker, BASCodeEntryReferenceChecker>();

            // 主檔簽核回寫（缺此註冊則核准後主檔停在 PendingApproval 且 IsActive=false；R15 守門）
            services.AddScoped<IApprovalSyncContributor, ProductApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, CompanyApprovalSync>();
            services.AddScoped<IApprovalSyncContributor, TaxApprovalSync>();

            // 簽核事件推播（Core IApprovalNotifier 擴充點 v1.3+；未註冊時 Core 以空實作略過）
            services.AddScoped<IApprovalNotifier, _Shared.PushApprovalNotifier>();

            // 驗證訊息在地化（DataAnnotation/ModelBinding/隱含必填 + 欄位顯示名）
            // 已上移至 Core 的 Program.cs（builder.Services.AddLocalizedValidation()，
            // 見 Services/LocalizedValidationExtensions.cs）——所有部署專案共用，
            // 此模組不再重複註冊（避免 ModelMetadataDetailsProviders 雙掛）。
            //
            // 但 Core 的查找鏈預設只有 SharedResource；顧問模組欄位（SlipNo/WipMaterial/
            // ActualOverheadCost…）的中英欄位名放在 ProjectZValidationResource，必須在此
            // 登記為 fallback，驗證訊息的 {0} 才不會退回英文屬性名。
            // 這是本專案唯一的登記點（全站共用、非 BAS 專屬，與同在本檔註冊的
            // ProjectZMenuContributor 同一性質）。
            services.AddValidationResourceFallback<SmartFactory.MES.ProjectZValidationResource>();

            // SystemParam 強化 Filter（攔截 SystemParamController，注入幣別資料）
            services.AddScoped<SystemParamEnhancementFilter>();
            // Plant 強化 Filter（攔截 PlantController，把該廠的參數覆寫掛進廠別維護面板）
            services.AddScoped<PlantFormEnhancementFilter>();
            // 全站唯一鍵衝突（23505）轉譯 Filter：把 PK/Unique Index 衝突包成使用者看得懂的訊息
            services.AddScoped<DbUniqueViolationExceptionFilter>();
            services.Configure<MvcOptions>(options =>
            {
                options.Filters.AddService<SystemParamEnhancementFilter>();
                options.Filters.AddService<PlantFormEnhancementFilter>();
                options.Filters.AddService<DbUniqueViolationExceptionFilter>();
            });

            // BAS 模組 Razor View 路徑
            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/BAS/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/BAS/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app)
        {
            // BAS 模組目前無需額外 Middleware 設定
        }
    }
}
