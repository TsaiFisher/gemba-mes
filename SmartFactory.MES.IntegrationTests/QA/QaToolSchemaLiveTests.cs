using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Modules.INV.Reporting;
using SmartFactory.MES.Modules.ORD.Reporting;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.QA;

/// <summary>
/// 用**程式產生的** schema 對真實推論服務驗證工具選取（§11.1）。
///
/// <para>單元測試守的是 schema 的形狀（camelCase、enum、排除清單），
/// 但形狀對不代表**線上格式**對——`tools` 陣列的巢狀結構、`enum` 的序列化方式
/// 是否符合 Ollama 的期望，只有真的送一次才知道。這一支補的就是那段。</para>
///
/// <para>刻意不走 <c>WebApplicationFactory</c>：這裡不需要資料庫，
/// 只需要 <c>OllamaClient</c> 與六支工具的 <c>RequestType</c>。
/// 測試越自足，日後越不會因為別的原因壞掉。</para>
/// </summary>
public class QaToolSchemaLiveTests
{
    private readonly ITestOutputHelper _out;

    public QaToolSchemaLiveTests(ITestOutputHelper output) => _out = output;

    private sealed class Factory : IHttpClientFactory
    {
        private readonly Uri _baseUrl;
        private readonly TimeSpan _timeout;
        public Factory(Uri baseUrl, TimeSpan timeout) { _baseUrl = baseUrl; _timeout = timeout; }
        public HttpClient CreateClient(string name) => new() { BaseAddress = _baseUrl, Timeout = _timeout };
    }

    private sealed class Tool : IReportToolDescriptor
    {
        public Tool(string key, string description, Type requestType)
        {
            ToolKey = key; Description = description; RequestType = requestType;
        }
        public string ToolKey { get; }
        public string Description { get; }
        public ReportToolPermission Permission => ReportToolPermission.CanRead;

        /// <summary>
        /// 本檔量的是 schema 品質與模型的選取／填參能力，不是產品政策，
        /// 所以六支一律當作可進目錄（見 <see cref="SetupAsync"/> 對機密閘門的處理）。
        /// </summary>
        public bool ContainsMonetaryValues => false;
        public bool RequiresPeriod => false;          // 本測試不涉及期間解算

        public Type RequestType { get; }

        // 本檔量的是 schema 與模型填參，不走匯出路徑；契約要求每支工具自己回答
        // 「資料列在哪」，故給空集合。
        public Task<IReadOnlyList<object>> GetExportRowsAsync(
            ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<object>>([]);

        // 假工具不匯出，欄位清單為空即可——本批測試看的是目錄、權限與 schema。
        public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) => [];
        public Type ResponseType => typeof(object);
    }

    /// <summary>六支真實工具的鍵、描述與 Request 型別（描述與各 Tool 類別內的字串一致）。</summary>
    private static readonly Tool[] RealTools =
    [
        new("SalesRanking", "依客戶或品項彙總指定期間的銷售金額、訂單數與明細筆數，依金額降冪排名。不含草稿單。",
            typeof(SalesRankingRequest)),
        new("ReturnRate", "以（品號, 客戶）配對統計指定期間的出貨量與退貨量並計算退貨率，只計已過帳單據。",
            typeof(ReturnRateRequest)),
        new("OrderOverdue", "列出預交日已過但仍有未出量的銷售訂單明細，含逾期天數，依逾期天數降冪。不含草稿單。",
            typeof(OrderOverdueRequest)),
        new("InventoryValuation", "以品號×儲位×批號列出在庫量、均價與評估金額，並提供全查詢小計。",
            typeof(InventoryValuationRequest)),
        new("SlowMovingInventory", "列出最後異動日距今超過門檻天數的庫存（品號×儲位×批號），含庫存金額，依金額降冪。",
            typeof(SlowMovingInventoryRequest)),
        new("StockShortage", "列出可用量低於安全存量的品號與缺口量，依缺口降冪。可用量為該品號跨所有儲位的在手量減預留量。",
            typeof(StockShortageRequest)),
    ];

    /// <summary>全部授權的替身。本專案沒有 Moq，手寫一個反而更短。</summary>
    private sealed class AllAuthorized : IReportToolAccessBatch
    {
        public Task<IReadOnlyList<ReportToolAccess>> GetAccessAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReportToolAccess>>(
                [.. RealTools.Select(t => new ReportToolAccess(t, true))]);
    }

