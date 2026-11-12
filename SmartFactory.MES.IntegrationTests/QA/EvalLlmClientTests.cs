using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 外部評測 client 的請求組裝與回應判讀（§17.11.8-七 路徑一）。
    ///
    /// <para><b>不打網路</b>：這裡量的是「同一份提示詞有沒有被送成同一件事」與
    /// 「失敗形狀有沒有被當成失敗」。實跑要金鑰與對外連線，而
    /// 開發沙箱兩者都沒有——若把驗證全押在實跑上，這段程式碼在需要用它的那一天
    /// 才第一次被執行。</para>
    /// </summary>
    public class EvalLlmClientTests
    {
        private static readonly TimeSpan Elapsed = TimeSpan.FromSeconds(3);

        // ── Anthropic：請求 ──────────────────────────────────────────────

        [Fact]
        public void Anthropic_system提示詞放頂層_不混進messages()
        {
            var body = JsonNode.Parse(
                new AnthropicEvalClient("k", "claude-haiku-4-5", 1024, "")
                    .BuildBody("你是手冊助理", "怎麼改交期？"))!.AsObject();

            Assert.Equal("你是手冊助理", body["system"]!.GetValue<string>());

            // messages 只有使用者那一則：把 system 放進 messages 會被當成使用者發言，
            // 而 §10.8.2 之二量過提示詞位置會改變拒答行為——那會讓兩輪不可比。
            var messages = body["messages"]!.AsArray();
            Assert.Single(messages);
            Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
            Assert.Equal("怎麼改交期？", messages[0]!["content"]!.GetValue<string>());
        }

        [Fact]
        public void Anthropic_extra可併入且同名鍵以extra為準()
        {
            // 用途正是覆寫預設值：關閉思考的寫法各家不同、而且會變（§17.11.8-五），
            // 寫死在程式碼裡過期時的症狀是 400 或悄悄多花十倍 token。
            var body = JsonNode.Parse(
                new AnthropicEvalClient("k", "claude-opus-5", 1024,
                        """{"thinking":{"type":"disabled"},"max_tokens":4096}""")
                    .BuildBody("s", "u"))!.AsObject();

            Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
            Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
            Assert.Equal("claude-opus-5", body["model"]!.GetValue<string>());
        }

        [Fact]
        public void Anthropic_extra不是JSON物件就明確報錯()
        {
            var client = new AnthropicEvalClient("k", "m", 1024, "not json");
            var ex = Assert.Throws<OllamaException>(() => client.BuildBody("s", "u"));
            Assert.Contains(EvalLlmClients.ExtraJsonVar, ex.Message);
        }

        // ── Anthropic：回應 ──────────────────────────────────────────────

        [Fact]
        public void Anthropic_只取text區塊_thinking不算答案()
        {
            // thinking 混進答案會讓引文解析看到不存在的頁面代號，
            // 於是幻覺引文率變成雜訊——而那是四項指標裡唯一完全可自動判定的一項。
            var json = """
            {"model":"claude-haiku-4-5-20251001","stop_reason":"end_turn",
             "content":[{"type":"thinking","thinking":"先看 ord-01 再看 ord-02"},
                        {"type":"text","text":"改交期請至訂單維護。\n來源：ord-01"}],
             "usage":{"input_tokens":3120,"output_tokens":48}}
            """;

            var a = AnthropicEvalClient.ParseAnswer(json, "claude-haiku-4-5", Elapsed);

            Assert.DoesNotContain("先看 ord-01", a.Text);
            Assert.Contains("來源：ord-01", a.Text);
            Assert.Equal("claude-haiku-4-5-20251001", a.Model);   // 實際回覆的模型，不是要求的
            Assert.Equal(3120, a.PromptTokens);
            Assert.Equal(48, a.CompletionTokens);
            Assert.Equal("end_turn", a.DoneReason);
        }

        [Fact]
        public void Anthropic_多個text區塊要接起來()
        {
            var json = """
            {"stop_reason":"end_turn",
             "content":[{"type":"text","text":"第一段。"},{"type":"text","text":"第二段。"}],
             "usage":{"input_tokens":1,"output_tokens":2}}
            """;
            Assert.Equal("第一段。第二段。", AnthropicEvalClient.ParseAnswer(json, "m", Elapsed).Text);
        }

        [Fact]
        public void Anthropic_被max_tokens截斷算失敗()
        {
            // 與本地對 done_reason=="length" 的處置一致（§10.8.1 第 2 點）。
            // 兩邊判準不同的話，「被截斷」在本地算失敗、在雲端算成功——
            // 而截斷的答案通常只是少了最後一行來源，看起來完全正常。
            var json = """
            {"stop_reason":"max_tokens","content":[{"type":"text","text":"改交期請至"}],
             "usage":{"input_tokens":10,"output_tokens":1024}}
            """;
            var ex = Assert.Throws<OllamaException>(() => AnthropicEvalClient.ParseAnswer(json, "m", Elapsed));
            Assert.Contains("max_tokens", ex.Message);
        }

        [Fact]
        public void Anthropic_只有thinking沒有text算失敗()
        {
            // 思考吃光額度＝空答案，與本地 num_predict 設太小是同一種形狀。
            var json = """
            {"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"嗯"}],
             "usage":{"input_tokens":10,"output_tokens":900}}
            """;
            Assert.Throws<OllamaException>(() => AnthropicEvalClient.ParseAnswer(json, "m", Elapsed));
        }

        [Fact]
        public void Anthropic_API錯誤本體要帶出原訊息()
        {
            var json = """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens too large"}}""";
            var ex = Assert.Throws<OllamaException>(() => AnthropicEvalClient.ParseAnswer(json, "m", Elapsed));
            Assert.Contains("max_tokens too large", ex.Message);
        }

        // ── OpenAI 相容（B 類自架）───────────────────────────────────────

        [Fact]
        public void OpenAi_端點組法不受尾斜線影響()
        {
            // vLLM／LM Studio 的位址常見兩種寫法，組出兩個不同端點的話
            // 症狀是 404，而 404 看起來像「服務沒開」。
            foreach (var baseUrl in new[] { "http://10.0.0.20:8000", "http://10.0.0.20:8000/" })
            {
                var body = JsonNode.Parse(new OpenAiCompatibleEvalClient(baseUrl, null, "qwen", 1024, "")
                    .BuildBody("s", "u"))!.AsObject();
                Assert.Equal("qwen", body["model"]!.GetValue<string>());
            }
        }

        [Fact]
        public void OpenAi_system與user各一則()
        {
            var body = JsonNode.Parse(new OpenAiCompatibleEvalClient("http://h:1", null, "m", 512, "")
                .BuildBody("你是手冊助理", "怎麼改交期？"))!.AsObject();

            var messages = body["messages"]!.AsArray();
            Assert.Equal(2, messages.Count);
            Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
            Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
            Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        }

        [Fact]
        public void OpenAi_正常回應()
        {
            var json = """
            {"model":"qwen2.5:14b","choices":[{"finish_reason":"stop",
             "message":{"role":"assistant","content":"改交期請至訂單維護。"}}],
             "usage":{"prompt_tokens":2900,"completion_tokens":30}}
            """;
            var a = OpenAiCompatibleEvalClient.ParseAnswer(json, "m", Elapsed);
            Assert.Equal("改交期請至訂單維護。", a.Text);
            Assert.Equal(2900, a.PromptTokens);
            Assert.Equal(30, a.CompletionTokens);
        }

        [Fact]
        public void OpenAi_finish_reason_length算失敗()
        {
            var json = """
            {"choices":[{"finish_reason":"length","message":{"content":"改交期請至"}}],
             "usage":{"prompt_tokens":10,"completion_tokens":1024}}
            """;
            Assert.Throws<OllamaException>(() => OpenAiCompatibleEvalClient.ParseAnswer(json, "m", Elapsed));
        }

        [Fact]
        public void OpenAi_內容為空算失敗()
        {
            var json = """{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""";
            Assert.Throws<OllamaException>(() => OpenAiCompatibleEvalClient.ParseAnswer(json, "m", Elapsed));
        }

        [Fact]
        public void 兩種後端的回應都不是合法JSON時要說清楚()
        {
            // 反向代理回的 HTML 錯誤頁是最常見的形狀，而「JSON 解析失敗」
            // 比「連不上」更難查——訊息裡要留下原因。
            Assert.Throws<OllamaException>(() => AnthropicEvalClient.ParseAnswer("<html>502</html>", "m", Elapsed));
            Assert.Throws<OllamaException>(() => OpenAiCompatibleEvalClient.ParseAnswer("<html>502</html>", "m", Elapsed));
        }

        // ── 後端選擇 ─────────────────────────────────────────────────────

        [Fact]
        public void 未設定provider時不碰環境變數也不需要金鑰()
        {
            // 預設必須完全維持原行為：否則所有既有的評測都會突然要求金鑰。
            Assert.Null(Environment.GetEnvironmentVariable(EvalLlmClients.ProviderVar));
        }

        [Fact]
        public void anthropic缺金鑰時_錯誤訊息指名環境變數()
        {
            var prior = Environment.GetEnvironmentVariable(EvalLlmClients.ProviderVar);
            var priorKey = Environment.GetEnvironmentVariable(EvalLlmClients.KeyVar);
            try
            {
                Environment.SetEnvironmentVariable(EvalLlmClients.ProviderVar, "anthropic");
                Environment.SetEnvironmentVariable(EvalLlmClients.KeyVar, null);

                var ex = Assert.Throws<InvalidOperationException>(() => EvalLlmClients.Resolve(null!));
                Assert.Contains(EvalLlmClients.KeyVar, ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable(EvalLlmClients.ProviderVar, prior);
                Environment.SetEnvironmentVariable(EvalLlmClients.KeyVar, priorKey);
            }
        }

        [Fact]
        public void 不認得的provider要明確報錯而不是靜默退回本機()
        {
            // 靜默退回的話，一整輪雲端評測會在本機跑完，而報表上的數字看起來很正常。
            var prior = Environment.GetEnvironmentVariable(EvalLlmClients.ProviderVar);
            try
            {
                Environment.SetEnvironmentVariable(EvalLlmClients.ProviderVar, "gemini");
                var ex = Assert.Throws<InvalidOperationException>(() => EvalLlmClients.Resolve(null!));
                Assert.Contains("gemini", ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable(EvalLlmClients.ProviderVar, prior);
            }
        }
    }
}
