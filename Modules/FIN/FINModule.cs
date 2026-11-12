using Microsoft.AspNetCore.Mvc.Razor;
using SmartFactory.MES.Modules.FIN.Services;

namespace SmartFactory.MES.Modules.FIN
{
    /// <summary>
    /// FIN（財務）模組的 IAppModule 實作（Phase A）。
    /// 包含 APInvoice / ARInvoice / APPayment / ARReceipt / BankAccount。
    /// </summary>
    public class FINModule : SmartFactory.MES.Services.IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IAPInvoiceService, APInvoiceService>();
            services.AddScoped<IARInvoiceService, ARInvoiceService>();
            // 折讓單（W3-F3 Phase 2）：已結清發票遭退貨時唯一的處理出口。
            services.AddScoped<IARCreditNoteService, ARCreditNoteService>();
            services.AddScoped<IAPDebitNoteService, APDebitNoteService>();
            services.AddScoped<IFinPaymentService, FinPaymentService>();
            services.AddScoped<IErpSyncService, ErpSyncService>();

            // 手動發票的簽核回寫。**必須與 CreateManualAsync 的簽核判斷同批註冊**——
            // 少了 contributor，單據送審之後會靜默停在待審而畫面上看不出異常。
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, APInvoiceApprovalSync>();
            services.AddScoped<SmartFactory.MES.Services.IApprovalSyncContributor, ARInvoiceApprovalSync>();

            // 手機簽核摘要。⚠️ 發票的金額語意與採購／訂單相反（TotalAmount 是交易幣、
            // AmountLocal 才是本幣），所以刻意不共用那組基底，見 InvoiceApprovalSummaries。
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.APInvoiceApprovalSummary>();
            services.AddScoped<SmartFactory.MES.Modules._Shared.IApprovalSummaryProvider, Services.ARInvoiceApprovalSummary>();

            // FIN 模組 Razor View 路徑
            services.Configure<RazorViewEngineOptions>(options =>
            {
                options.ViewLocationFormats.Add(
                    "/Modules/FIN/Views/{1}/{0}" + RazorViewEngine.ViewExtension);
                options.ViewLocationFormats.Add(
                    "/Modules/FIN/Views/Shared/{0}" + RazorViewEngine.ViewExtension);
            });
        }

        public void Configure(WebApplication app) { }
    }
}
