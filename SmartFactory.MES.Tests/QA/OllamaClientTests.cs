using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="OllamaClient"/> 的守門測試。**完全不碰真實推論**——
    /// 這正是規格 §17.5 說「介面存在的理由是讓測試替換掉真實推論」的意思。
    ///
    /// <para>這裡守的核心是 §10.8.1 的三道契約，它們全部來自 2026-08-05 的實測：
    /// <c>think:false</c> 一定要送出、被截斷要當成失敗、空回覆要當成失敗。
    /// 三者的共同點是 **HTTP 200 卻沒有答案**——沒有這些檢查，
    /// 症狀會是「有時候問了沒反應」，是最難查的形狀。</para>
    /// </summary>
    public class OllamaClientTests
    {
        private const string Url = "http://127.0.0.1:11435";

        /// <summary>可控的假 HTTP 端點：記下送出的請求，回傳指定的回應或例外。</summary>
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            public string? LastBody { get; private set; }
            public string? LastPath { get; private set; }
            public int Calls { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                LastPath = request.RequestUri?.AbsolutePath;
                LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return _respond(request);
            }
        }

        private sealed class FakeFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public FakeFactory(HttpMessageHandler handler) => _handler = handler;

            public HttpClient CreateClient(string name) =>
                new(_handler, disposeHandler: false) { BaseAddress = new Uri(Url) };
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static OllamaClient Build(
            HttpMessageHandler handler,
            string chatModel = "gemma4",
            string embeddingModel = "embeddinggemma",
            params (string Key, string Value)[] extra)
        {
            var settings = new Dictionary<string, string?>
            {
                [OllamaClient.UrlKey] = Url,
                [OllamaClient.ChatModelKey] = chatModel,
                [OllamaClient.EmbeddingModelKey] = embeddingModel,
            };
            foreach (var (k, v) in extra) settings[k] = v;

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            return new OllamaClient(new FakeFactory(handler), configuration, NullLogger<OllamaClient>.Instance);
        }

        private static string ChatOk(string content, string doneReason = "stop") => $$"""
            {
              "model": "gemma4:latest",
              "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}} },
              "done": true,
              "done_reason": "{{doneReason}}",
              "prompt_eval_count": 412,
              "eval_count": 58,
              "total_duration": 4100000000
            }
            """;

        // ── 生成：§10.8.1 的三道契約 ────────────────────────────────────────

        [Fact]
        public async Task 一定要送出_think_false()
        {
            // 這不是效能微調，是可用性的前提：實測開著思考 528 tokens／43.4 秒，
            // 關掉 58 tokens／4.7 秒——同一個問題、同樣正確的答案，9 倍差距。
            var handler = new FakeHandler(_ => Json(ChatOk("答案")));
            var client = Build(handler);

            await client.ChatAsync("系統提示", "使用者提問");

            using var sent = JsonDocument.Parse(handler.LastBody!);
            Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
            Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("/api/chat", handler.LastPath);
        }

        [Fact]
        public async Task 生成長度上限預設要夠寬鬆()
        {
            // 設太小的後果不是「答案變短」，而是「答案完全消失」（見下一條測試）。
            var handler = new FakeHandler(_ => Json(ChatOk("答案")));
            var client = Build(handler);

            await client.ChatAsync("系統提示", "使用者提問");

            using var sent = JsonDocument.Parse(handler.LastBody!);
            Assert.Equal(OllamaClient.DefaultNumPredict,
                sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        }

        [Fact]
        public async Task 被長度截斷一律視為失敗()
        {
            // 實測形狀：生成滿 num_predict 個 token、done_reason="length"，
            // 而 response 與 thinking 兩個欄位**都是空的**。HTTP 200、done:true，
            // 唯獨沒有答案。放行的話畫面就是一片空白。
            var handler = new FakeHandler(_ => Json(ChatOk(string.Empty, doneReason: "length")));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains(OllamaClient.NumPredictKey, ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\n\t ")]
        public async Task 空回覆一律視為失敗(string content)
        {
            // done_reason=stop 但內容是空的：token 全部進了思考過程時會這樣。
            var handler = new FakeHandler(_ => Json(ChatOk(content)));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains("think", ex.Message);
        }

        [Fact]
        public async Task 成功時完整帶回計量()
        {
            var handler = new FakeHandler(_ => Json(ChatOk("  銷售排名報表可依客戶彙總。  ")));
            var client = Build(handler);

            var answer = await client.ChatAsync("系統", "提問");

            Assert.Equal("銷售排名報表可依客戶彙總。", answer.Text);   // 前後空白要修掉
            Assert.Equal("gemma4:latest", answer.Model);
            Assert.Equal("stop", answer.DoneReason);
            Assert.Equal(412, answer.PromptTokens);
            Assert.Equal(58, answer.CompletionTokens);

            // total_duration 是奈秒。單位換算弄錯不會報錯，只會讓效能數字差 100 倍。
            Assert.Equal(4.1, answer.Duration.TotalSeconds, precision: 3);
        }

        // ── 錯誤訊息要看得懂且可行動 ────────────────────────────────────────

        [Fact]
        public async Task 未設定生成模型時要指名組態鍵()
        {
            var handler = new FakeHandler(_ => Json(ChatOk("答案")));
            var client = Build(handler, chatModel: "");

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains(OllamaClient.ChatModelKey, ex.Message);
            Assert.Equal(0, handler.Calls);   // 連線都不該發生
        }

        [Fact]
        public async Task 模型不存在要提示先拉模型()
        {
            // Ollama 的原文是英文的 model "x" not found, try pulling it first——
            // 直接丟給使用者等於什麼都沒說。
            var handler = new FakeHandler(_ => Json(
                """{"error":"model \"gemma4\" not found, try pulling it first"}""", HttpStatusCode.NotFound));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains("ollama pull", ex.Message);
            Assert.Contains("gemma4", ex.Message);
        }

        [Fact]
        public async Task 非_JSON_的錯誤回應也要能處理()
        {
            // 反向代理擋下來時回的是 HTML 錯誤頁，硬解析會拋 JsonException，
            // 於是真正的原因（502）被一個解析錯誤蓋掉。
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
            });
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains("502", ex.Message);
        }

        [Fact]
        public async Task 連不上時要指名組態鍵與通道()
        {
            var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains(OllamaClient.UrlKey, ex.Message);
        }

        [Fact]
        public async Task 逾時要說出逾時秒數而不是被誤報成取消()
        {
            // HttpClient 逾時在 .NET 也是 TaskCanceledException，與呼叫端取消同型別。
            // 分不出來的後果是：純 CPU 推論太慢時，log 顯示「使用者取消了」。
            var handler = new FakeHandler(_ => throw new TaskCanceledException("timeout"));
            var client = Build(handler, extra: (OllamaClient.TimeoutSecondsKey, "45"));

            var ex = await Assert.ThrowsAsync<OllamaException>(() => client.ChatAsync("系統", "提問"));
            Assert.Contains("45", ex.Message);
        }

        [Fact]
        public async Task 呼叫端取消時不得轉成推論失敗()
        {
            // 反方向：使用者關掉頁面不是推論服務壞了，不該記成錯誤也不該顯示錯誤訊息。
            var handler = new FakeHandler(_ => throw new TaskCanceledException("cancelled"));
            var client = Build(handler);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.ChatAsync("系統", "提問", cts.Token));
        }

        // ── 嵌入 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task 空輸入不打_HTTP()
        {
            var handler = new FakeHandler(_ => Json("""{"embeddings":[]}"""));
            var client = Build(handler);

            var result = await client.EmbedAsync(Array.Empty<string>(), EmbeddingKind.Passage);

            Assert.Empty(result);
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task 嵌入結果依序對應輸入()
        {
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[1,0,0],[0,1,0]]}"""));
            var client = Build(handler);

            var result = await client.EmbedAsync(new[] { "第一段", "第二段" }, EmbeddingKind.Passage);

            Assert.Equal(2, result.Count);
            Assert.Equal(new[] { 1f, 0f, 0f }, result[0]);
            Assert.Equal(new[] { 0f, 1f, 0f }, result[1]);
            Assert.Equal("/api/embed", handler.LastPath);
        }

        [Fact]
        public async Task 向量數量與輸入不符必須整批失敗()
        {
            // 這是本層最惡劣的失敗形狀：不報錯，只讓向量與 chunk **錯位**，
            // 於是每則答覆都引用到別的頁面，而系統看起來完全正常。
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[1,0,0]]}"""));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(
                () => client.EmbedAsync(new[] { "第一段", "第二段" }, EmbeddingKind.Passage));
            Assert.Contains("錯位", ex.Message);
        }

        [Fact]
        public async Task 維度不一致必須整批失敗()
        {
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[1,0,0],[0,1]]}"""));
            var client = Build(handler);

            await Assert.ThrowsAsync<OllamaException>(
                () => client.EmbedAsync(new[] { "a", "b" }, EmbeddingKind.Passage));
        }

        [Fact]
        public async Task 零向量必須失敗()
        {
            // 零向量的餘弦是 0/0。放行的話錯誤會在檢索期以 NaN 出現，排序不確定且看不出成因。
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[0,0,0]]}"""));
            var client = Build(handler);

            var ex = await Assert.ThrowsAsync<OllamaException>(
                () => client.EmbedAsync(new[] { "a" }, EmbeddingKind.Passage));
            Assert.Contains("零向量", ex.Message);
        }

        [Fact]
        public async Task 未設定嵌入模型時要指名組態鍵()
        {
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[1,0,0]]}"""));
            var client = Build(handler, embeddingModel: "");

            var ex = await Assert.ThrowsAsync<OllamaException>(
                () => client.EmbedAsync(new[] { "a" }, EmbeddingKind.Passage));
            Assert.Contains(OllamaClient.EmbeddingModelKey, ex.Message);
            Assert.Equal(0, handler.Calls);
        }

        // ── 前綴：§10.7 實作要求 3 的對照測試 ───────────────────────────────
        //
        // 這幾條測試存在的唯一目的，是讓「前綴被拿掉」變成紅燈而不是無聲降級。
        // 漏加前綴不會有任何錯誤，只會讓檢索品質默默下降——那正是最難發現的一種壞法。

        [Fact]
        public void 查詢與內容必須用不同前綴()
        {
            const string text = "銷售排名報表可依客戶彙總";

            var asQuery = OllamaClient.ApplyPrefix("embeddinggemma", EmbeddingKind.Query, text);
            var asPassage = OllamaClient.ApplyPrefix("embeddinggemma", EmbeddingKind.Passage, text);

            Assert.NotEqual(asQuery, asPassage);
            Assert.NotEqual(text, asQuery);      // 兩者都必須真的有前綴，
            Assert.NotEqual(text, asPassage);    // 否則「不同」可以靠其中一個沒加來滿足
            Assert.EndsWith(text, asQuery);
            Assert.EndsWith(text, asPassage);
        }

        [Fact]
        public void 未查證的模型一律不加前綴()
        {
            // 保守方向：對「本來就不需要前綴」的模型是正確的，
            // 對「需要但還沒查證」的模型是品質略降而不是壞掉。
            const string text = "任意內容";

            Assert.Equal(text, OllamaClient.ApplyPrefix("multilingual-e5-large", EmbeddingKind.Query, text));
            Assert.Equal(text, OllamaClient.ApplyPrefix("mxbai-embed-large", EmbeddingKind.Passage, text));
        }

        [Fact]
        public void 查證過不需要前綴_與_沒查證所以不加_必須分得開()
        {
            // 兩者的字串結果相同（都是原文），意義卻相反：
            // granite 是**模型卡明示不需要**，e5 是**還沒查證**。
            // 這條測試守的不是輸出，是「granite 有自己的分支」——
            // 少了它，日後有人整理 switch 時會把 granite 併進 `_`，
            // 而那等於把一個已查證的結論降級成待查證，且完全看不出來。
            const string text = "內容";
            Assert.Equal(text, OllamaClient.ApplyPrefix("granite-embedding:278m", EmbeddingKind.Query, text));
            Assert.Equal(text, OllamaClient.ApplyPrefix("granite-embedding:278m", EmbeddingKind.Passage, text));
        }

        [Theory]
        // Snowflake Arctic Embed 2.0：只有查詢加前綴，文件不加。
        [InlineData("snowflake-arctic-embed2", "query: ", "")]
        // Nomic Embed v2 MoE：查詢與文件各有前綴。
        [InlineData("nomic-embed-text-v2-moe", "search_query: ", "search_document: ")]
        public void 候選模型的前綴依各自模型卡(string model, string queryPrefix, string passagePrefix)
        {
            const string text = "銷售排名";

            Assert.Equal(queryPrefix + text, OllamaClient.ApplyPrefix(model, EmbeddingKind.Query, text));
            Assert.Equal(passagePrefix + text, OllamaClient.ApplyPrefix(model, EmbeddingKind.Passage, text));
        }

        [Fact]
        public void 前綴要忽略標籤()
        {
            // 組態寫 embeddinggemma:300m 與寫 embeddinggemma 是同一顆模型，
            // 前綴卻只認得其中一種的話，索引期與查詢期會用不同前綴——正是本節要防的事。
            const string text = "內容";

            Assert.Equal(
                OllamaClient.ApplyPrefix("embeddinggemma", EmbeddingKind.Query, text),
                OllamaClient.ApplyPrefix("embeddinggemma:300m", EmbeddingKind.Query, text));
        }

        [Fact]
        public async Task 送出前必須真的套用前綴()
        {
            // 上面三條守的是 ApplyPrefix 本身。這一條守的是**它真的被呼叫**——
            // 少了這條，把 EmbedAsync 裡的 ApplyPrefix 拿掉仍然全綠。
            var handler = new FakeHandler(_ => Json("""{"embeddings":[[1,0,0]]}"""));
            var client = Build(handler);

            await client.EmbedAsync(new[] { "銷售排名" }, EmbeddingKind.Query);
            var asQuery = JsonDocument.Parse(handler.LastBody!)
                .RootElement.GetProperty("input")[0].GetString();

            await client.EmbedAsync(new[] { "銷售排名" }, EmbeddingKind.Passage);
            var asPassage = JsonDocument.Parse(handler.LastBody!)
                .RootElement.GetProperty("input")[0].GetString();

            Assert.Equal(OllamaClient.ApplyPrefix("embeddinggemma", EmbeddingKind.Query, "銷售排名"), asQuery);
            Assert.NotEqual(asQuery, asPassage);
        }

        // ── 健康檢查：三種故障要分得開 ──────────────────────────────────────

        [Fact]
        public async Task 服務連不上時回報不可達而不是拋例外()
        {
            // 健康檢查拋例外就失去意義了——它存在的目的正是「在不炸掉的前提下說明現況」。
            var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
            var client = Build(handler);

            var health = await client.CheckAsync();

            Assert.False(health.Reachable);
            Assert.Contains(OllamaClient.UrlKey, health.Detail);
        }

        [Fact]
        public async Task 模型未下載時要分別指出缺哪一個()
        {
            var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath == "/api/version"
                ? Json("""{"version":"0.32.5"}""")
                : Json("""{"models":[{"name":"gemma4:latest"}]}"""));
            var client = Build(handler);

            var health = await client.CheckAsync();

            Assert.True(health.Reachable);
            Assert.True(health.ChatModelPresent);          // gemma4 ↔ gemma4:latest 是同一顆
            Assert.False(health.EmbeddingModelPresent);
            Assert.Contains("ollama pull embeddinggemma", health.Detail);
        }

        [Fact]
        public async Task 兩個模型都就緒時要明講就緒()
        {
            var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath == "/api/version"
                ? Json("""{"version":"0.32.5"}""")
                : Json("""{"models":[{"name":"gemma4:latest"},{"name":"embeddinggemma:latest"}]}"""));
            var client = Build(handler);

            var health = await client.CheckAsync();

            Assert.True(health.ChatModelPresent);
            Assert.True(health.EmbeddingModelPresent);
            Assert.Equal("0.32.5", health.Version);
        }

        [Theory]
        [InlineData("gemma4", "gemma4:latest", true)]
        [InlineData("gemma4", "gemma4", true)]
        [InlineData("GEMMA4", "gemma4:latest", true)]
        [InlineData("gemma4:9b", "gemma4:latest", false)]   // 明確指定標籤就不該被別的標籤滿足
        [InlineData("gemma4", "gemma4-tools:latest", false)]
        public void 模型名稱比對要處理_latest_標籤(string configured, string present, bool expected) =>
            Assert.Equal(expected, OllamaClient.ModelMatches(configured, present));

        // ── 組態 ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("不是網址")]
        public void 位址沒填或填壞都退回預設而不是崩潰(string? raw)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [OllamaClient.UrlKey] = raw })
                .Build();

            Assert.Equal(new Uri(OllamaClient.DefaultUrl), OllamaClient.ResolveBaseUrl(configuration));
        }

        [Fact]
        public void 位址填對就用填的()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [OllamaClient.UrlKey] = "  http://gpu-host:11434  " })
                .Build();

            Assert.Equal(new Uri("http://gpu-host:11434"), OllamaClient.ResolveBaseUrl(configuration));
        }
    }
}
