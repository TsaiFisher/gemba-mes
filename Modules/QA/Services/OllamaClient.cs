using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Modules.QA.Contracts;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>生成（規格 §9、§10.8.1）。</summary>
public interface ILlmClient
{
    /// <summary>
    /// 一次非串流生成。**成功回傳即代表有可用的答案**——空字串與截斷都會拋
    /// <see cref="OllamaException"/>，呼叫端不必再判空。
    /// </summary>
    Task<LlmAnswer> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>嵌入（規格 §10.7）。</summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// 目前組態的嵌入模型名稱。**要跟著向量一起存進 <c>DocChunk.EmbeddingModel</c>**——
    /// 不同模型的向量空間不可比，換模型後不重嵌，相似度會變成雜訊而不是錯誤（§10.7）。
    /// </summary>
    string EmbeddingModelName { get; }

    /// <summary>
    /// 批次嵌入。回傳順序與 <paramref name="inputs"/> 一一對應。
    /// </summary>
    /// <param name="kind">決定前綴。查詢與內容用錯前綴不會報錯，只會讓檢索默默變差（§10.7）。</param>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, EmbeddingKind kind, CancellationToken ct = default);
}

/// <summary>
/// 帶工具的生成（Phase 2、§9.1、§11.1）。
///
/// <para><b>刻意不併進 <see cref="ILlmClient"/>：</b>那支的契約是「成功回傳即代表有可用的答案，
/// 空字串與截斷都會拋」，而帶工具時**空 <c>content</c> ＋ 有 <c>tool_calls</c> 正是成功的樣子**。
/// 沿用同一條檢查會把每一次正常的工具選取都當成失敗。
/// 第四個介面、同一個實作——延續 §17.5 的既有模式，不是新增供應商框架。</para>
/// </summary>
public interface ILlmToolClient
{
    /// <summary>
    /// 送出提問與工具目錄，回傳模型選了哪支工具、填了什麼參數。
    ///
    /// <para><paramref name="tools"/> **必須是已經過 L1 權限過濾的清單**
    /// （<c>IQaToolSchemaBuilder</c>）——模型拒答時會把它看得到的工具列給使用者，
    /// 過濾晚一步，目錄就已經洩漏了（§11.1 實測）。</para>
    /// </summary>
    Task<LlmToolAnswer> ChatWithToolsAsync(
        string systemPrompt, string userPrompt,
        IReadOnlyList<QaToolDefinition> tools, CancellationToken ct = default);
}

/// <summary>
/// 串流生成（§11.2 C4）。
///
/// <para><b>刻意不併進 <see cref="ILlmClient"/>：</b>那支的簽章是 <c>Task&lt;LlmAnswer&gt;</c>，
/// 加一個成員會讓每一個既有的測試替身（實作 <c>ILlmClient</c> 的假物件）當場編不過，
/// 而它們量的是與串流無關的東西。第五個介面、同一個實作——延續 <see cref="ILlmToolClient"/>
/// 的既有模式（§17.5），不是新增供應商框架。</para>
///
/// <para><b>不是所有呼叫端都該改用它。</b>評測、批次、任何「沒有人在螢幕前等」的路徑
/// 用 <see cref="ILlmClient"/> 就好——串流的價值全在體感，而它換來的代價是
/// 「失敗可能發生在已經吐出半段答案之後」。</para>
/// </summary>
public interface ILlmStreamClient
{
    /// <summary>
    /// 逐段生成。序列的形狀見 <see cref="LlmDelta"/>。
    ///
    /// <para><b>限流名額涵蓋整個列舉過程</b>，不是只涵蓋送出——呼叫端必須把列舉跑完
    /// 或把列舉器 dispose 掉，否則名額不會歸還。<c>await foreach</c> 兩者都會做到。</para>
    /// </summary>
    IAsyncEnumerable<LlmDelta> ChatStreamAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>推論服務的健康檢查（§17.8）。</summary>
public interface IOllamaDiagnostics
{
    Task<OllamaHealth> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Ollama 的 HTTP client。
///
/// <para><b>走原生 API（<c>/api/chat</c>、<c>/api/embed</c>），不走 OpenAI 相容層</b>（§17.5 定案 #26）：
/// 相容層沒有 <c>think</c> 參數，而關閉思考是硬性要求——實測開著思考是 528 tokens／43.4 秒，
/// 關掉是 58 tokens／4.7 秒，同一個問題、同樣正確的答案，**9 倍差距**（§10.8.1）。</para>
///
/// <para><b>三個介面刻意分開，但只有一個實作。</b>它們存在的理由是讓單元測試替換掉真實推論，
/// <b>不是</b>為了日後換供應商——不要據此長出一套供應商框架（§17.5）。</para>
///
/// <para><b>Singleton。</b>本身無狀態，組態於建構時讀一次（§17.2 規則 3：不支援熱切換）。
/// 連線一律經 <see cref="IHttpClientFactory"/> 取得，不自行 new HttpClient。</para>
/// </summary>
public class OllamaClient : ILlmClient, ILlmStreamClient, ILlmToolClient, IEmbeddingClient, IOllamaDiagnostics
{
    public const string HttpClientName = "qa-ollama";

