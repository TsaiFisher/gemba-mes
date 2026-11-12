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
    /// **有／無擷取物的對照量測**（規格 §10.8.2 之十二、§10.11）。
    ///
    /// <para><b>要回答的是一個代價問題，不是一個效果問題。</b>擷取物會佔索引裡的列、
    /// 會被嵌入、會跟手冊的塊一起排名——也就是說它們**跟手冊搶 <c>topK</c> 的名額**
    /// （§10.11「三件事是實跑換來的」③ 已經指出這個風險）。
    /// 效果那一面是顯然的（沒有擷取物就答不出規則類問題），
    /// 代價那一面才是會讓現有 53 題悄悄退步的那一個，而且**退步不會有任何訊號**：
    /// 檢索照樣回東西、模型照樣作答、報表照樣好看。</para>
    ///
    /// <para><b>兩臂在同一個進程裡跑完</b>，順序是先「無」後「有」。
    /// 分兩次執行的話，兩臂之間差的不只是擷取物——還差一次索引重建、一次權限狀態、
    /// 可能還差別的進程對共用測試庫做的事（那正是 <c>ManualGenerationBenchmarkTests</c>
    /// 的權限守門存在的理由）。**對照量測唯一該變的就是那一個變因。**</para>
    ///
    /// <para><b>三組題目，各自量一件事：</b></para>
    /// <list type="number">
    ///   <item><see cref="ManualQuestionBank.Hard"/> 53 題——**代價**。看
    ///     「檢索有給到手冊正解」的題數在兩臂之間掉了幾題，以及是哪幾題。</item>
    ///   <item><see cref="ManualQuestionBank.RuleGrounded"/> 6 題——**效果**。
    ///     無擷取物時應該大量誤拒；有擷取物時應該答得出來並引 <c>rules/*</c>。</item>
    ///   <item><see cref="ManualQuestionBank.RejectedRuleProbes"/> 2 題與離題 12 題——**閘門與拒答**。
    ///     被退回的段落已經在索引裡，只靠 <c>DocChunkFilter</c> 的 <c>AND c."Approved"</c> 擋著；
    ///     閘門失效的症狀是「答得出來」而不是報錯。</item>
    /// </list>
    ///
    /// <para><b>時機是硬的：必須在第一批核可的擷取物進入正式索引之前跑。</b>
    /// 之後再跑就沒有「無擷取物」那一臂了——正式索引隨 DB 出貨（§10.11.6），
    /// 而重建一份沒有擷取物的索引來事後補量，量到的是那時候的手冊，不是現在這一份。</para>
    ///
    /// <para><b>不設任何品質門檻</b>，理由同 <c>ManualHardBenchmarkTests</c>：
    /// 首測值當目標值是反模式。唯一的斷言是「兩臂真的不同」——
    /// 擷取物沒被匯入的話，這支測試會印出一份兩欄一模一樣的漂亮報表。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualExtractImpactBenchmarkTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const int TopK = 5;

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;

        public ManualExtractImpactBenchmarkTests(AuthedScheduleFactory factory, ITestOutputHelper output)
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
        public async Task 量測_擷取物對既有手冊問答的影響()
        {
            using var scope = _app.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

            var chatModel = config[OllamaClient.ChatModelKey] ?? string.Empty;
            Assert.False(chatModel.Length == 0,
                $"未設定 {OllamaClient.ChatModelKey}，這個測試無法量任何東西。");
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型，檢索端拿不到語意通道，量到的會是詞彙檢索的生成品質。");

            _out.WriteLine($"生成模型 {chatModel}；嵌入模型 {embedding.EmbeddingModelName}；topK={TopK}");
            _out.WriteLine("");

            // ── 第一臂：無擷取物 ────────────────────────────────────────────
            // 受測應用程式的 manifest 目錄本來就指到不存在的路徑（見 TestWebApplicationFactory），
            // 但**別的測試可能已經把擷取物寫進共用測試庫**——測試庫是跨進程共用的。
            // 所以這裡明確清乾淨，不假設起始狀態。
            var removed = await DeleteExtractRowsAsync();
            _out.WriteLine($"【第一臂】無擷取物（清掉 {removed} 列殘留）");
            await EmbedAllAsync();
            var without = await RunArmAsync("無擷取物");

            // ── 第二臂：匯入版控裡真正的 manifest ──────────────────────────
            var imported = await ImportRepoManifestsAsync();
            Assert.True(imported.EntriesApproved > 0,
                $"匯入了 {imported.EntriesTotal} 段但已核可 0 段——" +
                "第二臂與第一臂會完全相同，這一輪的對照沒有意義（檢索只吃 Approved 的列）。");

            _out.WriteLine("");
            _out.WriteLine($"【第二臂】有擷取物：manifest {imported.ManifestsScanned} 份／" +
                           $"共 {imported.EntriesTotal} 段（重寫 {imported.EntriesRewritten} 段）／**已核可 {imported.EntriesApproved} 段**／" +
                           $"過期 {imported.EntriesStale} 段");
            var (embedded, pending) = await EmbedAllAsync();
            _out.WriteLine($"          本臂補嵌入 {embedded} 塊（待嵌 {pending}）");
            Assert.Equal(0, pending);

            var with = await RunArmAsync("有擷取物");

            // ── 對照報表 ──────────────────────────────────────────────────
            Report(without, with);

            // 唯一的斷言：兩臂真的不同。**擷取物一列都沒進檢索的話，
            // 上面那份報表會是兩欄一模一樣的數字，而它看起來完全正常。**
            Assert.True(with.ExtractCitations > 0 || with.RuleAnswered > without.RuleAnswered,
                "第二臂一次都沒有引用到 rules/* 也沒有多答出任何規則類題目——" +
                "擷取物多半沒有真的進到檢索（核可、stale、嵌入三者查一下）。這一輪的對照作廢。");

            await AssertPermissionsIntactAsync(scope.ServiceProvider, "兩臂都跑完後");
        }

        // ── 一臂 ────────────────────────────────────────────────────────────

        /// <summary>一臂的全部數字。<c>Manual*</c> 是 53 題、<c>Rule*</c> 是 6 題、其餘見欄名。</summary>
        private sealed record Arm(
            string Label,
            int ManualAsked, int ManualRetrievalGave, int ManualCitedRight, int ManualCitedRightGiven,
            int ManualFalseRefusal, int ManualHallucinated, double ManualAvgCitations,
            int RuleAsked, int RuleAnswered, int RuleCitedRight, int RuleFalseRefusal,
            int OffRefused, int OffAsked,
            int ProbeRefused, int ProbeAsked,
            int ExtractCitations,
            double AvgSeconds,
            IReadOnlyDictionary<string, bool> ManualRetrievalHit,
            IReadOnlyList<string> Misses);

        private async Task<Arm> RunArmAsync(string label)
        {
            using var scope = _app.Services.CreateScope();
            var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();

            var manualAsked = 0;
            var manualGave = 0;
            var manualCited = 0;
            var manualCitedGiven = 0;
            var manualRefused = 0;
            var manualHallucinated = 0;
            var citationCounts = new List<int>();
            var retrievalHit = new Dictionary<string, bool>(StringComparer.Ordinal);
            var misses = new List<string>();
            var seconds = new List<double>();
            var extractCitations = 0;

            foreach (var q in ManualQuestionBank.Hard)
            {
                // 權限是量測的前提不是量測的對象；理由與守法見 ManualGenerationBenchmarkTests。
                if (manualAsked % 5 == 0)
                    await AssertPermissionsIntactAsync(scope.ServiceProvider, $"{label} 第 {manualAsked + 1} 題前");

                manualAsked++;
                var (given, parts, elapsed, error) = await AskAsync(llm, q.Question);
                if (error is not null) { misses.Add($"  [{label}][生成失敗] {q.Question,-28} {error}"); continue; }
                seconds.Add(elapsed);

                var hasExpected = q.Expected.Any(e => given.Contains(e));
                retrievalHit[q.Question] = hasExpected;
                if (hasExpected) manualGave++;
                extractCitations += parts.Citations.Count(IsExtractPage);

                if (parts.IsNoAnswer) { manualRefused++; continue; }
                citationCounts.Add(parts.Citations.Count);
                if (parts.Citations.Any(c => !given.Contains(c))) manualHallucinated++;
                if (parts.Citations.Any(c => q.Expected.Contains(c)))
                {
                    manualCited++;
                    if (hasExpected) manualCitedGiven++;
                }
            }

            // ── 規則類 6 題：無擷取物時應該答不出來 ────────────────────────
            var ruleAnswered = 0;
            var ruleCited = 0;
            var ruleRefused = 0;
            foreach (var q in ManualQuestionBank.RuleGrounded)
            {
                var (given, parts, elapsed, error) = await AskAsync(llm, q.Question);
                if (error is not null) { misses.Add($"  [{label}][生成失敗] {q.Question,-28} {error}"); continue; }
                seconds.Add(elapsed);
                extractCitations += parts.Citations.Count(IsExtractPage);

                if (parts.IsNoAnswer)
                {
                    ruleRefused++;
                    misses.Add($"  [{label}][規則類拒答] {q.Question,-28}（參考資料：{Join(given)}）");
                    continue;
                }

                ruleAnswered++;
                if (parts.Citations.Any(c => q.Expected.Contains(c))) ruleCited++;
                else
                    misses.Add($"  [{label}][規則類引錯頁] {q.Question,-28} 引了 {Join(parts.Citations)}" +
                               $"／期望 {Join(q.Expected)}");
            }

            // ── 離題 12 題＋退回段落探針 2 題：都該拒答 ────────────────────
            var offRefused = await CountRefusalsAsync(llm, ManualQuestionBank.OutOfScope, label, "離題", misses, seconds);
            var probeRefused = await CountRefusalsAsync(
                llm, ManualQuestionBank.RejectedRuleProbes, label, "退回段落探針", misses, seconds);

            return new Arm(label,
                manualAsked, manualGave, manualCited, manualCitedGiven,
                manualRefused, manualHallucinated,
                citationCounts.Count > 0 ? citationCounts.Average() : 0,
                ManualQuestionBank.RuleGrounded.Length, ruleAnswered, ruleCited, ruleRefused,
                offRefused, ManualQuestionBank.OutOfScope.Length,
                probeRefused, ManualQuestionBank.RejectedRuleProbes.Length,
                extractCitations,
                seconds.Count > 0 ? seconds.Average() : 0,
                retrievalHit, misses);
        }

        private async Task<int> CountRefusalsAsync(
            ILlmClient llm, IReadOnlyList<string> questions, string label, string kind,
            List<string> misses, List<double> seconds)
        {
            var refused = 0;
            foreach (var q in questions)
            {
                var (given, parts, elapsed, error) = await AskAsync(llm, q);
                if (error is not null) { misses.Add($"  [{label}][生成失敗] {q,-28} {error}"); continue; }
                seconds.Add(elapsed);

                if (parts.IsNoAnswer) refused++;
                else
                    misses.Add($"  [{label}][{kind}該拒未拒] {q,-28} → {Oneline(parts.Body)}" +
                               $"（引了 {Join(parts.Citations)}）");
            }
            return refused;
        }

        /// <summary>
        /// 問一題，回傳**真的進了 prompt 的頁碼**與解析結果。
        /// 用 <c>SelectWithinBudget</c> 而不是檢索回傳的全部，理由見 <c>ManualGenerationBenchmarkTests</c>：
        /// 超出字數預算被丟掉的塊，模型根本沒看到。**這一點在本測試裡更關鍵**——
        /// 擷取物擠掉手冊的方式之一正是佔掉字數預算，而那是 <c>hits</c> 看不出來的。
        /// </summary>
        private async Task<(List<string> Given, QaAnswerParts Parts, double Seconds, string? Error)> AskAsync(
            ILlmClient llm, string question)
        {
            var hits = await SearchAsync(question, TopK);
            var given = QaPromptBuilder.SelectWithinBudget(hits)
                .Select(h => QaPromptBuilder.PageCode(h.DocUrl)).ToList();

            var sw = Stopwatch.StartNew();
            try
            {
                var answer = await llm.ChatAsync(
                    QaPromptBuilder.SystemPrompt, QaPromptBuilder.BuildUserPrompt(question, hits));
                return (given, QaPromptBuilder.Parse(answer.Text), sw.Elapsed.TotalSeconds, null);
            }
            catch (OllamaException ex)
            {
                return (given, default, sw.Elapsed.TotalSeconds, Oneline(ex.Message));
            }
        }

        // ── 報表 ────────────────────────────────────────────────────────────

        private void Report(Arm a, Arm b)
        {
            _out.WriteLine("");
            _out.WriteLine("══ 之十二　有／無擷取物對照 ══");
            _out.WriteLine($"{"",-34}{a.Label,12}{b.Label,12}");
            Row("手冊 53 題：檢索給到正解", a.ManualRetrievalGave, b.ManualRetrievalGave, a.ManualAsked);
            Row("手冊 53 題：引文命中（給到時）", a.ManualCitedRightGiven, b.ManualCitedRightGiven, a.ManualRetrievalGave);
            Row("手冊 53 題：誤拒", a.ManualFalseRefusal, b.ManualFalseRefusal, a.ManualAsked);
            Row("手冊 53 題：幻覺引文", a.ManualHallucinated, b.ManualHallucinated, a.ManualAsked);
            _out.WriteLine($"{"手冊 53 題：平均引用頁數",-34}{a.ManualAvgCitations,12:F2}{b.ManualAvgCitations,12:F2}");
            _out.WriteLine("");
            Row("規則類 6 題：有作答", a.RuleAnswered, b.RuleAnswered, a.RuleAsked);
            Row("規則類 6 題：引到正解頁", a.RuleCitedRight, b.RuleCitedRight, a.RuleAsked);
            _out.WriteLine("");
            Row("離題 12 題：正確拒答", a.OffRefused, b.OffRefused, a.OffAsked);
            Row("退回段落探針：正確拒答", a.ProbeRefused, b.ProbeRefused, a.ProbeAsked);
            _out.WriteLine($"{"引用到 rules/* 的次數",-34}{a.ExtractCitations,12}{b.ExtractCitations,12}");
            _out.WriteLine($"{"平均秒數（非評選依據）",-34}{a.AvgSeconds,12:F1}{b.AvgSeconds,12:F1}");
            _out.WriteLine("");

            // **這一份清單才是本次量測的主要產物。**彙總數字持平不代表沒事：
            // 掉了兩題又補回兩題，加總看起來一動也沒動。
            var lost = a.ManualRetrievalHit
                .Where(kv => kv.Value && b.ManualRetrievalHit.TryGetValue(kv.Key, out var still) && !still)
                .Select(kv => kv.Key).ToList();
            var gained = b.ManualRetrievalHit
                .Where(kv => kv.Value && a.ManualRetrievalHit.TryGetValue(kv.Key, out var had) && !had)
                .Select(kv => kv.Key).ToList();

            _out.WriteLine($"── 檢索被擷取物擠掉的手冊題（{lost.Count} 題）──");
            foreach (var q in lost) _out.WriteLine("  " + q);
            if (gained.Count > 0)
            {
                _out.WriteLine($"── 反而變得給得到的（{gained.Count} 題，注意：這代表排序本來就在邊界上）──");
                foreach (var q in gained) _out.WriteLine("  " + q);
            }

            _out.WriteLine("");
            _out.WriteLine("── 逐題失分明細 ──");
            foreach (var line in a.Misses) _out.WriteLine(line);
            foreach (var line in b.Misses) _out.WriteLine(line);
        }

        private void Row(string label, int a, int b, int denom)
        {
            var delta = b - a;
            var mark = delta == 0 ? "" : delta > 0 ? $"  ↑{delta}" : $"  ↓{-delta}";
            _out.WriteLine($"{label,-34}{a + "/" + denom,12}{b + "/" + denom,12}{mark}");
        }

        // ── 索引操作 ────────────────────────────────────────────────────────

        private async Task<int> DeleteExtractRowsAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        /// <summary>
        /// 匯入**版控裡真正的那幾份 manifest**（不是測試自己捏的）。
        /// 之十二 要量的就是「即將出貨的這一批」對現有問答的影響，
        /// 捏一份出來量等於量了一個不會上線的東西。
        /// </summary>
        private async Task<ExtractImportResult> ImportRepoManifestsAsync()
        {
            var dir = Path.Combine(RepoRoot(), ExtractManifestImporter.DefaultManifestDir);
            Assert.True(Directory.Exists(dir), $"找不到擷取物目錄 {dir}，第二臂無事可測。");

            using var scope = _app.Services.CreateScope();
            var importer = new ExtractManifestImporter(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ExtractManifestImporter.ManifestDirKey] = dir,
                }).Build(),
                scope.ServiceProvider.GetRequiredService<ILogger<ExtractManifestImporter>>());

            return await importer.ImportAsync();
        }

        private async Task<(int Embedded, int Pending)> EmbedAllAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().EmbedPendingAsync();
        }

        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(string question, int topK)
        {
            using var scope = _app.Services.CreateScope();
            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new ImpactBenchUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK);
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private static bool IsExtractPage(string pageCode) =>
            pageCode.StartsWith("rules/", StringComparison.Ordinal);

        private static string Join(IEnumerable<string> items) => string.Join("、", items);

        private static string Oneline(string s)
        {
            var t = (s ?? string.Empty).Replace('\n', ' ').Trim();
            return t.Length <= 60 ? t : t[..60] + "…";
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

            throw new DirectoryNotFoundException("找不到 repo 根目錄（.git）");
        }

        /// <summary>同 <c>ManualGenerationBenchmarkTests</c> 的守門，理由見該檔（2026-08-08 的污染量測）。</summary>
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
                $"缺 {missing.Count} 個（例：{string.Join("、", missing.Take(5))}）。這一輪的數字全部作廢。");
        }

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

        private sealed class ImpactBenchUserContext : IUserContext
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
