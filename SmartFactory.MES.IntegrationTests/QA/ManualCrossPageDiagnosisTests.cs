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
    /// **跨頁題為什麼只有 4/12 全中的分診**（規格 §17.9 缺口 G5）。
    ///
    /// <para><b>這支測試不修任何東西，它只回答一個問題：瓶頸是「分數不夠」還是「名額被佔」。</b>
    /// 2026-08-26 <c>ManualContextBudgetTests</c> 量到跨頁 12 題只有 4 題「全部正解都在 topK」，
    /// 而 topK 5／4／3 <b>結果完全一樣</b>——所以它不是名額不夠。但「不是名額不夠」有兩種可能，
    /// 而兩者的修法差一個量級：</para>
    /// <list type="bullet">
    ///   <item><b>分數不夠</b>——第二頁在整個候選集裡就排得很後面（甚至不在候選集內）。
    ///     要修得動查詢改寫或兩段式檢索，成本高。</item>
    ///   <item><b>名額被同一頁佔掉</b>——第二頁其實排得不遠，只是前面幾格被<b>同一頁的其他塊</b>
    ///     佔滿了。排序後加一道「每頁只取最好的一塊」就能修，成本近乎零。</item>
    /// </list>
    ///
    /// <para><b>分辨的方法是模擬，不是猜。</b>取一份夠深的融合結果，做兩件事：
    /// ① 記下每個正解頁的<b>最佳名次</b>（頁層級，不是塊層級）；
    /// ② 把候選集<b>每頁只留最好的一塊</b>之後重新取前 5，看涵蓋率會不會變。
    /// ② 的數字若明顯上升，修法就是頁層級去重；若不動，那就是分數問題。</para>
    ///
    /// <para><b>刻意走正式檢索路徑</b>（<c>ManualSearchService</c>、預設 semantic-first）：
    /// 這裡要診斷的是使用者實際拿到的那幾塊，換一條路徑量出來的結論套不回去。</para>
    ///
    /// <para><b>擷取物必須在場</b>：它們與手冊在同一次排序裡競爭名額（<c>DocChunkFilter</c>
    /// 是兩個語料別的 OR），少了它們量到的名次分佈不是正式環境的分佈。</para>
    ///
    /// <para><b>不設門檻。</b>用途是選修法，選完之後守門的是既有的
    /// <c>ManualHardBenchmarkTests</c>。首測值當目標值是 §10.9 說過不要做的事。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualCrossPageDiagnosisTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        /// <summary>
        /// 候選深度。取 50 不是 5：要看的是「第二頁排在哪」，
        /// 而只取 5 的話所有沒進來的頁都會回報成同一個「不在裡面」，分不出遠近。
        /// </summary>
        private const int Depth = 50;

        /// <summary>正式的 topK（<c>QaAssistantController.TopK</c>）。模擬去重時取同一個值才可比。</summary>
        private const int TopK = 5;

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;
        private int _extractRowsOnEntry;

        public ManualCrossPageDiagnosisTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _app = factory;
            _out = output;
        }

        public async Task InitializeAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();

            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            _extractRowsOnEntry = await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract);

            await GrantEveryIndexedKeyAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), qa);
        }

        /// <summary>
        /// 還原測試庫。理由同 <c>ManualLexicalWeightSweepTests.DisposeAsync</c>：
        /// 庫名寫死是跨進程共用的，留著擷取物會讓之後每一支檢索測試量到另一份語料而不報錯。
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_extractRowsOnEntry > 0) return;

            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        [LiveOllamaFact]
        public async Task 分診_跨頁題的瓶頸是分數還是名額()
        {
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型。預設檢索模式是 semantic-first，沒有語意通道量到的會是另一條路。");

            var imported = await ImportRepoManifestsAsync();
            var (_, pending) = await EmbedAllAsync();
            Assert.Equal(0, pending);

            var (manual, extract) = await CorpusSizesAsync();
            _out.WriteLine($"語料：手冊 {manual} 塊、擷取物 {extract} 塊（已核可 {imported.EntriesApproved}）");
            _out.WriteLine($"候選深度 {Depth}、正式 topK {TopK}");
            _out.WriteLine("");

            var cross = ManualQuestionBank.Hard.Where(q => q.Kind == HardKind.CrossPage).ToArray();

            var fullNow = 0;
            var fullDeduped = 0;
            var missingEntirely = 0;   // 正解頁連候選集都沒進去 → 分數問題
            var withinDepth = 0;       // 正解頁在候選集內但沒進 topK
            var fullManualOnly = 0;    // 擷取物不佔名額時的全中題數
            var extractSlots = 0;      // 擷取物一共佔掉幾格

            foreach (var q in cross)
            {
                var hits = await SearchAsync(q.Question, Depth);

                // 頁層級的最佳名次：同一頁可能有好幾塊，取最前面那一塊的名次。
                var pageRank = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < hits.Count; i++)
                    pageRank.TryAdd(hits[i].DocUrl, i + 1);

                var ranks = q.Expected
                    .Select(e => (Page: e, Rank: pageRank.TryGetValue("/docs/user/" + e, out var r) ? r : 0))
                    .ToArray();

                // 現況：正式 topK 是塊層級的前 5。
                var top = hits.Take(TopK).Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                var nowHit = q.Expected.Count(e => top.Contains("/docs/user/" + e));

                // 模擬：每頁只留最好的一塊，再取前 5。
                var dedupedTop = hits
                    .GroupBy(h => h.DocUrl, StringComparer.Ordinal)
                    .Select(g => (Url: g.Key, Rank: hits.ToList().FindIndex(h => h.DocUrl == g.Key)))
                    .OrderBy(x => x.Rank)
                    .Take(TopK)
                    .Select(x => x.Url)
                    .ToHashSet(StringComparer.Ordinal);
                var dedupHit = q.Expected.Count(e => dedupedTop.Contains("/docs/user/" + e));

                if (nowHit == q.Expected.Length) fullNow++;
                if (dedupHit == q.Expected.Length) fullDeduped++;
                missingEntirely += ranks.Count(r => r.Rank == 0);
                withinDepth += ranks.Count(r => r.Rank > TopK);

                // 前 5 格裡有幾格是「這一頁已經出現過了」——名額被佔的直接證據。
                var top5Urls = hits.Take(TopK).Select(h => h.DocUrl).ToList();
                var wasted = top5Urls.Count - top5Urls.Distinct(StringComparer.Ordinal).Count();

                // 模擬：擷取物不佔名額（＝手冊塊裡的前 5 個相異頁）。
                // 這一臂測的是 §10.9 那個「稀釋」假設對跨頁題成不成立。
                var manualOnlyTop = hits
                    .Where(h => !IsExtract(h))
                    .Select(h => h.DocUrl)
                    .Distinct(StringComparer.Ordinal)
                    .Take(TopK)
                    .ToHashSet(StringComparer.Ordinal);
                var manualOnlyHit = q.Expected.Count(e => manualOnlyTop.Contains("/docs/user/" + e));
                if (manualOnlyHit == q.Expected.Length) fullManualOnly++;
                extractSlots += hits.Take(TopK).Count(IsExtract);

                _out.WriteLine($"「{q.Question}」");
                _out.WriteLine($"   正解 {q.Expected.Length} 頁　現況命中 {nowHit}　每頁去重後 {dedupHit}" +
                               $"　擷取物不佔名額 {manualOnlyHit}" +
                               $"　前 {TopK} 格重複佔用 {wasted} 格");
                foreach (var (page, rank) in ranks)
                    _out.WriteLine($"     {(rank == 0 ? "  —" : rank.ToString().PadLeft(3))}  {page}");

                // 誰佔住了前 5 格。沒有這一段就只知道「正解排第 8」，不知道被什麼壓著。
                for (var i = 0; i < Math.Min(TopK, hits.Count); i++)
                    _out.WriteLine($"     #{i + 1} {(IsExtract(hits[i]) ? "規則" : "手冊")}  {hits[i].DocUrl}");
            }

            _out.WriteLine("");
            _out.WriteLine($"跨頁題全中：現況 {fullNow}/{cross.Length}" +
                           $"　每頁只取最好一塊 {fullDeduped}/{cross.Length}" +
                           $"　擷取物不佔名額 {fullManualOnly}/{cross.Length}");
            _out.WriteLine($"擷取物佔掉的名額：{extractSlots}/{cross.Length * TopK} 格");
            _out.WriteLine($"正解頁不在候選集（深度 {Depth}）內：{missingEntirely} 頁　" +
                           $"在候選集內但排在 {TopK} 名之後：{withinDepth} 頁");
            _out.WriteLine("");
            _out.WriteLine("※ 判讀：『每頁只取最好一塊』明顯上升 → 修法是頁層級去重（便宜）；");
            _out.WriteLine("   不動而『不在候選集內』佔多數 → 是分數問題，要查詢改寫或兩段式檢索（貴）。");

            // ── 稀釋：同一件事在直白題庫上有多大 ────────────────────────────
            // §10.9 記的「擷取物只讓 @1 掉 1 題」量的是**純詞彙通道**。
            // 預設是 semantic-first，那個數字套不到預設路徑上——這裡補量。
            var now1 = 0; var now5 = 0; var only1 = 0; var only5 = 0; var slots = 0;
            foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
            {
                var hits = await SearchAsync(question, Depth);
                var url = "/docs/user/" + expected;

                var nowRank = hits.Take(TopK).ToList().FindIndex(h => h.DocUrl == url) + 1;
                if (nowRank == 1) now1++;
                if (nowRank >= 1) now5++;
                slots += hits.Take(TopK).Count(IsExtract);

                var manualOnly = hits.Where(h => !IsExtract(h)).Take(TopK).ToList();
                var onlyRank = manualOnly.FindIndex(h => h.DocUrl == url) + 1;
                if (onlyRank == 1) only1++;
                if (onlyRank >= 1) only5++;
            }

            _out.WriteLine("");
            _out.WriteLine($"直白題庫 {ManualRetrievalBenchmarkTests.Bank.Length} 題（semantic-first 預設路徑）：");
            _out.WriteLine($"  現況　　　　　Recall@1 {now1}　Recall@5 {now5}" +
                           $"　擷取物佔 {slots}/{ManualRetrievalBenchmarkTests.Bank.Length * TopK} 格");
            _out.WriteLine($"  擷取物不佔名額 Recall@1 {only1}　Recall@5 {only5}");
            _out.WriteLine("※ 兩列的差就是擷取物在**預設檢索路徑**上造成的稀釋。");
            _out.WriteLine("   §10.9 記的『只掉 1 題』量的是純詞彙通道，套不到這條路上。");

            // ── 分艙掃描：手冊 topK ＋ 擷取物另給 N 段 ──────────────────────
            // ⚠️ 上面每一列模擬的是「擷取物不佔名額」（＝把它們整批拿掉），
            // 這裡量的是「另給名額」——兩者不同，前者是診斷、後者才是修法。
            _out.WriteLine("");
            _out.WriteLine($"分艙掃描（手冊 {TopK} 塊 ＋ 擷取物另給 N 段）");
            _out.WriteLine("  N     跨頁題全中   直白@1  直白@5   規則題(中/英)   prompt 平均字數   估算 prefill");

            foreach (int? quota in new int?[] { null, 0, 1, 2, 3, 5 })
            {
                var crossFull = 0;
                foreach (var q in cross)
                {
                    var urls = (await SearchAsync(q.Question, TopK, quota))
                        .Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                    if (q.Expected.All(e => urls.Contains("/docs/user/" + e))) crossFull++;
                }

                var b1 = 0; var b5 = 0; var chars = new List<int>();
                foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
                {
                    var hits = await SearchAsync(question, TopK, quota);
                    chars.Add(QaPromptBuilder.BuildUserPrompt(question, hits).Length);

                    var rank = hits.ToList().FindIndex(h => h.DocUrl == "/docs/user/" + expected) + 1;
                    if (rank == 1) b1++;
                    if (rank >= 1) b5++;
                }

                // 🚨 沒有規則題這兩欄的話，這張表會選出 N=0——那是「把 1070 段語料整批關掉」，
                //    而上面每一個指標都量不到那件事（它們的正解全在手冊裡）。
                var ruleZh = await RuleHitsAsync(ManualQuestionBank.RuleGrounded, quota);
                var ruleEn = await RuleHitsAsync(ManualQuestionBank.RuleGroundedEn, quota);

                // §11.2 實測：0.7105 token/字、85 prompt token/秒。
                var seconds = chars.Average() * 0.7105 / 85.0;

                _out.WriteLine($"  {(quota is null ? "共用" : quota.ToString()!.PadLeft(2) + "  "),-4}" +
                               $"  {crossFull,2}/{cross.Length}" +
                               $"        {b1,2}     {b5,2}" +
                               $"        {ruleZh}/{ManualQuestionBank.RuleGrounded.Length}" +
                               $" {ruleEn}/{ManualQuestionBank.RuleGroundedEn.Length}" +
                               $"        {chars.Average(),6:F0}          {seconds,5:F1} 秒");
            }

            _out.WriteLine("※ 「共用」是原行為（topK 個名額兩個語料別一起競爭）。");
            _out.WriteLine("※ N=0 是「完全不給擷取物名額」——規則題那兩欄會直接歸零，它是上界參考不是候選值。");
            _out.WriteLine("※ 字數換算：§11.2 實測 0.7105 token/字、85 tok/s。");

            // ── 相鄰選項：不分艙，直接把 topK 往上加 ────────────────────────
            // #48 只往下量過（5→4→3）。分艙的代價其實來自「手冊塊比擷取物大七倍」，
            // 而加大 topK 兩個語料別會一起變多——所以它每一格的邊際成本比分艙低。
            // 不量它就下「分艙不划算」的結論，等於沒有比較過最明顯的替代方案。
            _out.WriteLine("");
            _out.WriteLine("共用名額、直接加大 topK（對照組）");
            _out.WriteLine("  topK  跨頁題全中   直白@1  直白@5   規則題(中/英)   prompt 平均字數   估算 prefill");

            foreach (var k in new[] { 5, 6, 7, 8 })
            {
                var crossFull = 0;
                foreach (var q in cross)
                {
                    var urls = (await SearchAsync(q.Question, k))
                        .Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                    if (q.Expected.All(e => urls.Contains("/docs/user/" + e))) crossFull++;
                }

                var b1 = 0; var b5 = 0; var chars = new List<int>();
                foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
                {
                    var hits = await SearchAsync(question, k);
                    chars.Add(QaPromptBuilder.BuildUserPrompt(question, hits).Length);
                    var rank = hits.ToList().FindIndex(h => h.DocUrl == "/docs/user/" + expected) + 1;
                    if (rank == 1) b1++;
                    if (rank >= 1) b5++;
                }

                var ruleZh = await RuleHitsAsync(ManualQuestionBank.RuleGrounded, null, k);
                var ruleEn = await RuleHitsAsync(ManualQuestionBank.RuleGroundedEn, null, k);
                var seconds = chars.Average() * 0.7105 / 85.0;

                _out.WriteLine($"   {k}      {crossFull,2}/{cross.Length}" +
                               $"        {b1,2}     {b5,2}" +
                               $"        {ruleZh}/{ManualQuestionBank.RuleGrounded.Length}" +
                               $" {ruleEn}/{ManualQuestionBank.RuleGroundedEn.Length}" +
                               $"        {chars.Average(),6:F0}          {seconds,5:F1} 秒");
            }
            _out.WriteLine("※ 直白@5 這一欄在 topK>5 時已經不是 @5——它是「正解有沒有進 topK」，隨 k 放寬。");

            Assert.NotEmpty(cross);
        }

        /// <summary>某一組規則題裡，有幾題的規則段落進得了 topK。</summary>
        private async Task<int> RuleHitsAsync(HardQuestion[] bank, int? quota, int topK = TopK)
        {
            var hit = 0;
            foreach (var q in bank)
            {
                var urls = (await SearchAsync(q.Question, topK, quota))
                    .Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                if (q.Expected.Where(e => e.StartsWith("rules/", StringComparison.Ordinal))
                              .Any(e => urls.Contains("/docs/user/" + e))) hit++;
            }
            return hit;
        }

        /// <summary>是不是擷取物。判斷用 <c>DocUrl</c> 前綴，與 <c>ManualContextBudgetTests</c> 同一個依據。</summary>
        private static bool IsExtract(DocSearchHit hit) =>
            hit.DocUrl.StartsWith(ExtractManifestImporter.RulesUrlRoot, StringComparison.Ordinal);

        // ── 輔助（與 ManualContextBudgetTests 同形）──────────────────────────

        private async Task<(int Manual, int Extract)> CorpusSizesAsync()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            return (await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Manual),
                    await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract));
        }

        private async Task<ExtractImportResult> ImportRepoManifestsAsync()
        {
            var dir = Path.Combine(RepoRoot(), ExtractManifestImporter.DefaultManifestDir);
            Assert.True(Directory.Exists(dir), $"找不到擷取物目錄 {dir}。");

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

        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(
            string question, int topK, int? extractSlots = null)
        {
            using var scope = _app.Services.CreateScope();

            // 只把分艙配額疊上去——換一份乾淨的 IConfiguration 會連 RetrievalMode／
            // LexicalWeights 一起換掉，那時兩臂之間差的就不只是分艙。
            var config = new ConfigurationBuilder()
                .AddConfiguration(scope.ServiceProvider.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ManualSearchService.ExtractSlotsKey] = extractSlots?.ToString() ?? "",
                })
                .Build();

            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new DiagnosisUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                config,
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK);
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

            throw new DirectoryNotFoundException("找不到 repo 根目錄（.git）");
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

        private sealed class DiagnosisUserContext : IUserContext
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