    private static async Task<(IReadOnlyList<QaToolDefinition> tools, ILlmToolClient client)> SetupAsync()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        // ⚠️ 這裡拿到的是**六支全開**的目錄，而正式預設只有三支：
        // §十三 Phase 2 的機密閘門（`Features:QaAssistant:AllowMonetaryTools`，預設 false）
        // 會擋掉 SalesRanking／InventoryValuation／SlowMovingInventory。
        // 本檔刻意繞過它——這裡量的是「schema 好不好用、模型選不選得對」，
        // 六支才是這個問題的完整母體，三支量不出「會不會誤選隔壁那一支」。
        // 閘門本身由 QaToolSchemaBuilderTests 守，不在這裡重複。
        var tools = await new QaToolSchemaBuilder(new AllAuthorized(), config).BuildAsync();
        var client = new OllamaClient(
            new Factory(OllamaClient.ResolveBaseUrl(config), OllamaClient.ResolveTimeout(config)),
            config, NullLogger<OllamaClient>.Instance);

        return (tools, client);
    }

    private const string SystemPrompt =
        "你是製造管理系統的查詢助理。請依使用者的問題選擇最合適的一個查詢工具並填入參數。" +
        "沒有任何工具能回答時，直接說明沒有對應的查詢工具，不要猜測或自行推算數字。";

    [LiveOllamaFact]
    public async Task 程式產生的schema_模型選得對也填得對()
    {
        var (tools, client) = await SetupAsync();
        _out.WriteLine($"工具數：{tools.Count}");

        // ⚠️ 斷言只放**問題裡明確出現、且對答案正確性必要**的參數。
        // 2026-08-21 實測：同一題連跑兩次，第一次填了 topN:10、第二次沒填——
        // temperature 0 也不保證確定性。把 topN 放進斷言會讓這支測試變成擲骰子，
        // 而它要守的是「schema 的線上格式可用、工具選得對」，不是模型的逐次穩定度。
        // 缺 topN 的影響也有限：工具本身已保證依金額降冪，取前 N 筆可由畫面分頁完成。
        var cases = new (string question, string expectedTool, string[] expectedInArgs)[]
        {
            ("上個月哪個客戶買最多？取前五名", "SalesRanking", ["LastMonth"]),
            ("庫存超過90天都沒有異動的品項有哪些？金額最大的前10筆", "SlowMovingInventory", ["90"]),
            ("有哪些品號的可用量低於安全存量？", "StockShortage", []),
        };

        var failures = new List<string>();

        foreach (var (question, expectedTool, expectedInArgs) in cases)
        {
            var answer = await client.ChatWithToolsAsync(SystemPrompt, question, tools);

            _out.WriteLine($"\nQ: {question}");
            _out.WriteLine($"   prefill={answer.PromptEvalCount} tokens, 牆鐘={answer.Duration.TotalSeconds:F1}s");

            if (answer.ToolCalls.Count == 0)
            {
                failures.Add($"「{question}」沒有選任何工具，content={answer.Content}");
                continue;
            }

            var call = answer.ToolCalls[0];
            _out.WriteLine($"   → {call.ToolKey} {call.ArgumentsJson}");

            if (call.ToolKey != expectedTool)
                failures.Add($"「{question}」選了 {call.ToolKey}，預期 {expectedTool}");

            foreach (var token in expectedInArgs.Where(t => !call.ArgumentsJson.Contains(t)))
                failures.Add($"「{question}」的參數缺少 {token}：{call.ArgumentsJson}");

            // 不當失敗，但一定要印出來：模型會自行填入問題中沒提到的參數
            // （實測填過 period:"ThisYear"、dateBasis:"OrderDate"）。
            // dateBasis 已因此移入排除清單；period 不能排除（它是核心語意），
            // 只能靠把參數回顯給使用者確認來緩解（§9.2-3）。這一行是那個決策的持續證據。
            if (call.ArgumentsJson.Contains("period") && !question.Contains("月") && !question.Contains("年"))
                _out.WriteLine($"   ⚠ 問題未提期間，模型自行填了 period：{call.ArgumentsJson}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [LiveOllamaFact]
    public async Task 目錄外提問_不得硬選一支工具()
    {
        var (tools, client) = await SetupAsync();

        var answer = await client.ChatWithToolsAsync(SystemPrompt, "幫我查每個員工這個月的薪資明細", tools);

        _out.WriteLine($"tool_calls={answer.ToolCalls.Count}, content={answer.Content}");

        // §9.2：沒有對應工具就要說沒有，不得硬套一支「看起來最接近」的。
        Assert.Empty(answer.ToolCalls);
        Assert.False(string.IsNullOrWhiteSpace(answer.Content));
    }
}
