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
    /// **topK 與參考資料字數對 prefill 的影響**（規格 §11.2、待決 #48 之二）。
    ///
    /// <para><b>量的是「餵進去多少字」，不是「答得好不好」。</b>§11.2 已經量到
    /// 延遲的 93% 在 prefill、每多 1000 個 prompt token 約多 12 秒，
    /// 所以「少餵 token」是唯一還有槓桿的方向。而降 topK 到底省多少，
    /// 取決於檢索**實際**回了哪些塊——手冊塊平均 647 字、擷取物平均 97 字，
    /// 差了將近七倍，猜不出來。</para>
    ///
    /// <para><b>不跑生成</b>：跑一輪 35 題 × 3 個 topK 的真實生成在 CPU 上是一個多小時，
    /// 而它回答不了這裡要問的問題。字數是確定性的、可重跑的；
    /// 換算成秒用的是 §11.2 已經量過的 85 tok/s，不在這裡重量一次。</para>
    ///
    /// <para><b>刻意不設任何門檻</b>：這支測試的用途是決定 topK 要不要降，
    /// 決定完之後守門的是既有的檢索與生成基準。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualContextBudgetTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        /// <summary>要比較的 topK。5 是現行值（<c>QaAssistantController.TopK</c>）。</summary>
        private static readonly int[] TopKs = { 5, 4, 3 };

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;
        private int _extractRowsOnEntry;

        public ManualContextBudgetTests(AuthedScheduleFactory factory, ITestOutputHelper output)
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

        /// <summary>還原測試庫。理由同 <c>ManualLexicalWeightSweepTests.DisposeAsync</c>。</summary>
        public async Task DisposeAsync()
        {
            if (_extractRowsOnEntry > 0) return;

            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        [LiveOllamaFact]
        public async Task 量測_降低_topK_能省下多少_prefill()
        {
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型。預設檢索模式是 semantic-first，沒有語意通道量到的會是另一條路。");

            // 擷取物必須在場：它們與手冊在同一次排序裡競爭 topK 的名額，
            // 而兩者的塊長差了七倍——少了它們，量到的字數分佈不會是正式環境的分佈。
            var imported = await ImportRepoManifestsAsync();
            var (embedded, pending) = await EmbedAllAsync();
            Assert.Equal(0, pending);

            var (manual, extract) = await CorpusSizesAsync();
            _out.WriteLine($"語料：手冊 {manual} 塊、擷取物 {extract} 塊（已核可 {imported.EntriesApproved}）；" +
                           $"本輪補嵌入 {embedded} 塊");
            _out.WriteLine($"參考資料字數上限 {QaPromptBuilder.DefaultContextBudget} 字（超過的整塊丟掉）");
            _out.WriteLine("");

            foreach (var topK in TopKs)
            {
                var promptChars = new List<int>();
                var extractShare = new List<double>();
                var dropped = 0;
                var ranks = new List<int>();

                foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
                {
                    var hits = await SearchAsync(question, topK);
                    var prompt = QaPromptBuilder.BuildUserPrompt(question, hits);

                    promptChars.Add(prompt.Length);
                    if (hits.Count > 0)
                        extractShare.Add(hits.Count(h => h.DocUrl.StartsWith(
                            ExtractManifestImporter.RulesUrlRoot, StringComparison.Ordinal)) / (double)hits.Count);

                    // 預算把塊整塊丟掉時，prompt 裡的段數會少於檢索回的塊數。
                    // 這個數字若大於 0，代表降 topK 其實省不到那麼多——預算已經先砍過一輪了。
                    var kept = hits.Count(h => prompt.Contains(h.DocUrl[QaPromptBuilder.DocRoot.Length..],
                        StringComparison.Ordinal));
                    dropped += hits.Count - kept;

                    var idx = hits.ToList().FindIndex(h => h.DocUrl == "/docs/user/" + expected);
                    ranks.Add(idx < 0 ? 0 : idx + 1);
                }

                var sorted = promptChars.OrderBy(x => x).ToList();
                var avg = promptChars.Average();
                var p90 = sorted[(int)(sorted.Count * 0.9)];

                _out.WriteLine(
                    $"topK={topK}  prompt 平均 {avg,6:F0} 字／中位 {sorted[sorted.Count / 2],5} ／p90 {p90,5} ／max {sorted[^1],5}" +
                    $"　擷取物佔比 {extractShare.Average():P0}　被預算丟掉 {dropped} 塊" +
                    $"　命中 topK 的題數 {ranks.Count(r => r > 0)}/{ranks.Count}");
            }

            _out.WriteLine("");
            _out.WriteLine("※ 換算：§11.2 實測 CPU 約 85 prompt token/秒、每多 1000 token 約多 12 秒。");
            _out.WriteLine("※ 「命中 topK 的題數」下降代表降 topK 會讓那幾題的正解根本不在參考資料裡——");
            _out.WriteLine("   那時模型只能拒答，省下來的時間沒有意義。");

            // ── 難題題庫 ──────────────────────────────────────────────────
            // 35 題那一份是回歸守門用的，題目相對直白；要決定「敢不敢降 topK」，
            // 憑據必須來自沒有飽和的那一份（定案 #40）。
            // 跨頁題尤其關鍵：它天生就需要不只一塊，降 topK 首先砍掉的就是這一類。
            _out.WriteLine("");
            _out.WriteLine($"難題題庫 {ManualQuestionBank.Hard.Length} 題（定案 #40）");
            foreach (var topK in TopKs)
            {
                var full = 0;
                var partial = 0;
                var chars = new List<int>();
                var crossFull = 0;
                var crossTotal = ManualQuestionBank.Hard.Count(q => q.Kind == HardKind.CrossPage);

                foreach (var q in ManualQuestionBank.Hard)
                {
                    var hits = await SearchAsync(q.Question, topK);
                    chars.Add(QaPromptBuilder.BuildUserPrompt(q.Question, hits).Length);

                    var urls = hits.Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                    var hitCount = q.Expected.Count(e => urls.Contains("/docs/user/" + e));

                    if (hitCount == q.Expected.Length) full++;
                    if (hitCount > 0) partial++;
                    if (q.Kind == HardKind.CrossPage && hitCount == q.Expected.Length) crossFull++;
                }

                _out.WriteLine(
                    $"topK={topK}  prompt 平均 {chars.Average(),6:F0} 字" +
                    $"　全部正解都在 topK {full,2}/{ManualQuestionBank.Hard.Length}" +
                    $"　至少一個 {partial,2}/{ManualQuestionBank.Hard.Length}" +
                    $"　跨頁題全中 {crossFull}/{crossTotal}");
            }

            Assert.NotEmpty(TopKs);
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

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

        /// <summary>走正式的檢索路徑與正式的合併模式——量的必須是使用者實際會拿到的那幾塊。</summary>
        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(string question, int topK)
        {
            using var scope = _app.Services.CreateScope();
            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new BudgetUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
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

        private sealed class BudgetUserContext : IUserContext
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
