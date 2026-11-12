using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services.Jobs;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 途程工序等候時間建議值統計（mfg-domain.md §23.2-2），每晚 02:00 執行。
    /// 計算交給 <see cref="IQueueTimeSuggestionService"/>：結果只寫入建議值表，<b>不改途程主檔</b>——
    /// 途程編輯畫面顯示建議與「套用」，由工藝人員確認（規格定案）。
    ///
    /// <para><b>2026-10-06 改寫</b>：原本直接回寫 <c>RoutingOperation.QueueTime</c>，但查詢受全域過濾影響
    /// 在背景撈到 0 筆、從未生效；計算本身另有四個錯誤（見服務註解）。取樣改為近 90 天。</para>
    ///
    /// <para><b>重複執行是安全的</b>：每次整批覆蓋建議值，結果只取決於當時的資料。</para>
    /// </summary>
    public class QueueTimeBackfillService : DailyJobServiceBase
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<QueueTimeBackfillService> _logger;
        private readonly IConfiguration _configuration;

        public QueueTimeBackfillService(
            IServiceProvider serviceProvider,
            ILogger<QueueTimeBackfillService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
        }

        protected override int RunAtHour => 2;
        protected override string JobName => "QueueTime 統計回填";
        protected override ILogger JobLogger => _logger;
        protected override IConfiguration JobConfiguration => _configuration;

        protected override async Task ExecuteJobAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IQueueTimeSuggestionService>();

            _logger.LogInformation("開始途程等候時間建議值統計");
            var count = await svc.RecalculateAsync(DateTime.UtcNow, ct);
            _logger.LogInformation("途程等候時間建議值統計完成，{Count} 道工序有建議值", count);
        }
    }
}
