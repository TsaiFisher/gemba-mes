using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// S-7 真的問一題，走完整條 HTTP 路徑（定案 #47 的驗收）。
    ///
    /// <para><b>為什麼 <c>QaAssistantScreenTests</c> 不夠。</b>那一支刻意只涵蓋
    /// 「不需要推論服務就成立」的一半，於是引文這一段從來沒有被端到端驗過：
    /// <c>QaCitationPresenter</c> 有自己的單元測試，但它吃的是**手工餵的** <c>given</c>；
    /// 真實路徑上 <c>given</c> 來自 <c>SelectWithinBudget</c>、<c>DocUrl</c> 來自匯入器、
    /// 引文字串來自模型。這三者任何一個對不上，單元測試全綠而畫面上是壞的。</para>
    ///
    /// <para><b>最該守的是 <c>kind</c> 的序列化形狀。</b>本專案沒有全域註冊字串列舉轉換器
    /// （見 <c>QaCitationKind</c> 的類別註解），少了那個屬性就會序列化成 <c>0/1/2</c>，
    /// 而畫面比對的是 <c>c.kind === 'Rule'</c>——數字永遠不相等，於是**每一則引文都掉進
    /// 「無法查證」那一支**，正常的手冊連結會被整批標成幻覺。沒有例外、沒有紅燈。
    /// 所以這裡斷言的是**原始 JSON 的字面**，不是反序列化後的列舉：
    /// 用型別化的 DTO 讀回來會自動把 <c>0</c> 讀成 <c>Manual</c>，正好把要抓的東西抹平。</para>
    ///
    /// <para><b>不設品質門檻</b>（同 <c>ManualHardBenchmarkTests</c>）。這裡不管模型答得好不好，
    /// 只管「引文的形狀對不對」——好不好由那幾支量測負責，而且它們刻意不設門檻。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class QaAssistantLiveAskTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const string MenuKey = "QaAssistant";

        /// <summary>
        /// 這一題是**選出來的**，不是隨手挑的：它的正解只存在於擷取物
        /// （<c>rules/currency-d151520a</c>「採購付款用買入匯率」），
        /// 手冊 <c>bas/currency.html</c> 沒有寫買入／賣出的選用時機
        /// （<c>ManualQuestionBank.RuleGrounded</c> 的類別註解查證過）。
        /// 手冊也答得出來的題目會讓這支測試在「擷取物一則都沒進索引」時照樣通過。
        /// </summary>
        private const string RuleQuestion = "採購付款要用買入匯率還是賣出匯率";

        private readonly AuthedScheduleFactory _factory;
        private readonly ITestOutputHelper _out;

        public QaAssistantLiveAskTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _out = output;
        }

        public async Task InitializeAsync()
        {
            await SetAsync(read: true, update: true);

            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();

            // 受測應用程式的 manifest 目錄刻意指到不存在的路徑（見 TestWebApplicationFactory），
            // 所以擷取物要由測試自己匯入——這也是 ManualExtractImpactBenchmarkTests 的做法。
            var imported = await ImportRepoManifestsAsync();
            Assert.True(imported.EntriesApproved > 0,
                $"匯入 {imported.EntriesTotal} 段但已核可 0 段——檢索只吃 Approved 的列，" +
                "這一輪不可能出現任何 rules/* 引文，測到的會是「沒有擷取物」的行為。");

            var (_, pending) = await scope.ServiceProvider
                .GetRequiredService<IDocIndexBuilder>().EmbedPendingAsync();
            Assert.Equal(0, pending);

            _out.WriteLine($"索引就緒：manifest {imported.ManifestsScanned} 份／" +
                           $"共 {imported.EntriesTotal} 段（重寫 {imported.EntriesRewritten} 段）／已核可 {imported.EntriesApproved} 段／" +
                           $"過期 {imported.EntriesStale} 段");
        }

        public Task DisposeAsync() => SetAsync(read: true, update: true);

        [LiveOllamaFact]
        public async Task 問一題規則類問題_引文以就地展開的形式回來()
        {
            var json = await AskAsync(RuleQuestion);
            _out.WriteLine("");
            _out.WriteLine($"提問：{RuleQuestion}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(),
                $"提問失敗：{root.GetProperty("message")}");

            var data = root.GetProperty("data");
            _out.WriteLine($"耗時 {data.GetProperty("elapsedMs").GetInt32() / 1000.0:F1}s" +
                           $"／模型 {data.GetProperty("model")}");
            _out.WriteLine("");
            _out.WriteLine("答覆：");
            _out.WriteLine(data.GetProperty("body").GetString() ?? "(空)");

            Assert.False(data.GetProperty("isNoAnswer").GetBoolean(),
                "模型拒答了。這一題的正解在已核可的擷取物裡，拒答代表檢索沒把它給出去。");

            // **字面比對，不反序列化**：見類別註解。列舉一旦掉回數字，
            // 型別化的讀法會安靜地把 0 讀成 Manual，正好抹掉要抓的東西。
            Assert.Contains("\"kind\":\"", json, StringComparison.Ordinal);

            var citations = data.GetProperty("citations").EnumerateArray().ToList();
            Assert.NotEmpty(citations);

            _out.WriteLine("");
            _out.WriteLine("引文：");
            foreach (var c in citations)
            {
                var kind = c.GetProperty("kind").GetString();
                var code = c.GetProperty("pageCode").GetString();
                var url = c.GetProperty("url").GetString() ?? string.Empty;
                var text = c.GetProperty("text").GetString() ?? string.Empty;
                _out.WriteLine($"  [{kind}] {code}");
                _out.WriteLine($"      麵包屑 {c.GetProperty("headingPath").GetString()}");
                _out.WriteLine($"      連結 {(url.Length == 0 ? "（無，就地展開）" : url)}");
                if (text.Length > 0) _out.WriteLine($"      展開內容 {text}");
            }

            var rules = citations.Where(c => c.GetProperty("kind").GetString() == "Rule").ToList();
            Assert.True(rules.Count > 0,
                "一則 rules/* 引文都沒有。這一題的正解只在擷取物裡，" +
                "代表檢索、核可、嵌入、或引文分類其中一環斷了。");

            foreach (var c in rules)
            {
                var code = c.GetProperty("pageCode").GetString() ?? string.Empty;
                Assert.StartsWith(QaCitationPresenter.RulePrefix, code, StringComparison.Ordinal);

                // 就地展開的內容必須隨答案一起送出：磁碟上沒有 rules/*.html，
                // 而且刻意不產（產出來就是匿名可讀，wwwroot 在 UseAuthentication 之前）。
                Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("text").GetString()),
                    $"{code} 沒有展開內容——畫面會渲染出一個空的摺疊區塊，" +
                    "而那看起來像「這頁剛好沒內容」，不像壞掉。");

                // 空字串代表畫面不得畫 <a>，不是「連到首頁」。
                Assert.Equal(string.Empty, c.GetProperty("url").GetString());
            }

            // 手冊引文的行為必須完全不變（定案 #47 只動擷取物那一種）。
            foreach (var c in citations.Where(c => c.GetProperty("kind").GetString() == "Manual"))
            {
                Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("url").GetString()),
                    "手冊引文沒有連結——它點得進去是既有行為，不該被這次變更影響。");
                Assert.Equal(string.Empty, c.GetProperty("text").GetString());
            }
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task<ExtractImportResult> ImportRepoManifestsAsync()
        {
            var dir = Path.Combine(RepoRoot(), ExtractManifestImporter.DefaultManifestDir);
            Assert.True(Directory.Exists(dir), $"找不到擷取物目錄 {dir}。");

            using var scope = _factory.Services.CreateScope();
            var importer = new ExtractManifestImporter(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ExtractManifestImporter.ManifestDirKey] = dir,
                }).Build(),
                scope.ServiceProvider.GetRequiredService<ILogger<ExtractManifestImporter>>());

            return await importer.ImportAsync();
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

            throw new DirectoryNotFoundException("找不到 repo 根目錄（.git）");
        }

        private HttpClient Client() => _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        /// <summary>
        /// 串流真的問一題（§11.2 C4 的驗收）。**這裡量的是傳輸，不是正確性**——
        /// 答得對不對由既有那幾支負責。
        ///
        /// <para><b>⚠️ 這支測試原本斷言「首個訊框早於完成時間的一半」，實測不成立，已改。</b>
        /// 2026-08-21 桌機 `gemma4:latest` CPU 推論實測：首個訊框 45.3 秒、完成 48.5 秒、
        /// 共 39 個訊框。**時間不是花在傳輸，是花在 prefill**——直接對 Ollama 量（完全繞過本專案）：
        /// 2 字的 prompt 首字 0.8 秒，3149 prompt tokens 的首字 36.8 秒、總計 39.9 秒。
        /// 也就是說本功能的 prompt（§10.8 的 5 塊 context）光是讀進去就要四十幾秒，
        /// 而真正生成只有三秒。**串流搬動的是那三秒，不是那四十五秒。**</para>
        ///
        /// <para>所以這裡改成斷言**串流真正保證的那件事**：訊框是「散佈在生成期間」而不是
        /// 「在最後一次全給」。這道斷言與 prefill 多久無關，而它擋得住唯一會讓串流失效的回歸——
        /// 任何人把 <c>ResponseHeadersRead</c> 拿掉、或在中間插一層會緩衝的中介層時，
        /// 所有訊框會擠在同一個瞬間到達，首末差會塌成 0。</para>
        /// </summary>
        [LiveOllamaFact]
        public async Task 串流時第一個字遠早於最後一個字()
        {
            using var client = Client();
            var html = await client.GetStringAsync("/QaAssistant");
            var m = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(m.Success, "對話畫面上找不到 AntiForgery token。");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/QaAssistant/AskStream")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("question", RuleQuestion),
                    new KeyValuePair<string, string>("__RequestVerificationToken", m.Groups[1].Value),
                }),
            };

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // ResponseHeadersRead 是這支測試成立的前提：預設會把整個回應讀完才回來，
            // 那樣量到的「第一個字」永遠等於「最後一個字」，測試就變成恆綠的裝飾。
            using var res = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var stream = await res.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            TimeSpan? firstToken = null;
            TimeSpan? lastToken = null;
            TimeSpan? doneAt = null;
            var tokens = 0;
            var text = new System.Text.StringBuilder();
            JsonElement done = default;
            JsonElement infer = default;
            var sawInfer = false;

            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                using var frame = JsonDocument.Parse(line);

                if (frame.RootElement.TryGetProperty("t", out var t))
                {
                    firstToken ??= clock.Elapsed;
                    lastToken = clock.Elapsed;
                    tokens++;
                    text.Append(t.GetString());
                    continue;
                }

                // 階段訊框（C4-A）。**必須在 done 之前先攔掉**：這個迴圈原本把
                // 「不是 t、不是 error」的訊框一律當成 done，多一種訊框就會在
                // GetProperty("done") 炸開——而且只有真的有 Ollama 時才炸得到。
                if (frame.RootElement.TryGetProperty("stage", out var stage))
                {
                    if (stage.GetString() == "infer")
                    {
                        sawInfer = true;
                        infer = frame.RootElement.Clone();
                    }
                    continue;
                }

                if (frame.RootElement.TryGetProperty("error", out var err))
                    Assert.Fail($"串流回了錯誤訊框：{err.GetString()}");

                doneAt = clock.Elapsed;
                done = frame.RootElement.GetProperty("done").Clone();
            }

            Assert.NotNull(firstToken);
            Assert.NotNull(doneAt);
            Assert.True(tokens > 1, $"只收到 {tokens} 個訊框——那不是串流，是一次全給。");

            // 有命中就一定會進推論，所以 infer 階段必到，而且它報的兩個數字要是真的。
            // 這是唯一能驗到 chunks／chars 的地方：沒有推論服務的機器上，
            // 檢索命中不了任何東西（fixture 沒有向量），走的是零命中短路那條路。
            Assert.True(sawInfer, "有命中卻沒收到 infer 階段訊框——等待中的畫面會停在「思考中」。");
            Assert.True(infer.GetProperty("chunks").GetInt32() > 0, "infer 訊框說參考資料是 0 段。");
            Assert.True(infer.GetProperty("chars").GetInt32() > 0, "infer 訊框說提示詞是 0 字。");

            var spread = lastToken!.Value - firstToken!.Value;

            _out.WriteLine("");
            _out.WriteLine($"提問：{RuleQuestion}");
            _out.WriteLine($"首個訊框 {firstToken!.Value.TotalSeconds:F1} 秒／" +
                           $"末個訊框 {lastToken!.Value.TotalSeconds:F1} 秒／" +
                           $"完成 {doneAt!.Value.TotalSeconds:F1} 秒／共 {tokens} 個訊框");
            _out.WriteLine($"其中 prefill（首個訊框之前）{firstToken!.Value.TotalSeconds:F1} 秒、" +
                           $"生成 {spread.TotalSeconds:F1} 秒");
            _out.WriteLine($"模型 {done.GetProperty("model").GetString()}");

            // 訊框必須散佈在生成期間。被緩衝的實作會讓首末差塌成接近 0
            // （所有訊框在同一個瞬間到達），這道門檻擋的正是那件事。
            // **刻意不拿 prefill 當分母**：那段長短取決於 prompt 大小與硬體，
            // 與「有沒有在串流」無關，拿它當門檻只會量到機器快不快。
            Assert.True(spread > TimeSpan.FromMilliseconds(500),
                $"{tokens} 個訊框的首末只差 {spread.TotalMilliseconds:F0} ms——" +
                "它們幾乎同時到達，串流很可能在某一層被緩衝掉了。");

            // done 訊框的本文與串流累積的原始輸出不必相等（來源行會被 Parse 拿掉），
            // 但**本文必須是原始輸出的一部分**——不是的話代表兩條路徑取的不是同一段生成。
            var body = done.GetProperty("body").GetString() ?? string.Empty;
            Assert.False(body.Length == 0, "done 訊框沒有本文。");
            Assert.Contains(body.Split('\n')[0].Trim(), text.ToString(), StringComparison.Ordinal);
        }

        private async Task<string> AskAsync(string question)
        {
            // 取 token 與送出必須是同一個 client（AntiForgery 是 cookie＋欄位成對驗證）。
            using var client = Client();
            var html = await client.GetStringAsync("/QaAssistant");
            var m = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
            Assert.True(m.Success, "對話畫面上找不到 AntiForgery token。");

            var res = await client.PostAsync("/QaAssistant/Ask", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("question", question),
                new KeyValuePair<string, string>("__RequestVerificationToken", m.Groups[1].Value),
            }));

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        private async Task SetAsync(bool read, bool update)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var p = await db.MenuPermissions
                .FirstOrDefaultAsync(x => x.RoleId == RoleId && x.MenuKey == MenuKey);
            if (p is null)
            {
                p = new MenuPermission { RoleId = RoleId, MenuKey = MenuKey };
                db.MenuPermissions.Add(p);
            }

            p.CanRead = read;
            p.CanUpdate = update;
            await db.SaveChangesAsync();
        }
    }
}
