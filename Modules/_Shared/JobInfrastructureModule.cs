using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Services;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.Shared
{
    /// <summary>
    /// 每日排程共用基礎設施的註冊。
    ///
    /// <para>目前只有 <see cref="IJobRunRecorder"/>：每日排程的執行紀錄，讓
    /// <c>DailyJobServiceBase</c> 的衍生類別能判斷「今天跑過了沒」。</para>
    ///
    /// <para><b>為什麼放在 _Shared 而不是某個業務模組</b>：使用者跨模組——現在是 MFG 的
    /// ETA 校準與生產準備提醒，日後 ACT 的稽核與清理、RPS 的報表保留都可以只加一行覆寫
    /// 就接上。掛在 MFGModule 會讓 ACT 的排程隱性依賴 MFG 有沒有被載入，那種相依
    /// 在模組被停用時才會炸，而且症狀是 DI 解析失敗、離真因很遠。</para>
    ///
    /// <para><b>為什麼不寫在 Program.cs</b>：零衝突模組架構要求擴充一律走 <c>IAppModule</c>
    /// （同 <see cref="ManualDocsCacheModule"/>）。</para>
    /// </summary>
    public class JobInfrastructureModule : IAppModule
    {
        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IJobRunRecorder, JobRunRecorder>();
        }

        public void Configure(WebApplication app) { }
    }
}