    public const string UrlKey = "Features:QaAssistant:OllamaUrl";
    public const string ChatModelKey = "Features:QaAssistant:ChatModel";
    public const string EmbeddingModelKey = "Features:QaAssistant:EmbeddingModel";
    public const string TimeoutSecondsKey = "Features:QaAssistant:TimeoutSeconds";
    public const string NumPredictKey = "Features:QaAssistant:NumPredict";

    public const string DefaultUrl = "http://localhost:11434";

    /// <summary>
    /// 預設逾時。開發機純 CPU 實測 12–14 tok/s，1024 tokens 最壞情況約 80 秒，
    /// 加上首次載入 9.6 GB 模型（實測占牆鐘 20 秒）——<c>HttpClient</c> 預設的 100 秒會**剛好不夠**，
    /// 而症狀是「偶爾逾時」這種最難查的形狀。故放寬到 180 秒。
    /// </summary>
    public const int DefaultTimeoutSeconds = 180;

    /// <summary>
    /// 生成長度上限。**設太小會靜默回空字串**：實測設 300 時生成滿 300 tokens、
    /// <c>done_reason:"length"</c>，而 <c>response</c> 與 <c>thinking</c> 兩個欄位都是空的——
    /// 推理過程吃光額度，答案一個字都沒有也沒有任何錯誤（§10.8.1 第 2 點）。**寧可寬鬆。**
    /// </summary>
    public const int DefaultNumPredict = 1024;

    /// <summary>
    /// 生成溫度。Ollama 預設 0.8，對「只准依據 context 回答」的用途偏高。
    /// **這個值尚未實測**——是 grounded QA 的通則值，不是本專案量出來的；
    /// S3 接上 30 題題庫後才有依據調整（屆時連同 §10.8 的門檻一起量）。
    /// </summary>
    private const double Temperature = 0.2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _factory;
    private readonly IInferenceGate _gate;
    private readonly ILogger<OllamaClient> _log;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly int _timeoutSeconds;
    private readonly int _numPredict;

    /// <summary>
    /// <paramref name="gate"/> 可為 <c>null</c>——**只為既有測試而留**（它們量的是 HTTP 行為，
    /// 不是排隊行為），正式路徑一律由 DI 注入。給 <c>null</c> 等於不限流。
    /// </summary>
    public OllamaClient(IHttpClientFactory factory, IConfiguration configuration, ILogger<OllamaClient> log,
                        IInferenceGate? gate = null)
    {
        _factory = factory;
        _gate = gate ?? NullInferenceGate.Instance;
        _log = log;
        _chatModel = configuration[ChatModelKey]?.Trim() ?? string.Empty;
        _embeddingModel = configuration[EmbeddingModelKey]?.Trim() ?? string.Empty;
        _timeoutSeconds = (int)ResolveTimeout(configuration).TotalSeconds;
        _numPredict = Positive(configuration.GetValue<int?>(NumPredictKey), DefaultNumPredict);
    }

    public string EmbeddingModelName => _embeddingModel;

