using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.MFG.Reporting;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.RPS
{
    /// <summary>
    /// 期間界線比對 <c>timestamp with time zone</c> 欄位的報表工具，在**真的 PostgreSQL** 上跑得起來。
    ///
    /// <para>2026-10-07 人工驗收 TZ-3：P2（America/New_York）排程「昨天」的不良率報表按「立即執行」，
    /// 執行紀錄是 <c>Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'</c>。
    /// 工具用 <c>DateOnly.ToDateTime(TimeOnly.MinValue)</c> 當界線，單元測試是 InMemory，Kind 不檢查，
    /// 所以這個錯只有走 Npgsql 才看得到——這也是本檔放在整合測試的唯一理由。</para>
    ///
    /// <para>走 <see cref="IReportToolRunner"/>（派工器用的同一個入口），不直接 new 工具。
    /// 不種資料：例外發生在參數寫入，零筆就足以證明查詢送得出去。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ReportToolTimestamptzBoundaryIntegrationTests
    {
        private const string PlantId = "ZZTSTZ";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly AuthedScheduleFactory _factory;

        public ReportToolTimestamptzBoundaryIntegrationTests(AuthedScheduleFactory factory) => _factory = factory;

        public static TheoryData<string, string> Cases => new()
        {
            { "DefectRate",
              JsonSerializer.Serialize(new DefectRateRequest { PlantId = PlantId, Period = RelativePeriod.Yesterday }, Json) },
            { "WorkOrderProgress",
              JsonSerializer.Serialize(new WorkOrderProgressRequest { PlantId = PlantId, Period = RelativePeriod.Yesterday }, Json) },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task 相對期間昨天_紐約時區_查詢送得出去(string toolKey, string paramsJson)
        {
            using var scope = _factory.Services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IReportToolRunner>();

            var result = await runner.RunAsync(toolKey, paramsJson, user: null, "America/New_York");

            Assert.NotNull(result);
        }
    }
}
