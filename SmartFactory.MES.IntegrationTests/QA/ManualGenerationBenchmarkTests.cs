using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using SmartFactory.MES.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// **生成品質評測**（規格 §10.8.2、待決 #5）。走完整條 檢索 → 組 prompt → 生成 → 解析。
    ///
    /// <para><b>四項指標，兩兩成對，單看一項都會選錯模型。</b></para>
    /// <list type="number">
    ///   <item><b>引文正確率</b>（答得對不對）配<b>平均引用頁數</b>（是不是用亂槍打鳥換來的）。
    ///     一個把參考資料五頁全部列進來源行的模型，引文命中率會很好看——
    ///     那不是理解，是把選擇題全部塗滿。</item>
    ///   <item><b>拒答正確率</b>（離題該說不知道）配<b>誤拒率</b>（在範圍內的題不該說不知道）。
    ///     只看前者的話，**一個永遠回「使用手冊中查無相關說明。」的模型會拿滿分**。
    ///     這兩項必須一起讀。</item>
    ///   <item><b>幻覺引文率</b>——引用了參考資料裡根本沒出現的頁面代號。
    ///     這一項與答案內容無關，是純粹的「有沒有在編」，而且完全可自動判定。</item>
    ///   <item><b>繁中用語</b>——簡體字與大陸慣用語。使用者是台灣工廠的現場人員，
    ///     一句「请点击保存按钮」就足以讓整個功能不能用。</item>
    /// </list>
    ///
    /// <para><b>引文正確率有一個必須拆開的分母。</b>檢索沒把正解頁放進參考資料時，
    /// 模型再強也引不到——那是檢索的失分，不是生成的。所以同時印出
    /// 「檢索有給到正解」的題數，以及**條件於此**的引文命中率。
    /// 不拆的話，這個指標量到的是檢索與生成的混合物，而換的是生成模型。</para>
    ///
    /// <para><b>延遲不是評選依據</b>（§17.11.3）。開發機無獨顯、一律 CPU 推論，
    /// 延遲是環境產物。這裡仍記錄秒數與 token 數，用途只有一個：
    /// 診斷 <c>num_predict</c> 是否被撞到（§10.8.1 第 2 點的靜默失敗）。</para>
    ///
    /// <para><b>不設回歸門檻</b>，理由與 <see cref="ManualHardBenchmarkTests"/> 相同。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualGenerationBenchmarkTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const int TopK = 5;

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;

        public ManualGenerationBenchmarkTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _app = factory;
            _out = output;
        }

        public async Task InitializeAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();
            await GrantEveryIndexedKeyAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<QaDbContext>());
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveOllamaFact]
        public async Task 量測_生成品質的四項指標()
        {
            using var scope = _app.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

            // 生成端可換成外部 API（§17.11.8-七 路徑一：開發期評測，不需待決 #49 的裁決）。
            // 預設完全維持原行為＝DI 的本機 Ollama。**判定程式碼一行都不為它改**，
            // 否則兩輪數字就不是同一把尺量的。
            var (llm, backend) = EvalLlmClients.Resolve(scope.ServiceProvider);
            var external = Environment.GetEnvironmentVariable(EvalLlmClients.ProviderVar) is { Length: > 0 } p
                           && !string.Equals(p.Trim(), "ollama", StringComparison.OrdinalIgnoreCase);

            var chatModel = external
                ? Environment.GetEnvironmentVariable(EvalLlmClients.ModelVar) ?? "(外部後端預設模型)"
                : config[OllamaClient.ChatModelKey] ?? string.Empty;

            // 只有走本機後端時才要求組態有生成模型：外部後端的模型名來自環境變數。
            Assert.False(!external && chatModel.Length == 0,
                $"未設定 {OllamaClient.ChatModelKey}，這個測試無法量任何東西。");
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型，檢索端拿不到語意通道，量到的會是詞彙檢索的生成品質。");

            var (embedded, pending) = await scope.ServiceProvider
                .GetRequiredService<IDocIndexBuilder>().EmbedPendingAsync();
            Assert.Equal(0, pending);

            // **權限是量測的前提，不是量測的對象**（見 GrantEveryIndexedKeyAsync）。
            // 2026-08-08 有一輪 121 把鑰匙只剩 1 把是 CanRead，於是模型幾乎只拿得到目錄頁，
            // 而報表照樣印出一整組看起來很合理的數字（引文 1/1＝100%）。
            // **那是最壞的一種壞法**：不報錯、不為零、還很好看。
            //
            // 肇因已於 2026-08-10 查明：不是這支測試自己搞的，是**另一個進程**——
            // ManualIndexIntegrationTests 當時用 Admin 角色做權限收窄且不還原，
            // 而測試庫名寫死，連不同 worktree 也共用同一份 MenuPermissions。
            // 那支已改用專屬角色；這裡的守門仍然留著，因為共用資料的假設不會只被違反一次。
            await AssertPermissionsIntactAsync(scope.ServiceProvider, "量測開始前");

            // 少量抽樣用（開發時想先確認 harness 會動），預設跑全部。
            var limit = int.TryParse(Environment.GetEnvironmentVariable("QA_GEN_LIMIT"), out var n) && n > 0
                ? n : int.MaxValue;

            // **失分明細一定要能回推到模型看到什麼。**只印「誤拒 2/3」的話，
            // 分不出是模型太保守還是提示詞太嚴——而後者是我自己寫的，
            // 拿它去比模型等於在比提示詞。設 QA_GEN_DUMP=1 印出每題的參考資料與答案全文。
            var dump = Environment.GetEnvironmentVariable("QA_GEN_DUMP") == "1";

            var inScope = ManualQuestionBank.Hard.Take(limit).ToList();
            var offScope = ManualQuestionBank.OutOfScope.Take(limit).ToList();

            // 抬頭一定要印後端：沒有它，兩份報表分不出是換了模型還是換了機器
            // （而換機器同時換掉了延遲、量化方式與思考行為）。
            _out.WriteLine($"生成後端 {backend}");
            _out.WriteLine($"生成模型 {chatModel}；嵌入模型 {embedding.EmbeddingModelName}（本次補嵌入 {embedded} 塊）" +
                           (external ? "  ← 嵌入仍在本機（§17.11.8 代價 2：換嵌入＝全量重嵌）" : ""));
            _out.WriteLine($"題目：範圍內 {inScope.Count} 題、離題 {offScope.Count} 題；topK={TopK}");
            _out.WriteLine("");

            // ── 範圍內：引文、誤拒、幻覺、用語 ────────────────────────────
            var citedRight = 0;          // 引用的頁面裡有正解
            var citedRightGiven = 0;     // 同上，但只算「檢索有給到正解」的題
            var retrievalGave = 0;       // 檢索有把正解放進參考資料
            var noCitation = 0;          // 有作答但沒有來源行
            var hallucinated = 0;        // 引用了參考資料裡沒有的頁面代號
            var falseRefusal = 0;        // 範圍內的題卻說查無
            var emptyContext = 0;        // 參考資料是空的（＝前提壞了，不是題目難）
            var citationCounts = new List<int>();
            var failures = new List<string>();
            var badChinese = new List<string>();
            var seconds = new List<double>();
            var completionTokens = new List<int>();
            var misses = new List<string>();

            var asked = 0;

            foreach (var q in inScope)
            {
                // **每 5 題重驗一次前提。**只驗頭尾的話，被改掉的那一輪要等整整兩小時
                // 才知道白跑——2026-08-10 就這樣連燒兩輪（見 ManualIndexIntegrationTests
                // 的 RoleId 註解）。權限是別的進程也能改的共用資料，抓早不抓晚。
                if (asked++ % 5 == 0)
                    await AssertPermissionsIntactAsync(scope.ServiceProvider, $"第 {asked} 題前");

                var hits = await SearchAsync(q.Question, TopK);

                // **要用真的進了 prompt 的那幾塊**，不是檢索回傳的全部。
                // 超出字數預算被丟掉的塊，模型根本沒看到；拿 hits 當分母的話，
                // 「引不到」會被記成生成端的失分，而它根本沒拿到那一頁。
                var given = QaPromptBuilder.SelectWithinBudget(hits)
                    .Select(h => QaPromptBuilder.PageCode(h.DocUrl)).ToList();
                var hasExpected = q.Expected.Any(e => given.Contains(e));
                if (hasExpected) retrievalGave++;
                if (given.Count == 0) emptyContext++;

                var (answer, error, elapsed) = await AskAsync(llm, q.Question, hits);
                if (error is not null) { failures.Add($"  {q.Question,-30} {error}"); continue; }

                seconds.Add(elapsed);
                completionTokens.Add(answer.CompletionTokens);

                var parts = QaPromptBuilder.Parse(answer.Text);
                if (dump) Dump(q.Question, given, answer.Text);

                if (parts.IsNoAnswer)
                {
                    falseRefusal++;
                    misses.Add($"  [誤拒] {q.Question,-30} 檢索{(hasExpected ? "有" : "沒")}給到正解" +
                               $"（參考資料：{string.Join("、", given)}）");
                    continue;
                }

                citationCounts.Add(parts.Citations.Count);
                if (parts.Citations.Count == 0)
                {
                    noCitation++;
                    misses.Add($"  [無來源] {q.Question,-30}");
                }
                else
                {
                    var invented = parts.Citations.Where(c => !given.Contains(c)).ToList();
                    if (invented.Count > 0)
                    {
                        hallucinated++;
                        misses.Add($"  [幻覺引文] {q.Question,-30} 引了 {string.Join("、", invented)}");
                    }

                    if (parts.Citations.Any(c => q.Expected.Contains(c)))
                    {
                        citedRight++;
                        if (hasExpected) citedRightGiven++;
                    }
                    else
                    {
                        misses.Add($"  [引錯頁] {q.Question,-30} 引了 {string.Join("、", parts.Citations)}" +
                                   $"／期望 {string.Join("、", q.Expected)}" +
                                   (hasExpected ? "" : "（檢索本來就沒給到）"));
                    }
                }

                var flaws = ChineseTextFlaws.Describe(parts.Body);
                if (flaws.Length > 0) badChinese.Add($"  {q.Question,-24} {flaws}");
            }

            // ── 離題：該拒答 ──────────────────────────────────────────────
            var refused = 0;
            var fabricated = new List<string>();
            foreach (var q in offScope)
            {
                var hits = await SearchAsync(q, TopK);
                var (answer, error, elapsed) = await AskAsync(llm, q, hits);
                if (error is not null) { failures.Add($"  {q,-30} {error}"); continue; }

                seconds.Add(elapsed);
                completionTokens.Add(answer.CompletionTokens);

                var parts = QaPromptBuilder.Parse(answer.Text);
                if (dump) Dump(q, QaPromptBuilder.SelectWithinBudget(hits)
                    .Select(h => QaPromptBuilder.PageCode(h.DocUrl)).ToList(), answer.Text);

                if (parts.IsNoAnswer) refused++;
                else fabricated.Add($"  {q,-16} → {Oneline(parts.Body)}");

                var flaws = ChineseTextFlaws.Describe(parts.Body);
                if (flaws.Length > 0) badChinese.Add($"  {q,-24} {flaws}");
            }

            // ── 報表 ──────────────────────────────────────────────────────
            var attempted = inScope.Count;

            _out.WriteLine($"【引文】命中正解 {citedRight}/{attempted}" +
                           $"（{Pct(citedRight, attempted)}）");
            _out.WriteLine($"        檢索有給到正解的 {retrievalGave}/{attempted} 題中，" +
                           $"引文命中 {citedRightGiven}/{retrievalGave}（{Pct(citedRightGiven, retrievalGave)}）" +
                           "  ← 這一項才是生成端的分數");
            _out.WriteLine($"        沒附來源 {noCitation}／幻覺引文 {hallucinated}／" +
                           $"平均引用 {(citationCounts.Count > 0 ? citationCounts.Average() : 0):F2} 頁" +
                           "（越接近 topK 越可疑：那是把選擇題塗滿，不是理解）");
            _out.WriteLine("");
            _out.WriteLine($"【拒答】離題正確拒答 {refused}/{offScope.Count}（{Pct(refused, offScope.Count)}）");
            _out.WriteLine($"        範圍內誤拒 {falseRefusal}/{attempted}（{Pct(falseRefusal, attempted)}）" +
                           "  ← 與上一行必須一起讀，只看拒答率的話全部拒答就是滿分");
            foreach (var line in fabricated) _out.WriteLine("  [該拒未拒] " + line);
            _out.WriteLine("");
            _out.WriteLine($"【繁中】有問題的回答 {badChinese.Count}/{attempted + offScope.Count}");
            foreach (var line in badChinese.Take(20)) _out.WriteLine(line);
            _out.WriteLine("");

            if (failures.Count > 0)
            {
                _out.WriteLine($"【生成失敗】{failures.Count} 題（截斷或空回覆，見 §10.8.1）");
                foreach (var line in failures.Take(20)) _out.WriteLine(line);
                _out.WriteLine("");
            }

            _out.WriteLine($"【參考（非評選依據）】平均 {(seconds.Count > 0 ? seconds.Average() : 0):F1} 秒／" +
                           $"平均生成 {(completionTokens.Count > 0 ? completionTokens.Average() : 0):F0} tokens／" +
                           $"最長 {(completionTokens.Count > 0 ? completionTokens.Max() : 0)} tokens" +
                           $"（上限 {config.GetValue<int?>(OllamaClient.NumPredictKey) ?? OllamaClient.DefaultNumPredict}）");
            _out.WriteLine("");
            _out.WriteLine("★ 換生成模型比這四項——" +
                           $"引文（給到正解時）{citedRightGiven}/{retrievalGave}／" +
                           $"拒答 {refused}/{offScope.Count}／誤拒 {falseRefusal}/{attempted}／" +
                           $"幻覺引文 {hallucinated}／繁中缺陷 {badChinese.Count}");
            _out.WriteLine("");
            _out.WriteLine("── 逐題失分明細 ──");
            foreach (var line in misses) _out.WriteLine(line);

            // 唯一的品質相關斷言：整條管線真的跑起來了。**不設品質門檻**——首測值當目標值是反模式。
            Assert.True(seconds.Count > 0, "一題都沒有成功生成，這不是品質問題而是管線壞了。");

            // ── 量完再驗一次前提 ────────────────────────────────────────
            // 權限可能在跑的中途被改掉（實測發生過），而中途被改掉的那一輪，
            // 前半段的數字是對的、後半段是垃圾——只驗開頭抓不到。
            await AssertPermissionsIntactAsync(scope.ServiceProvider, "量測結束後");

            // 範圍內的題目不該有「參考資料是空的」這種事：檢索無門檻（定案 #29），
            // 空的參考資料代表權限或索引出問題，不代表這一題難。
            Assert.True(emptyContext == 0,
                $"有 {emptyContext} 題的參考資料是空的。檢索不設門檻，所以這不是難度問題，" +
                "是索引或權限壞了——這一輪的數字全部作廢，不要拿去比較。");
        }

        private static string Pct(int a, int b) => b == 0 ? "n/a" : $"{a * 100.0 / b:F0}%";

        private void Dump(string question, IReadOnlyList<string> given, string answer)
        {
            _out.WriteLine($"── 提問：{question}");
            _out.WriteLine($"   參考資料：{string.Join("、", given)}");
            foreach (var line in answer.Split('\n')) _out.WriteLine("   │ " + line);
            _out.WriteLine("");
        }

        private static string Oneline(string s)
        {
            var t = s.Replace('\n', ' ').Trim();
            return t.Length <= 60 ? t : t[..60] + "…";
        }

        /// <summary>
        /// 問一題。**生成失敗不中斷整輪**——一次截斷就讓 65 題的量測全部作廢，
        /// 而截斷本身正是要記錄的資料（§10.8.1 第 2 點）。
        /// </summary>
        private static async Task<(LlmAnswer Answer, string? Error, double Seconds)> AskAsync(
            ILlmClient llm, string question, IReadOnlyList<DocSearchHit> hits)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var answer = await llm.ChatAsync(
                    QaPromptBuilder.SystemPrompt, QaPromptBuilder.BuildUserPrompt(question, hits));
                return (answer, null, sw.Elapsed.TotalSeconds);
            }
            catch (OllamaException ex)
            {
                return (default, Oneline(ex.Message), sw.Elapsed.TotalSeconds);
            }
        }

        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(string question, int topK)
        {
            using var scope = _app.Services.CreateScope();
            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new GenBenchUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK);
        }

        /// <summary>
        /// 驗證「索引裡的每一個 MenuKey 都仍有 CanRead」。
        ///
        /// <para><b>這道守門是 2026-08-08 一輪被污染的量測換來的。</b>
        /// <see cref="GrantEveryIndexedKeyAsync"/> 在 <c>InitializeAsync</c> 授權，
        /// 但那之後權限仍可能被改掉（實測出現過 121 把鑰匙只剩 1 把 <c>CanRead</c>）。
        /// 症狀不是例外也不是零分——檢索只剩沒有 MenuKey 的目錄頁還是會回東西，
        /// 於是報表印出一整組**看起來很合理的數字**。**開頭與結尾都要驗**：
        /// 只驗開頭的話，跑到一半才被改掉的那種，前半段對、後半段是垃圾。</para>
        /// </summary>
        private static async Task AssertPermissionsIntactAsync(IServiceProvider sp, string when)
        {
            var qa = sp.GetRequiredService<QaDbContext>();
            var db = sp.GetRequiredService<ApplicationDbContext>();

            var indexed = await qa.DocChunks.Where(c => c.MenuKey != "")
                .Select(c => c.MenuKey).Distinct().ToListAsync();
            var readable = await db.MenuPermissions
                .Where(p => p.RoleId == RoleId && p.CanRead)
                .Select(p => p.MenuKey).ToListAsync();

            var missing = indexed.Except(readable, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0,
                $"{when}：索引裡有 {indexed.Count} 個 MenuKey，但角色 {RoleId} 只讀得到 {readable.Count} 個，" +
                $"缺 {missing.Count} 個（例：{string.Join("、", missing.Take(5))}）。" +
                "權限是量測的前提不是量測的對象——這一輪的數字全部作廢。");
        }

        /// <summary>授出索引裡的每一個 MenuKey，理由見 <see cref="ManualHardBenchmarkTests"/>。</summary>
        private static async Task GrantEveryIndexedKeyAsync(ApplicationDbContext db, QaDbContext qa)
        {
            var keys = await qa.DocChunks
                .Where(c => c.MenuKey != "")
                .Select(c => c.MenuKey)
                .Distinct()
                .ToListAsync();

            var existing = await db.MenuPermissions.Where(p => p.RoleId == RoleId).ToListAsync();
            foreach (var p in existing) p.CanRead = true;

            foreach (var key in keys)
            {
                if (existing.Any(p => p.MenuKey == key)) continue;
                db.MenuPermissions.Add(new MenuPermission { RoleId = RoleId, MenuKey = key, CanRead = true });
            }

            await db.SaveChangesAsync();
        }

        private sealed class GenBenchUserContext : IUserContext
        {
            public string? ActiveRoleId => RoleId;
            public string? PlantId => "P1";
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => "admin";
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => Array.Empty<string>();
        }
    }
}