    /// <summary>
    /// 組態的基底位址。空值或填壞時退回 <see cref="DefaultUrl"/> 並出聲——
    /// 這裡不 fail fast，因為 S1（純詞彙檢索）不需要 Ollama，
    /// 為了一個沒填的推論位址讓整個 ERP 開不起來是不成比例的（對照 §17.2 規則 2 的 StoragePath，
    /// 那個是「開了功能卻一定會壞」，這個不是）。
    /// </summary>
    public static Uri ResolveBaseUrl(IConfiguration configuration, ILogger? log = null)
    {
        var raw = configuration[UrlKey];
        if (string.IsNullOrWhiteSpace(raw)) return new Uri(DefaultUrl);

        if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return uri;

        log?.LogWarning("{Key} 的值 '{Value}' 不是合法的絕對網址，改用預設 {Default}。", UrlKey, raw, DefaultUrl);
        return new Uri(DefaultUrl);
    }

    /// <summary>
    /// 逾時。**註冊具名 client 與本類別的錯誤訊息必須用同一個值**——
    /// 兩邊各讀各的，會出現「實際 100 秒斷線，訊息說 180 秒」這種對不起來的診斷。
    /// </summary>
    public static TimeSpan ResolveTimeout(IConfiguration configuration) =>
        TimeSpan.FromSeconds(Positive(configuration.GetValue<int?>(TimeoutSecondsKey), DefaultTimeoutSeconds));

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    // ── 生成 ────────────────────────────────────────────────────────────────

    public async Task<LlmAnswer> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (_chatModel.Length == 0)
            throw new OllamaException($"未設定生成模型。請在組態填入 {ChatModelKey}（例如 gemma4），並確認該模型已 ollama pull。");

        var request = new ChatRequest
        {
            Model = _chatModel,
            Stream = false,

            // ① 不是效能微調，是可用性的前提（§10.8.1 第 1 點）。
            //    對思考型模型不關掉，使用者要等 44 秒而不是 5 秒。
            Think = false,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            },
            Options = new GenerationOptions { NumPredict = _numPredict, Temperature = Temperature },
        };

        var response = await PostAsync<ChatRequest, ChatResponse>(
            "/api/chat", request, InferenceLane.Interactive, ct);

        // ② 截斷＝失敗。HTTP 200、done:true、token 也真的生成了，只有內容是空的——
        //    這是靜默失敗的教科書樣態，沒有這道檢查，症狀會是「有時候問了沒反應」（§10.8.1 第 3 點）。
        if (string.Equals(response.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new OllamaException(
                $"生成被長度上限截斷（已生成 {response.EvalCount} tokens，{NumPredictKey}={_numPredict}）。" +
                $"思考型模型的推理過程會吃掉額度，導致答案為空。請調高 {NumPredictKey}。");

        var text = response.Message?.Content ?? string.Empty;

        // ③ 空字串一律視為失敗，絕不送到畫面上。
        if (string.IsNullOrWhiteSpace(text))
            throw new OllamaException(
                $"模型 {response.Model ?? _chatModel} 回覆了空內容（done_reason={response.DoneReason ?? "未提供"}）。" +
                "已生成的 token 全部進了思考過程時會出現這個結果，請確認呼叫端有送出 think:false。");

        return new LlmAnswer(
            text.Trim(),
            response.Model ?? _chatModel,
            response.DoneReason ?? string.Empty,
            response.PromptEvalCount,
            response.EvalCount,
            TimeSpan.FromTicks(response.TotalDuration / 100));   // 奈秒 → tick（100 ns）
    }

    // ── 串流生成（§11.2 C4）────────────────────────────────────────────────

    /// <summary>
    /// 串流版的 <see cref="ChatAsync"/>。**三道可用性檢查與非串流版逐條對齊**
    /// （<c>think:false</c>、截斷即失敗、空內容即失敗），差別只在檢查的時機：
    /// 截斷與空內容只有在最後一行 NDJSON 到達時才知道，那時畫面上通常已經有字了。
    ///
    /// <para><b>為什麼不重用 <see cref="PostAsync{TRequest, TResponse}"/></b>：那支會
    /// <c>ReadFromJsonAsync</c> 整個回應，也就是把整段生成先讀完再回傳——
    /// 拿它做串流會得到「等 50 秒然後一次全部出現」，正是本節要消滅的東西。
    /// 這裡改用 <c>HttpCompletionOption.ResponseHeadersRead</c> 逐行讀。</para>
    ///
    /// <para>⚠️ <b>逾時的語意跟著改變了</b>：<c>HttpClient.Timeout</c> 在
    /// <c>ResponseHeadersRead</c> 下只涵蓋到「收到回應標頭」為止，之後逐行讀取不受它管。
    /// 對串流而言這是想要的（生成久不該被砍），但代價是**對面若在中途卡住不吐字也不斷線，
    /// 這裡會一直等**。取消仍然有效（<paramref name="ct"/> 一路傳到 <c>ReadLineAsync</c>），
    /// 所以使用者按取消永遠能脫身；沒有人按的情況才需要靠上游的閒置偵測，目前沒有做。</para>
    /// </summary>
    public async IAsyncEnumerable<LlmDelta> ChatStreamAsync(
        string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_chatModel.Length == 0)
            throw new OllamaException($"未設定生成模型。請在組態填入 {ChatModelKey}（例如 gemma4），並確認該模型已 ollama pull。");

        var request = new ChatRequest
        {
            Model = _chatModel,
            Stream = true,
            Think = false,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            },
            Options = new GenerationOptions { NumPredict = _numPredict, Temperature = Temperature },
        };

