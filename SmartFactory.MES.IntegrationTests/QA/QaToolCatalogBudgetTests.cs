using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// §11.1 重評門檻的守門與量測。
    ///
    /// <para><b>規格訂的是兩條硬線</b>：工具數 &gt; 20，<b>或</b>全目錄 schema 的 prefill
    /// &gt; 4k token，兩者任一成立就要回頭做「順位 3：embedding 檢索工具」。
    /// 那兩條線原本只寫在文件裡，靠人記得回來量——本檔把它變成會自己響的東西。</para>
    ///
    /// <para><b>為什麼不沿用 <c>QaToolSchemaLiveTests</c> 的六支假工具</b>：那一檔量的是
    /// 「schema 好不好用、模型選不選得對」，六支寫死在檔案裡正好當固定母體。
    /// 本檔要回答的是「**目前真的註冊了幾支、送出去有多大**」，
    /// 寫死的清單答不了這個問題——新工具進來時它不會變。所以這裡從 DI 取真實目錄。</para>
    ///
    /// <para>沿用既有 collection，理由見 <c>CollectionLayoutGuardTests</c>。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QaToolCatalogBudgetTests
    {
        /// <summary>§11.1：工具數超過這個值就要回頭做順位 3。</summary>
        private const int ToolCountThreshold = 20;

        /// <summary>§11.1：全目錄 schema 的 prefill 超過這個 token 數，同樣要回頭做順位 3。</summary>
        private const int PrefillTokenThreshold = 4000;

        private readonly AuthedScheduleFactory _factory;
        private readonly ITestOutputHelper _out;

        public QaToolCatalogBudgetTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _out = output;
        }

        /// <summary>全部授權的替身：本檔量的是目錄規模，不是誰看得到哪一支。</summary>
        private sealed class AllAuthorized(IReadOnlyList<IReportToolDescriptor> tools) : IReportToolAccessBatch
        {
            public Task<IReadOnlyList<ReportToolAccess>> GetAccessAsync(CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<ReportToolAccess>>(
                    [.. tools.Select(t => new ReportToolAccess(t, true))]);
        }

        /// <summary>
        /// 取 DI 裡**真實註冊**的那一批工具。各工具是 scoped（它們注入 <c>ApplicationDbContext</c>），
        /// 所以必須在 scope 內解析。
        /// </summary>
        private IReadOnlyList<IReportToolDescriptor> RealTools()
        {
            using var scope = _factory.Services.CreateScope();
            return [.. scope.ServiceProvider.GetServices<IReportToolDescriptor>()];
        }

        private async Task<IReadOnlyList<QaToolDefinition>> BuildAsync(bool allowMonetary)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [QaToolSchemaBuilder.AllowMonetaryKey] = allowMonetary ? "true" : "false",
                })
                .Build();

            return await new QaToolSchemaBuilder(new AllAuthorized(RealTools()), config).BuildAsync();
        }

        /// <summary>
        /// schema 的文字負載量。**不是 token 數**——是與 token 數同向的可量指標，
        /// 用來在沒有推論服務時也看得出目錄膨脹。真正的 token 數由下面那支 live 測試量。
        /// </summary>
        private static int PayloadChars(IReadOnlyList<QaToolDefinition> tools) =>
            JsonSerializer.Serialize(tools).Length;

        [Fact]
        public async Task 工具目錄沒有越過第十一章一節的重評門檻()
        {
            var effective = await BuildAsync(allowMonetary: false);
            var worstCase = await BuildAsync(allowMonetary: true);

            _out.WriteLine($"DI 註冊的工具總數：{RealTools().Count}");
            _out.WriteLine($"目前送給模型的（AllowMonetaryTools=false）：{effective.Count} 支，"
                         + $"schema {PayloadChars(effective)} 字元");
            _out.WriteLine($"開啟含金額工具後的最壞情況：{worstCase.Count} 支，"
                         + $"schema {PayloadChars(worstCase)} 字元");

            // 門檻比對的是**實際送出去**的那一份。開關打開之後的最壞情況只印不擋——
            // 那是組態決策，而 appsettings.json 的 _Note_AllowMonetaryTools 已經寫明
            // 打開會讓目錄從幾支變成幾支。
            Assert.True(effective.Count <= ToolCountThreshold,
                $"NL 工具目錄已有 {effective.Count} 支，超過 §11.1 的重評門檻 {ToolCountThreshold} 支。"
                + "請回頭評估「順位 3：embedding 檢索工具」，不要直接調高這個常數——"
                + "門檻是規格訂的，改它等於把重評這件事取消掉。");
        }

        /// <summary>
        /// 把推估換成實測。§11.1 原本的 320 token／支是 2026-08-21 以**六支** INV／ORD 工具
        /// 量出來的，後來進來的那批描述更長——外推那個比率答不了「越不越線」。
        /// 這一支量到的兩個數字（8 支 1951／17 支 3799）已經寫回 §11.1 取代該推估。
        ///
        /// <para>這一支送一則最短的提問，只為了拿 <c>PromptEvalCount</c>。
        /// 沒有推論服務時整支跳過（<c>LiveOllamaFact</c>），所以它不是 CI 的守門，
        /// 是「重評時不必再用比率外推」的那份實測數據。</para>
        /// </summary>
        [LiveOllamaFact]
        public async Task 實測全目錄schema的prefill並釘住四千token那條線()
        {
            var tools = await BuildAsync(allowMonetary: false);

            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            using var http = new HttpClient
            {
                BaseAddress = OllamaClient.ResolveBaseUrl(config),
                Timeout = OllamaClient.ResolveTimeout(config),
            };
            var client = new OllamaClient(
                new SingleClientFactory(http), config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaClient>.Instance);

            var worstCase = await BuildAsync(allowMonetary: true);

            // ⚠️ **兩份都真的送一次，不外推。**2026-09-17 第一版曾按「每支 token 數」外推，
            // 得到 17 支約 4146；改按字元數外推卻得到約 3688——一個越線一個沒有，
            // 而這兩個數字要回答的正是「打開開關會不會跨過門檻」。
            // 差異的來源是**含金額那九支的描述平均比較短**（最壞情況 973 字元／支，
            // 目前這八支是 1094），所以按支數推會系統性高估。
            // 決定越線與否的數字不該由挑哪一種外推法決定，多送一次請求就問得到答案。
            var effectiveAnswer = await Measure(client, tools);
            var worstAnswer     = await Measure(client, worstCase);

            _out.WriteLine($"目前送出（{tools.Count} 支）        → prefill {effectiveAnswer} tokens"
                         + $"（每支約 {(double)effectiveAnswer / tools.Count:F0}；§11.1 記的舊值是 320）");
            _out.WriteLine($"開啟含金額工具（{worstCase.Count} 支）→ prefill {worstAnswer} tokens"
                         + $"（門檻 {PrefillTokenThreshold}）");

            Assert.True(effectiveAnswer <= PrefillTokenThreshold,
                $"全目錄 schema 的 prefill 已達 {effectiveAnswer} tokens，"
                + $"超過 §11.1 的重評門檻 {PrefillTokenThreshold}。請回頭評估「順位 3：embedding 檢索工具」。");
        }

        /// <summary>送一則**最短**的提問，只為了拿 prefill——要量的是 schema 佔多少，不是提問佔多少。</summary>
        private static async Task<int> Measure(ILlmToolClient client, IReadOnlyList<QaToolDefinition> tools)
            => (await client.ChatWithToolsAsync("你是查詢助理。", "缺料", tools)).PromptEvalCount;

        private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }
    }
}
