using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// **開發期評測**用的推論後端選擇（規格 §17.11.8-六／七，路徑一）。
    ///
    /// <para><b>這裡是測試專案，不是產品程式。</b>§17.11.8 的裁決把「外部 LLM」切成兩件事：
    /// 產品功能要等待決 #49 裁決，而**開發期評測不需要**——餵進去的是手冊語料與題庫
    /// （§10.8.2 的 <c>ManualQuestionBank</c>），完全不觸及 §八 的機密營運資料。
    /// 所以它刻意住在 <c>SmartFactory.MES.IntegrationTests</c>：不進部署、客戶端無感、
    /// 也不可能被產品的 DI 解析到。</para>
    ///
    /// <para><b>為什麼要有它</b>：本機 CPU 推論是 52.8–97 秒／題，一輪評測好幾小時，
    /// 而換模型、改提示詞都要重跑一輪——評測迴圈的瓶頸從來不是題庫而是等待。
    /// 更關鍵的是「本地 <c>gemma4:latest</c> 到底夠不夠好」這個問題**目前沒有基準線可回答**。</para>
    ///
    /// <para>⚠️ <b>嵌入永遠留在本地</b>（§17.11.8 代價 2）：換嵌入供應商＝換向量空間＝全量重嵌。
    /// 這裡只換生成端，檢索仍走本機 Ollama，所以 <c>QA_LIVE_OLLAMA</c> 那道前置仍然必要——
    /// 沒有它就沒有語意通道，量到的會是「詞彙檢索餵給雲端模型」的混合物。</para>
    ///
    /// <para>⚠️ <b>只換生成端，指標的可比性才成立</b>：同一份題庫、同一份提示詞、
    /// 同一套判定程式碼（<see cref="ManualGenerationBenchmarkTests"/> 本體一行都沒為它改判定），
    /// 差異才只剩「哪一顆模型」。</para>
    ///
    /// <para>🔒 金鑰只從環境變數讀，**永不寫進 appsettings**（§17.11.8-四第 5 條：
    /// 該檔會被還原、會進備份，而 gitleaks 只擋得住進版控的那一份）。</para>
    /// </summary>
    public static class EvalLlmClients
    {
        /// <summary><c>ollama</c>（預設，走 DI 的本機 client）｜<c>anthropic</c>｜<c>openai-compatible</c>。</summary>
        public const string ProviderVar = "QA_EVAL_PROVIDER";

        /// <summary>模型 ID。<c>anthropic</c> 例：<c>claude-opus-5</c>／<c>claude-haiku-4-5</c>。</summary>
        public const string ModelVar = "QA_EVAL_MODEL";

        /// <summary>API 金鑰。**只讀環境變數**。</summary>
        public const string KeyVar = "QA_EVAL_API_KEY";

        /// <summary>自架 OpenAI 相容服務的基底位址（vLLM／LM Studio／llama.cpp／LiteLLM）。</summary>
        public const string BaseUrlVar = "QA_EVAL_BASE_URL";

        /// <summary>生成長度上限。對照 <c>NumPredict</c>，預設同為 1024。</summary>
        public const string MaxTokensVar = "QA_EVAL_MAX_TOKENS";

        /// <summary>
        /// 併進請求本體的額外 JSON 物件（例：<c>{"thinking":{"type":"disabled"}}</c>）。
        ///
        /// <para><b>刻意不把「關閉思考」寫成程式碼裡的固定參數。</b>§10.8.1 的硬性要求是
        /// 控制思考行為（實測 9 倍差距），但各家的做法不同、而且會變：Claude 的
        /// adaptive thinking 預設開啟，<c>thinking:{"type":"disabled"}</c> 在 effort 高於 high 時
        /// 直接回 400，官方作法是改用低 effort 取代關閉。把某一家某一版的寫法寫死在
        /// 這裡，過期時的症狀是「400 或悄悄多花十倍 token」。留成組態，
        /// 跑評測的人就能照當下的官方文件填，而**填了什麼會印在報表抬頭上**。</para>
        /// </summary>
        public const string ExtraJsonVar = "QA_EVAL_EXTRA_JSON";

        public const int DefaultMaxTokens = 1024;

        /// <summary>
        /// 依環境變數決定這一輪評測用哪個生成後端。預設完全維持原行為（DI 的本機 Ollama）。
        /// </summary>
        /// <returns>client 與**要印進報表抬頭的標籤**——沒有標籤的數字沒有意義，
        /// 兩輪之間分不出是換了模型還是換了後端。</returns>
        public static (ILlmClient Client, string Label) Resolve(IServiceProvider sp)
        {
            var provider = (Environment.GetEnvironmentVariable(ProviderVar) ?? "ollama").Trim().ToLowerInvariant();
            var model = Environment.GetEnvironmentVariable(ModelVar)?.Trim() ?? string.Empty;
            var extra = Environment.GetEnvironmentVariable(ExtraJsonVar)?.Trim() ?? string.Empty;
            var maxTokens = int.TryParse(Environment.GetEnvironmentVariable(MaxTokensVar), out var m) && m > 0
                ? m : DefaultMaxTokens;

            switch (provider)
            {
                case "ollama" or "":
                    return (sp.GetRequiredService<ILlmClient>(), "ollama（本機，走產品 DI）");

                case "anthropic":
                {
                    var key = Require(KeyVar);
                    if (model.Length == 0) model = "claude-haiku-4-5";
                    return (new AnthropicEvalClient(key, model, maxTokens, extra),
                        $"anthropic／{model}／max_tokens={maxTokens}" + ExtraLabel(extra));
                }

                case "openai-compatible":
                {
                    var baseUrl = Require(BaseUrlVar);
                    if (model.Length == 0) throw new InvalidOperationException($"{ProviderVar}=openai-compatible 必須指定 {ModelVar}。");
                    // 金鑰可有可無：自架的 vLLM／LM Studio 通常沒有認證。
                    var key = Environment.GetEnvironmentVariable(KeyVar);
                    return (new OpenAiCompatibleEvalClient(baseUrl, key, model, maxTokens, extra),
                        $"openai-compatible／{baseUrl}／{model}／max_tokens={maxTokens}" + ExtraLabel(extra));
                }

                default:
                    throw new InvalidOperationException(
                        $"{ProviderVar}='{provider}' 不認得。可用值：ollama｜anthropic｜openai-compatible。");
            }
        }

        private static string ExtraLabel(string extra) => extra.Length == 0 ? "" : $"／extra={extra}";

        private static string Require(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
                ? v
                : throw new InvalidOperationException(
                    $"未設定環境變數 {name}。金鑰與端點只從環境變數讀，不寫進 appsettings（§17.11.8-四第 5 條）。");

        /// <summary>
        /// 把 <see cref="ExtraJsonVar"/> 併進請求本體。
        /// **同名鍵以 extra 為準**——它的用途正是覆寫預設值（例如把 max_tokens 換掉）。
        /// </summary>
        internal static void MergeExtra(JsonObject body, string extraJson)
        {
            if (extraJson.Length == 0) return;

            JsonObject extra;
            try
            {
                extra = JsonNode.Parse(extraJson) as JsonObject
                        ?? throw new OllamaException($"{ExtraJsonVar} 必須是一個 JSON 物件，例如 {{\"effort\":\"low\"}}。");
            }
            catch (JsonException ex)
            {
                throw new OllamaException($"{ExtraJsonVar} 不是合法的 JSON：{ex.Message}", ex);
            }

            foreach (var kv in extra.ToList())
            {
                extra.Remove(kv.Key);           // 先脫離原父節點，否則 JsonNode 會拋「已有父節點」
                body[kv.Key] = kv.Value;
            }
        }
    }

    /// <summary>
    /// Anthropic Messages API（C 類：商用雲端）。
    ///
    /// <para><b>刻意不走 OpenAI 相容 shim</b>（§17.11.8-五）：相容層是過渡用途的最小交集，
    /// 會失去 thinking 控制這一項本專案唯一的硬性要求。這裡直接打 <c>/v1/messages</c>，
    /// 是最薄的一層——請求本體只有 model／max_tokens／system／messages，
    /// 其餘一律經 <see cref="EvalLlmClients.ExtraJsonVar"/> 由跑評測的人明示。</para>
    ///
    /// <para>失敗一律包成 <see cref="OllamaException"/>：<c>ManualGenerationBenchmarkTests.AskAsync</c>
    /// 用它來實現「單題失敗不中斷整輪」。換一個例外型別會讓一次 429 廢掉整輪 65 題的量測。</para>
    /// </summary>
    internal sealed class AnthropicEvalClient : ILlmClient
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

        private readonly string _key;
        private readonly string _model;
        private readonly int _maxTokens;
        private readonly string _extraJson;

        public AnthropicEvalClient(string key, string model, int maxTokens, string extraJson)
        {
            _key = key;
            _model = model;
            _maxTokens = maxTokens;
            _extraJson = extraJson;
        }

        internal string BuildBody(string systemPrompt, string userPrompt)
        {
            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,

                // system 是**頂層欄位**，不是 messages 裡的一則。放進 messages 的話
                // 它會被當成使用者發言，而 §10.8.2 之二已經量過：同一顆模型、
                // 提示詞位置不同，拒答行為就不同——那會讓兩輪數字不可比。
                ["system"] = systemPrompt,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt },
                },
            };

            EvalLlmClients.MergeExtra(body, _extraJson);
            return body.ToJsonString();
        }

        /// <summary>
        /// 回應判讀。<c>stop_reason == "max_tokens"</c> 與空字串一律當失敗——
        /// 與 <c>OllamaClient</c> 對 <c>done_reason == "length"</c> 的處置一致（§10.8.1 第 2／3 點）。
        /// 兩邊判準不同的話，「被截斷」在本地算失敗、在雲端算成功，而截斷的答案
        /// 通常只是少了最後一行來源——**看起來完全正常**。
        /// </summary>
        internal static LlmAnswer ParseAnswer(string json, string requestedModel, TimeSpan elapsed)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException ex) { throw new OllamaException($"回應不是合法的 JSON：{ex.Message}", ex); }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    throw new OllamaException("API 回錯誤：" +
                        (err.TryGetProperty("message", out var msg) ? msg.GetString() : err.ToString()));

                var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";
                var model = root.TryGetProperty("model", out var mm) ? mm.GetString() ?? requestedModel : requestedModel;

                // content 是區塊陣列，可能同時有 thinking 與 text 區塊。
                // **只取 text**：thinking 不是答案，混進來會讓引文解析（§10.8.1）看到
                // 一堆不存在的頁面代號，於是幻覺引文率變成雜訊。
                var text = new StringBuilder();
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                            block.TryGetProperty("text", out var txt))
                            text.Append(txt.GetString());

                var input = 0;
                var output = 0;
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var i)) input = i.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var o)) output = o.GetInt32();
                }

                if (stopReason == "max_tokens")
                    throw new OllamaException(
                        $"生成被 max_tokens（{output} tokens）截斷，這一題不計分。" +
                        $"請調高 {EvalLlmClients.MaxTokensVar}，或以 {EvalLlmClients.ExtraJsonVar} 降低思考額度。");

                if (text.Length == 0 || string.IsNullOrWhiteSpace(text.ToString()))
                    throw new OllamaException(
                        $"回應沒有任何 text 區塊（stop_reason={stopReason}，output_tokens={output}）。" +
                        "最常見的原因是思考過程吃光了額度——與本地 num_predict 設太小是同一種形狀（§10.8.1 第 2 點）。");

                return new LlmAnswer(text.ToString(), model, stopReason, input, output, elapsed);
            }
        }

        public async Task<LlmAnswer> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(BuildBody(systemPrompt, userPrompt), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", _key);
            req.Headers.Add("anthropic-version", ApiVersion);

            string json;
            try
            {
                using var res = await Http.SendAsync(req, ct);
                json = await res.Content.ReadAsStringAsync(ct);

                // 4xx／5xx 的本體通常就是 {"error":{...}}，交給 ParseAnswer 統一判讀；
                // 但完全空的回應要在這裡就講清楚，否則錯誤訊息會變成「JSON 解析失敗」。
                if (json.Length == 0)
                    throw new OllamaException($"HTTP {(int)res.StatusCode} 且回應本體是空的。");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new OllamaException($"呼叫 {Endpoint} 逾時（{Http.Timeout.TotalSeconds:F0} 秒）。", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaException(
                    $"連不上 {Endpoint}：{ex.Message}。開發機在沙箱內沒有外網——" +
                    "這一項評測必須在有對外連線的環境跑。", ex);
            }

            return ParseAnswer(json, _model, sw.Elapsed);
        }
    }

    /// <summary>
    /// 自架 OpenAI 相容服務（B 類：vLLM／LM Studio／llama.cpp server／LiteLLM proxy）。
    ///
    /// <para>§17.11.8-五 的分類裡，**只有這一類是「換個 URL」就成立的**：
    /// 同一份請求格式打得到很多後端，而它們都在自己機房裡。
    /// C 類（商用雲端）不行的理由見 <see cref="AnthropicEvalClient"/>。</para>
    /// </summary>
    internal sealed class OpenAiCompatibleEvalClient : ILlmClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private readonly Uri _endpoint;
        private readonly string? _key;
        private readonly string _model;
        private readonly int _maxTokens;
        private readonly string _extraJson;

        public OpenAiCompatibleEvalClient(string baseUrl, string? key, string model, int maxTokens, string extraJson)
        {
            _endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "v1/chat/completions");
            _key = key;
            _model = model;
            _maxTokens = maxTokens;
            _extraJson = extraJson;
        }

        internal string BuildBody(string systemPrompt, string userPrompt)
        {
            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt },
                },
            };

            EvalLlmClients.MergeExtra(body, _extraJson);
            return body.ToJsonString();
        }

        internal static LlmAnswer ParseAnswer(string json, string requestedModel, TimeSpan elapsed)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException ex) { throw new OllamaException($"回應不是合法的 JSON：{ex.Message}", ex); }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    throw new OllamaException("API 回錯誤：" +
                        (err.TryGetProperty("message", out var msg) ? msg.GetString() : err.ToString()));

                var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                             && choices.GetArrayLength() > 0
                    ? choices[0]
                    : throw new OllamaException("回應沒有 choices，無法取得答案。");

                var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "" : "";
                var text = choice.TryGetProperty("message", out var message) &&
                           message.TryGetProperty("content", out var c)
                    ? c.GetString() ?? ""
                    : "";

                var model = root.TryGetProperty("model", out var mm) ? mm.GetString() ?? requestedModel : requestedModel;

                var input = 0;
                var output = 0;
                if (root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("prompt_tokens", out var i)) input = i.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var o)) output = o.GetInt32();
                }

                if (finish == "length")
                    throw new OllamaException(
                        $"生成被 max_tokens（{output} tokens）截斷，這一題不計分。" +
                        $"請調高 {EvalLlmClients.MaxTokensVar}。");

                if (string.IsNullOrWhiteSpace(text))
                    throw new OllamaException(
                        $"回應的 content 是空的（finish_reason={finish}，completion_tokens={output}）。" +
                        "與本地 num_predict 設太小是同一種形狀（§10.8.1 第 2 點）。");

                return new LlmAnswer(text, model, finish, input, output, elapsed);
            }
        }

        public async Task<LlmAnswer> ChatAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(BuildBody(systemPrompt, userPrompt), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_key))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);

            string json;
            try
            {
                using var res = await Http.SendAsync(req, ct);
                json = await res.Content.ReadAsStringAsync(ct);
                if (json.Length == 0)
                    throw new OllamaException($"HTTP {(int)res.StatusCode} 且回應本體是空的。");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new OllamaException($"呼叫 {_endpoint} 逾時（{Http.Timeout.TotalSeconds:F0} 秒）。", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaException($"連不上 {_endpoint}：{ex.Message}", ex);
            }

            return ParseAnswer(json, _model, sw.Elapsed);
        }
    }
}