        // 名額涵蓋整個列舉。iterator 的 using 在列舉器 dispose 時才釋放，
        // 也就是 await foreach 正常跑完、break、或例外離開時——三種都會歸還。
        using var lease = await _gate.EnterAsync(InferenceLane.Interactive, ct);

        var client = CreateClient();
        var text = new System.Text.StringBuilder();
        ChatResponse? tail = null;

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request, options: Json),
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OllamaException(
                $"呼叫 {client.BaseAddress}api/chat 逾時（{_timeoutSeconds} 秒）。" +
                $"純 CPU 推論本來就慢，若屬正常可調高 {TimeoutSecondsKey}。");
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException(
                $"無法連線至推論服務 {client.BaseAddress}：{ex.Message}。" +
                $"請確認 Ollama 已啟動且 {UrlKey} 正確（開發機需維持 SSH 反向通道，見規格 §17.11.2）。", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new OllamaException(await DescribeErrorAsync(response, "/api/chat", ct));

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;

                ChatResponse? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<ChatResponse>(line, Json);
                }
                catch (JsonException ex)
                {
                    // 串流中出現不是 JSON 的一行，代表對面不是 Ollama（反向代理的錯誤頁最常見）。
                    // 靜靜跳過會讓答案默默少一段，所以當場停。
                    throw new OllamaException(
                        $"推論服務的串流回應無法解析：{Truncate(line)}。請確認 {UrlKey} 指向的是 Ollama 而非其他服務。", ex);
                }

                if (frame is null) continue;

                var delta = frame.Message?.Content;
                if (!string.IsNullOrEmpty(delta))
                {
                    text.Append(delta);
                    yield return new LlmDelta(delta, null);
                }

