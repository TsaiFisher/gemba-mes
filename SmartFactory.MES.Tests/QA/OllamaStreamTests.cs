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
    /// <see cref="OllamaClient.ChatStreamAsync"/> 的守門測試（規格 §11.2 C4）。
    ///
    /// <para><b>這一類程式最容易的壞法是「看起來有在串流」</b>：把整段生成收完再一次吐出來，
    /// 畫面照樣顯示得出答案，測試也照樣綠，只有使用者知道自己等了五十秒。
    /// 所以這裡幾乎每一支都在量**時序**（第幾段在什麼時候拿到）或**形狀**
    /// （最後那一項有沒有帶 <c>Final</c>），而不是只量「最後拿到的字串對不對」。</para>
    ///
    /// <para>另一半是**非串流版已經守住、串流版很容易忘記重做**的三道可用性檢查：
    /// <c>think:false</c>、截斷即失敗、空內容即失敗。三者的共同點都是 HTTP 200 卻沒有答案。</para>
    /// </summary>
    public class OllamaStreamTests
    {
        private const string Url = "http://127.0.0.1:11435";

        /// <summary>
        /// 逐行送出 NDJSON 的假端點。**可以在送完第一行後停住，等測試放行才送其餘**——沒有這個，
        /// 「有沒有邊收邊吐」根本量不出來（一次全給的實作也會通過）。
        /// 用握手而不是用延遲量時序：全量平行跑時執行緒池會被占滿，接收端可能晚好幾百毫秒才被排到，
        /// 那時所有訊框早已在緩衝區裡，計時的寫法因此偶發紅燈（11/2 CI 實測）。
        /// </summary>
        private sealed class StreamHandler : HttpMessageHandler
        {
            /// <summary>收完再吐的實作永遠等不到放行，停這麼久就自己送完，讓測試結束並判定失敗。</summary>
            private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(5);

            private readonly IReadOnlyList<string> _lines;
            private readonly Task? _holdAfterFirst;

            public StreamHandler(IReadOnlyList<string> lines, Task? holdAfterFirst = null)
            {
                _lines = lines;
                _holdAfterFirst = holdAfterFirst;
            }

            public string? LastBody { get; private set; }
            public string? LastPath { get; private set; }

            /// <summary>第一行之後的停頓是等到逾時才結束（而不是被放行）。</summary>
            public bool HoldTimedOut { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastPath = request.RequestUri?.AbsolutePath;
                LastBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                var pipe = new System.IO.Pipelines.Pipe();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < _lines.Count; i++)
                        {
                            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(_lines[i] + "\n"), cancellationToken);
                            await pipe.Writer.FlushAsync(cancellationToken);
                            if (i == 0 && _holdAfterFirst is not null)
                            {
                                var done = await Task.WhenAny(_holdAfterFirst, Task.Delay(HoldTimeout, cancellationToken));
                                HoldTimedOut = done != _holdAfterFirst;
                            }
                        }
                        await pipe.Writer.CompleteAsync();
                    }
                    catch (Exception ex)
                    {
                        await pipe.Writer.CompleteAsync(ex);
                    }
                }, cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(pipe.Reader.AsStream()),
                };
            }
        }

        private sealed class FakeFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public FakeFactory(HttpMessageHandler handler) => _handler = handler;

            public HttpClient CreateClient(string name) =>
                new(_handler, disposeHandler: false) { BaseAddress = new Uri(Url) };
        }

        private static OllamaClient Build(HttpMessageHandler handler, string chatModel = "gemma4",
                                          IInferenceGate? gate = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OllamaClient.UrlKey] = Url,
                [OllamaClient.ChatModelKey] = chatModel,
                [OllamaClient.EmbeddingModelKey] = "embeddinggemma",
            }).Build();

            return new OllamaClient(new FakeFactory(handler), configuration,
                                    NullLogger<OllamaClient>.Instance, gate);
        }

        /// <summary>一段文字的訊框（Ollama 串流時 done 為 false、message.content 是增量）。</summary>
        private static string Chunk(string content) =>
            $$"""{"model":"gemma4:latest","message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"done":false}""";

        /// <summary>結束訊框（content 為空，帶統計與 done_reason）。</summary>
        private static string Tail(string doneReason = "stop") =>
            $$"""{"model":"gemma4:latest","message":{"role":"assistant","content":""},"done":true,"done_reason":"{{doneReason}}","prompt_eval_count":412,"eval_count":58,"total_duration":4100000000}""";

        // ── 形狀 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task 最後一項帶Final其餘只有文字()
        {
            var handler = new StreamHandler(new[] { Chunk("工"), Chunk("單"), Tail() });
            var deltas = new List<LlmDelta>();

            await foreach (var d in Build(handler).ChatStreamAsync("系統", "提問"))
                deltas.Add(d);

            Assert.Equal(3, deltas.Count);
            Assert.All(deltas.Take(2), d => Assert.Null(d.Final));
            Assert.Equal(new[] { "工", "單" }, deltas.Take(2).Select(d => d.Text));

            // 最後一項是「結束標記」而不是內容：Text 空、Final 帶累積全文。
            Assert.Equal(string.Empty, deltas[^1].Text);
            var final = deltas[^1].Final;
            Assert.NotNull(final);
            Assert.Equal("工單", final!.Value.Text);
            Assert.Equal("gemma4:latest", final.Value.Model);
            Assert.Equal(58, final.Value.CompletionTokens);
        }

        [Fact]
        public async Task 真的是邊收邊吐不是收完再吐()
        {
            // 這一支是本檔的核心。若實作把整段讀完才開始 yield，第一段的到達時間
            // 會等於全部訊框的總延遲——串流的價值全部消失，而其他測試一支都不會紅。
            // 假端點送出第一段後就停住，直到這裡拿到第一段才放行其餘訊框。
            // 邊收邊吐的實作一定拿得到（不論排程多慢）；收完再吐的實作要等停頓逾時才拿到。
            var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new StreamHandler(new[] { Chunk("甲"), Chunk("乙"), Chunk("丙"), Tail() }, firstSeen.Task);

            var texts = new List<string>();
            var gotFinal = false;

            await foreach (var d in Build(handler).ChatStreamAsync("系統", "提問"))
            {
                if (d.Final is not null) { gotFinal = true; break; }
                texts.Add(d.Text);
                firstSeen.TrySetResult();
            }

            Assert.True(gotFinal);
            Assert.Equal(new[] { "甲", "乙", "丙" }, texts);
            Assert.False(handler.HoldTimedOut,
                "假端點送出第一段後一直等不到消費端拿到它——實作很可能是把整段讀完才開始吐。");
        }

        [Fact]
        public async Task 空白的增量不佔一個項次()
        {
            // Ollama 會送出 content 為空的中間訊框。把它們也 yield 出去，
            // 畫面端的「第一個 token 到了」就會提早觸發，於是計時停掉但畫面還是空的。
            var handler = new StreamHandler(new[] { Chunk(""), Chunk("答"), Chunk(""), Tail() });
            var texts = new List<string>();

            await foreach (var d in Build(handler).ChatStreamAsync("系統", "提問"))
                if (d.Final is null) texts.Add(d.Text);

            Assert.Equal(new[] { "答" }, texts);
        }

        // ── 與非串流版對齊的三道可用性檢查 ──────────────────────────────────

        [Fact]
        public async Task 一定要送出_think_false_且_stream_true()
        {
            var handler = new StreamHandler(new[] { Chunk("答"), Tail() });

            await foreach (var _ in Build(handler).ChatStreamAsync("系統", "提問")) { }

            using var sent = JsonDocument.Parse(handler.LastBody!);
            Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
            Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("/api/chat", handler.LastPath);
        }

        [Fact]
        public async Task 被長度截斷要當成失敗()
        {
            // 已經吐出去的字不會收回，但**不能給出 Final**——否則呼叫端會把
            // 半截答案當成完整答案存下來、顯示出來，而且沒有任何錯誤訊息。
            var handler = new StreamHandler(new[] { Chunk("答案被切"), Tail("length") });

            var got = new List<string>();
            var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
            {
                await foreach (var d in Build(handler).ChatStreamAsync("系統", "提問"))
                {
                    if (d.Final is not null) Assert.Fail("被截斷時不該給出 Final。");
                    got.Add(d.Text);
                }
            });

            Assert.Contains("截斷", ex.Message);
            Assert.Equal(new[] { "答案被切" }, got);   // 前面吐出去的仍然算數
        }

        [Fact]
        public async Task 整段都是空的要當成失敗()
        {
            var handler = new StreamHandler(new[] { Chunk(""), Tail() });

            var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
            {
                await foreach (var _ in Build(handler).ChatStreamAsync("系統", "提問")) { }
            });

            Assert.Contains("空內容", ex.Message);
        }

        [Fact]
        public async Task 沒收到結束訊框要當成失敗()
        {
            // 連線在中途被切斷（反向代理逾時、Ollama 被重啟）。列舉會自然結束，
            // 「跑完了」與「成功」在這裡不是同一件事——這正是 Final 存在的理由。
            var handler = new StreamHandler(new[] { Chunk("半"), Chunk("截") });

            var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
            {
                await foreach (var _ in Build(handler).ChatStreamAsync("系統", "提問")) { }
            });

            Assert.Contains("中斷", ex.Message);
        }

        [Fact]
        public async Task 未設定生成模型要當場說清楚()
        {
            var handler = new StreamHandler(new[] { Tail() });

            var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
            {
                await foreach (var _ in Build(handler, chatModel: "").ChatStreamAsync("系統", "提問")) { }
            });

            Assert.Contains("未設定生成模型", ex.Message);
        }

        [Fact]
        public async Task 串流中出現非JSON的一行要當場停()
        {
            // 反向代理的 HTML 錯誤頁最常見。跳過它會讓答案默默少一段。
            var handler = new StreamHandler(new[] { Chunk("答"), "<html>502 Bad Gateway</html>", Tail() });

            var ex = await Assert.ThrowsAsync<OllamaException>(async () =>
            {
                await foreach (var _ in Build(handler).ChatStreamAsync("系統", "提問")) { }
            });

            Assert.Contains("無法解析", ex.Message);
        }

        // ── 限流 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task 名額涵蓋整個列舉並在結束後歸還()
        {
            // 名額若只涵蓋「送出」，一題還在吐字時下一題就會被放行，
            // 於是 C1 訂的「同時只准一題」形同虛設，而且完全沒有症狀——
            // 只是兩個人一起把同一顆 CPU 等得更久。
            var gate = new RecordingGate();
            var handler = new StreamHandler(new[] { Chunk("甲"), Chunk("乙"), Tail() });

            await foreach (var d in Build(handler, gate: gate).ChatStreamAsync("系統", "提問"))
            {
                if (d.Final is null)
                    Assert.True(gate.Held, "還在吐字，名額不該已經還回去。");
            }

            Assert.Equal(1, gate.Entered);
            Assert.False(gate.Held);
        }

        [Fact]
        public async Task 中途放棄列舉也要歸還名額()
        {
            // break 出去（使用者關掉分頁、上層取消）時若不歸還，閘門會慢慢自我鎖死，
            // 症狀是「過一陣子之後大家都排不進去」，且沒有任何錯誤訊息。
            var gate = new RecordingGate();
            var handler = new StreamHandler(new[] { Chunk("甲"), Chunk("乙"), Tail() });

            await foreach (var _ in Build(handler, gate: gate).ChatStreamAsync("系統", "提問"))
                break;

            Assert.False(gate.Held);
        }

        private sealed class RecordingGate : IInferenceGate
        {
            public int Entered { get; private set; }
            public bool Held { get; private set; }

            public Task<IDisposable> EnterAsync(InferenceLane lane, CancellationToken ct = default)
            {
                Entered++;
                Held = true;
                return Task.FromResult<IDisposable>(new Lease(this));
            }

            private sealed class Lease : IDisposable
            {
                private readonly RecordingGate _gate;
                public Lease(RecordingGate gate) => _gate = gate;
                public void Dispose() => _gate.Held = false;
            }
        }
    }
}