                if (frame.Done) { tail = frame; break; }
            }
        }

        // 沒有收到 done:true 的那一行＝串流被中途切斷。已經吐出去的字是半截答案，
        // 不能讓它看起來像完成——這正是 LlmDelta.Final 存在的理由。
        if (tail is null)
            throw new OllamaException(
                $"串流在收到結束標記前中斷（已生成 {text.Length} 字）。" +
                "常見原因是推論服務被重啟或中間的反向代理逾時。");

        // 以下兩道與非串流版同義，訊息也刻意一致——同一種故障不該因為換了通道就換一種說法。
        if (string.Equals(tail.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new OllamaException(
                $"生成被長度上限截斷（已生成 {tail.EvalCount} tokens，{NumPredictKey}={_numPredict}）。" +
                $"思考型模型的推理過程會吃掉額度，導致答案為空。請調高 {NumPredictKey}。");

        var full = text.ToString();
        if (string.IsNullOrWhiteSpace(full))
            throw new OllamaException(
                $"模型 {tail.Model ?? _chatModel} 回覆了空內容（done_reason={tail.DoneReason ?? "未提供"}）。" +
                "已生成的 token 全部進了思考過程時會出現這個結果，請確認呼叫端有送出 think:false。");

        yield return new LlmDelta(string.Empty, new LlmAnswer(
            full.Trim(),
            tail.Model ?? _chatModel,
            tail.DoneReason ?? string.Empty,
            tail.PromptEvalCount,
            tail.EvalCount,
            TimeSpan.FromTicks(tail.TotalDuration / 100)));
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "…";

    // ── 帶工具的生成（Phase 2）──────────────────────────────────────────────

    public async Task<LlmToolAnswer> ChatWithToolsAsync(
        string systemPrompt, string userPrompt,
        IReadOnlyList<QaToolDefinition> tools, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (_chatModel.Length == 0)
            throw new OllamaException($"未設定生成模型。請在組態填入 {ChatModelKey}（例如 gemma4），並確認該模型已 ollama pull。");

        // 空工具目錄＝這位使用者一支報表工具都沒有權限。送出去只會讓模型憑空編一個工具名，
        // 而那個名字之後一定驗不過——不如當場講清楚（§9.2 的誠實拒答，只是原因不同）。
        if (tools.Count == 0)
            throw new OllamaException("目前沒有任何您有權限的查詢工具，因此無法以自然語言查詢。");

        var request = new ChatRequest
        {
            Model = _chatModel,
            Stream = false,
            Think = false,                       // 理由同 ChatAsync ①（§10.8.1 第 1 點）
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            ],
            Tools = tools.Select(ToWire).ToArray(),
            Options = new GenerationOptions { NumPredict = _numPredict, Temperature = Temperature },
        };

        var response = await PostAsync<ChatRequest, ChatResponse>(
            "/api/chat", request, InferenceLane.Interactive, ct);

        // 截斷＝失敗，理由同 ChatAsync ②。
        if (string.Equals(response.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            throw new OllamaException(
                $"工具選取被長度上限截斷（已生成 {response.EvalCount} tokens，{NumPredictKey}={_numPredict}）。" +
                $"請調高 {NumPredictKey}。");

        var calls = (response.Message?.ToolCalls ?? [])
            .Where(c => c.Function is not null && !string.IsNullOrWhiteSpace(c.Function.Name))
            .Select(c => new LlmToolCall(
                c.Function!.Name!.Trim(),
                // arguments 是 JSON object；原樣轉字串交給 ReportToolRunner 反序列化，
                // 這裡不做任何解讀——參數的合法性由工具契約決定，不是由這裡猜。
                c.Function.Arguments.ValueKind == JsonValueKind.Undefined
                    ? "{}"
                    : c.Function.Arguments.GetRawText()))
            .ToList();

        var content = response.Message?.Content?.Trim() ?? string.Empty;

        // ⚠️ 與 ChatAsync ③ 不同：這裡**不能**把空 content 當失敗。
        // 只有「既沒選工具、也沒說任何話」才是真的失敗——那代表這一輪什麼都沒發生。
        if (calls.Count == 0 && content.Length == 0)
            throw new OllamaException(
                $"模型 {response.Model ?? _chatModel} 既沒有選擇任何工具、也沒有回覆內容" +
                $"（done_reason={response.DoneReason ?? "未提供"}）。請確認該模型支援 function calling。");

        return new LlmToolAnswer(
            calls, content,
            response.Model ?? _chatModel,
            response.PromptEvalCount, response.EvalCount,
            TimeSpan.FromTicks(response.TotalDuration / 100));
    }

    private static ToolWire ToWire(QaToolDefinition d) => new()
    {
        Function = new ToolFunctionWire
        {
            Name = d.Name,
            Description = d.Description,
            Parameters = new ToolParametersWire
            {
                Properties = d.Properties.ToDictionary(
                    kv => kv.Key,
                    kv => new ToolPropertyWire
                    {
                        Type = kv.Value.Type,
                        Enum = kv.Value.EnumValues?.ToArray(),
                        Format = kv.Value.Format,
                    },
                    StringComparer.Ordinal),
            },
        },
    };

    // ── 嵌入 ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, EmbeddingKind kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return Array.Empty<float[]>();

        if (_embeddingModel.Length == 0)
            throw new OllamaException($"未設定嵌入模型。請在組態填入 {EmbeddingModelKey}，並確認該模型已 ollama pull。");

        var prefixed = inputs.Select(t => ApplyPrefix(_embeddingModel, kind, t)).ToArray();

        // 車道由既有的 `EmbeddingKind` 決定，不新增參數：Query 是使用者剛按下送出、
        // Passage 是建索引在批次重嵌（§10.3）。這個區分本來就存在（它決定前綴），
        // 而它剛好也就是「後面有沒有人在等」的那條界線。
        var response = await PostAsync<EmbedRequest, EmbedResponse>(
            "/api/embed", new EmbedRequest { Model = _embeddingModel, Input = prefixed },
            kind == EmbeddingKind.Query ? InferenceLane.Interactive : InferenceLane.Background, ct);

        var vectors = response.Embeddings;

        // 數量對不上是最惡劣的失敗形狀：不會報錯，只會讓向量與 chunk **錯位**，
        // 於是每則答覆都引用到別的頁面，而系統看起來完全正常。
        if (vectors is null || vectors.Length != inputs.Count)
            throw new OllamaException(
                $"嵌入模型 {_embeddingModel} 回傳 {vectors?.Length ?? 0} 個向量，與送出的 {inputs.Count} 段文字不符。" +
                "數量不符會讓向量與手冊內容錯位，故整批視為失敗。");

        for (var i = 0; i < vectors.Length; i++)
        {
            var v = vectors[i];

            if (v is null || v.Length == 0)
                throw new OllamaException($"嵌入模型 {_embeddingModel} 對第 {i + 1} 段文字回傳空向量。");

            if (v.Length != vectors[0].Length)
                throw new OllamaException(
                    $"嵌入模型 {_embeddingModel} 回傳的維度不一致（第 1 段 {vectors[0].Length} 維、" +
                    $"第 {i + 1} 段 {v.Length} 維），整批視為失敗。");

            // 零向量的餘弦相似度是 0/0。放行的話，錯誤會在檢索期以 NaN 的形式出現，
            // 排序結果不確定而且完全看不出成因。
            if (v.All(x => x == 0f))
                throw new OllamaException($"嵌入模型 {_embeddingModel} 對第 {i + 1} 段文字回傳零向量，無法計算相似度。");
        }

        return vectors;
    }

    /// <summary>
    /// 依「模型 × 角色」決定前綴。**索引期與查詢期共用這一個方法**（§10.7 實作要求 1）——
    /// 與 <see cref="CjkTokenizer"/> 同樣的理由：兩份實作遲早對不上，而且無聲。
    ///
    /// <para><b>表裡只放實測查證過的模型</b>（§10.7 實作要求 2：本規格刻意不預先斷言）。
    /// 未列的模型不加前綴——對「本來就不需要前綴」的模型是正確的，
    /// 對「需要但還沒查證」的模型是保守的（品質略降，不會壞掉）。</para>
    /// </summary>
    public static string ApplyPrefix(string model, EmbeddingKind kind, string text)
    {
        var name = model.Split(':')[0];

        var prefix = name switch
        {
            // embeddinggemma（2026-08-06 實測查證，見 §10.7）：
            //   ① Ollama 的 template 是 {{ .Prompt }}——**不會自動補任何前綴**，
            //      所以這件事一定要呼叫端做，不能指望模型端處理。
            //   ② 以 30 題題庫在頁面層級量四個方案：官方前綴 Recall@1 30/30、
            //      完全不加 28/30。差距只有兩題，故**採用的理由是「量過之後不比別的差、
            //      且是模型卡的慣例」，不是「量出明顯優勢」**。
            "embeddinggemma" => kind == EmbeddingKind.Query
                ? "task: search result | query: "
                : "title: none | text: ",

            // ── 以下三顆為 §10.7 的評選候選（2026-08-06 加入）───────────────
            // 已查證的部分：三顆的 Ollama template 都**不會自動補前綴**
            //   （granite／nomic 是 `{{ .Prompt }}`；snowflake 的 template 欄位裡
            //   裝的是 Apache License 全文——那是打包疏失，但 /api/embed 不套 template，
            //   已用「無關兩句的餘弦」驗證未被污染：0.3346，若污染會接近 1.0）。
            // **尚未查證的部分**：前綴本身取自各自的模型卡，未在本語料上做 A/B。
            //   對還沒被採用的候選先花一輪去 A/B 前綴是浪費——
            //   真的有候選勝出時，採用前再補量它的前綴（與 embeddinggemma 同樣的流程）。
            //   評選時三顆都用「自己模型卡的建議設定」，才不會比成「別人有前綴、它沒有」。

            // Snowflake Arctic Embed 2.0：只有查詢加前綴，文件不加。
            "snowflake-arctic-embed2" => kind == EmbeddingKind.Query ? "query: " : string.Empty,

            // Nomic Embed v2 MoE：查詢與文件各有前綴，兩邊都要加。
            "nomic-embed-text-v2-moe" => kind == EmbeddingKind.Query
                ? "search_query: "
                : "search_document: ",

            // IBM Granite Embedding：模型卡明示**不需要前綴**，故維持空字串
            //   （下方 `_` 也會得到同樣結果，這一行是為了把「查證過確實不用」
            //   和「沒查證所以保守不加」區分開——兩者的後續處理不同）。
            "granite-embedding" => string.Empty,

            _ => string.Empty,
        };

        return prefix + text;
    }

    // ── 健康檢查 ────────────────────────────────────────────────────────────

    public async Task<OllamaHealth> CheckAsync(CancellationToken ct = default)
    {
        var client = CreateClient();

        string version;
        try
        {
            var v = await client.GetFromJsonAsync<VersionResponse>("/api/version", Json, ct);
            version = v?.Version ?? string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new OllamaHealth(false, string.Empty, false, false,
                $"連不上推論服務 {client.BaseAddress}：{ex.Message}。請確認 Ollama 已啟動，" +
                $"以及 {UrlKey} 是否正確（開發機需維持 SSH 反向通道，見規格 §17.11.2）。");
        }

        var tags = await client.GetFromJsonAsync<TagsResponse>("/api/tags", Json, ct);
        var present = tags?.Models?.Select(m => m.Name ?? string.Empty).ToArray() ?? Array.Empty<string>();

        var chatOk = _chatModel.Length > 0 && present.Any(p => ModelMatches(_chatModel, p));
        var embedOk = _embeddingModel.Length > 0 && present.Any(p => ModelMatches(_embeddingModel, p));

        var missing = new List<string>();
        if (_chatModel.Length == 0) missing.Add($"未設定 {ChatModelKey}");
        else if (!chatOk) missing.Add($"生成模型 {_chatModel} 不存在（請執行 ollama pull {_chatModel}）");
        if (_embeddingModel.Length == 0) missing.Add($"未設定 {EmbeddingModelKey}");
        else if (!embedOk) missing.Add($"嵌入模型 {_embeddingModel} 不存在（請執行 ollama pull {_embeddingModel}）");

        var detail = missing.Count == 0
            ? $"Ollama {version} 正常，生成模型 {_chatModel}、嵌入模型 {_embeddingModel} 均已就緒。"
            : $"Ollama {version} 可連線，但：{string.Join("；", missing)}。";

        return new OllamaHealth(true, version, chatOk, embedOk, detail);
    }

    /// <summary>
    /// 組態寫 <c>gemma4</c>、Ollama 回 <c>gemma4:latest</c> 是同一顆模型。
    /// 不處理這件事的話，健康檢查會對一個裝好的模型回報「不存在」。
    /// </summary>
    public static bool ModelMatches(string configured, string present)
    {
        if (configured.Equals(present, StringComparison.OrdinalIgnoreCase)) return true;
        return !configured.Contains(':') &&
               present.Equals($"{configured}:latest", StringComparison.OrdinalIgnoreCase);
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(HttpClientName);

        // 具名 client 未經 QAModule 註冊時（單元測試直接 new 本類別的情形），
        // BaseAddress 會是 null。這裡補上預設，讓失敗形狀是「連不上」而不是 NRE。
        client.BaseAddress ??= new Uri(DefaultUrl);
        return client;
    }

    /// <summary>
    /// **閘門取在這裡，不取在各個公開方法**（§11.2 C1）：這是所有推論唯一的出口，
    /// 放在這裡就不會有人日後新增一個方法時忘了排隊。
    ///
    /// <para>健康檢查（<see cref="CheckAsync"/>）刻意**不經過這裡**——它走 GET，
    /// 而且必須在系統忙碌時照樣答得出來。排在一題幾十秒的推論後面，
    /// 那個健康橫幅就等於沒有（§17.8）。</para>
    /// </summary>
    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest body, InferenceLane lane, CancellationToken ct)
    {
        // 名額**涵蓋整個 HTTP 來回**，不是只涵蓋送出：推論的時間全花在對面，
        // 拿到回應才算讓出位子。
        using var lease = await _gate.EnterAsync(lane, ct);

        var client = CreateClient();

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(path, body, Json, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 逾時在 .NET 是 TaskCanceledException，與「呼叫端取消」同一個型別。
            // 靠 ct 分辨，否則逾時會被誤報成使用者取消，反之亦然。
            throw new OllamaException(
                $"呼叫 {client.BaseAddress}{path.TrimStart('/')} 逾時（{_timeoutSeconds} 秒）。" +
                $"純 CPU 推論本來就慢，若屬正常可調高 {TimeoutSecondsKey}。");
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException(
                $"無法連線至推論服務 {client.BaseAddress}：{ex.Message}。" +
                $"請確認 Ollama 已啟動且 {UrlKey} 正確（開發機需維持 SSH 反向通道，見規格 §17.11.2）。", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new OllamaException(await DescribeErrorAsync(response, path, ct));

        var parsed = await response.Content.ReadFromJsonAsync<TResponse>(Json, ct);
        if (parsed is null)
            throw new OllamaException($"推論服務對 {path} 回覆了無法解析的內容（HTTP {(int)response.StatusCode}）。");

        return parsed;
    }

    /// <summary>
    /// 把 Ollama 的錯誤回應翻成看得懂的一句話。**404 幾乎一定是模型沒拉**，
    /// 而原始訊息是英文的 <c>model "x" not found, try pulling it first</c>——
    /// 直接丟給使用者等於什麼都沒說。
    /// </summary>
    private async Task<string> DescribeErrorAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        var detail = raw;

        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(raw, Json)?.Error;
            if (!string.IsNullOrWhiteSpace(error)) detail = error;
        }
        catch (JsonException)
        {
            // 非 JSON（例如反向代理擋下來的 HTML 錯誤頁）。保留原文，截短即可。
        }

        if (detail.Length > 500) detail = detail[..500];

        var hint = response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                $" 這通常代表模型尚未下載，請執行 ollama pull（生成：{_chatModel}；嵌入：{_embeddingModel}）。",
            HttpStatusCode.BadRequest =>
                " 這通常代表請求參數與該模型不相容（例如對不支援思考的模型送出 think 參數）。",
            _ => string.Empty,
        };

        return $"推論服務 {path} 回應 HTTP {(int)response.StatusCode}：{detail}。{hint}".TrimEnd();
    }

    // ── Ollama 原生 API 的線上格式 ──────────────────────────────────────────
    // 刻意不用共用 DTO：這些型別的形狀由 Ollama 決定，不是本系統的契約。

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; init; } = Array.Empty<ChatMessage>();
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("think")] public bool Think { get; init; }
        [JsonPropertyName("options")] public GenerationOptions? Options { get; init; }

        /// <summary>只在帶工具時送出；null 時不序列化，普通問答的線上格式維持原樣。</summary>
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolWire[]? Tools { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;

        /// <summary>只出現在回應。送出時為 null 故不序列化。</summary>
        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolCallWire[]? ToolCalls { get; init; }
    }

    private sealed class ToolCallWire
    {
        [JsonPropertyName("function")] public ToolCallFunctionWire? Function { get; init; }
    }

    private sealed class ToolCallFunctionWire
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
    }

    private sealed class ToolWire
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "function";
        [JsonPropertyName("function")] public ToolFunctionWire? Function { get; init; }
    }

    private sealed class ToolFunctionWire
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
        [JsonPropertyName("parameters")] public ToolParametersWire? Parameters { get; init; }
    }

    private sealed class ToolParametersWire
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "object";
        [JsonPropertyName("properties")]
        public Dictionary<string, ToolPropertyWire> Properties { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class ToolPropertyWire
    {
        [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;

        [JsonPropertyName("enum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Enum { get; init; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Format { get; init; }
    }

    private sealed class GenerationOptions
    {
        [JsonPropertyName("num_predict")] public int NumPredict { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
        [JsonPropertyName("done")] public bool Done { get; init; }
        [JsonPropertyName("done_reason")] public string? DoneReason { get; init; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; init; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; init; }
        [JsonPropertyName("total_duration")] public long TotalDuration { get; init; }
    }

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
        [JsonPropertyName("input")] public string[] Input { get; init; } = Array.Empty<string>();
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")] public float[][]? Embeddings { get; init; }
    }

    private sealed class VersionResponse
    {
        [JsonPropertyName("version")] public string? Version { get; init; }
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")] public TagModel[]? Models { get; init; }
    }

    private sealed class TagModel
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class ErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; init; }
    }
}
