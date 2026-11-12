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
    /// **擷取物跨語系檢索的兩臂量測**（規格 §17.9 缺口 G4，
    /// <c>Features:QaAssistant:ExtractCrossLang</c>）。
    ///
    /// <para><b>要決定的是那個開關的預設值，所以兩件事都得量。</b>只量「英文提問拿不拿得到規則」
    /// 會得到一個必然向上的數字（現況是 0，怎麼動都是進步）；只量「中文提問有沒有變差」
    /// 會得到一個必然不動的數字（擷取物全是中文，中文那臂本來就撈得到）。
    /// 兩個都量才知道這個開關在交易什麼。</para>
    ///
    /// <list type="number">
    ///   <item><b>好處</b>：<see cref="ManualQuestionBank.RuleGroundedEn"/> 六題英文規則題，
    ///     看規則段落進不進得了 topK。</item>
    ///   <item><b>代價</b>：<see cref="ManualRetrievalBenchmarkTests.Bank"/> 裡的六題**英文**題，
    ///     正解全在手冊——中文規則段落擠進來會不會把它們推掉。這是本測試真正要抓的東西。</item>
    ///   <item><b>不該動的</b>：29 題中文題與中文規則題。擷取物都是 <c>zh</c>，
    ///     開關對它們在邏輯上是 no-op；<b>數字若動了就是這次改動有 bug</b>，不是效果。</item>
    /// </list>
    ///
    /// <para><b>「命中規則段落」與「命中任一正解」分開算。</b>
    /// <see cref="ManualQuestionBank.RuleGroundedEn"/> 前兩題的正解含 <c>bas/currency.html</c>，
    /// 而手冊有英文版——只看後者會把手冊的功勞算到擷取物頭上。同一個陷阱
    /// <see cref="ManualQuestionBank.RuleGrounded"/> 的註解已經記過一次。</para>
    ///
    /// <para><b>刻意不設門檻</b>：用途是選預設值，選完之後守門的是既有的檢索基準。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualCrossLangExtractTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const int TopK = 5;

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;
        private int _extractRowsOnEntry;

        public ManualCrossLangExtractTests(AuthedScheduleFactory factory, ITestOutputHelper output)
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

        public async Task DisposeAsync()
        {
            if (_extractRowsOnEntry > 0) return;

            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        [LiveOllamaFact]
        public async Task 量測_擷取物跨語系檢索的好處與代價()
        {
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定嵌入模型。開關只有語意通道拿得到——英文提問對中文 Lexemes 切不出命中。");

            var imported = await ImportRepoManifestsAsync();
            var (_, pending) = await EmbedAllAsync();
            Assert.Equal(0, pending);

            var (manualZh, manualEn, extractZh, extractEn) = await CorpusSizesAsync();
            _out.WriteLine($"語料：手冊 zh {manualZh}／en {manualEn}　擷取物 zh {extractZh}／en {extractEn}" +
                           $"（已核可 {imported.EntriesApproved}）");
            Assert.Equal(0, extractEn);   // 前提：G4 說的就是這件事。哪天不成立了，這支測試要重寫。
            _out.WriteLine("");

            foreach (var crossLang in new[] { false, true })
            {
                _out.WriteLine($"── ExtractCrossLang = {crossLang.ToString().ToLowerInvariant()} ──");

                // ① 好處：英文規則題
                var ruleHit = 0;
                var anyHit = 0;
                foreach (var q in ManualQuestionBank.RuleGroundedEn)
                {
                    var urls = (await SearchAsync(q.Question, TopK, crossLang))
                        .Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);

                    var expectedRules = q.Expected
                        .Where(e => e.StartsWith("rules/", StringComparison.Ordinal))
                        .Select(e => "/docs/user/" + e);

                    if (expectedRules.Any(urls.Contains)) ruleHit++;
                    if (q.Expected.Any(e => urls.Contains("/docs/user/" + e))) anyHit++;
                }
                _out.WriteLine($"  英文規則題 {ManualQuestionBank.RuleGroundedEn.Length} 題：" +
                               $"命中規則段落 {ruleHit}　命中任一正解 {anyHit}");

                // ② 代價：英文手冊題（正解全在手冊）
                //    ⚠️ Recall 這一列在現行語料上是 6/6 ＝ **已飽和**，量不出代價（定案 #40）。
                //    真正看得出擠壓的是 MRR 與「擷取物佔掉幾格」，兩者都沒有天花板。
                var en = await RecallAsync(IsEnglish, crossLang);
                _out.WriteLine($"  英文手冊題 {en.Count} 題：Recall@1 {en.At1}　Recall@5 {en.At5}" +
                               $"　MRR {en.Mrr:F3}　擷取物佔 {en.ExtractSlots}/{en.Count * TopK} 格");

                // ③ 不該動的：中文題
                var zh = await RecallAsync(q => !IsEnglish(q), crossLang);
                _out.WriteLine($"  中文手冊題 {zh.Count} 題：Recall@1 {zh.At1}　Recall@5 {zh.At5}" +
                               $"　MRR {zh.Mrr:F3}　擷取物佔 {zh.ExtractSlots}/{zh.Count * TopK} 格");

                var zhRule = 0;
                foreach (var q in ManualQuestionBank.RuleGrounded)
                {
                    var urls = (await SearchAsync(q.Question, TopK, crossLang))
                        .Select(h => h.DocUrl).ToHashSet(StringComparer.Ordinal);
                    if (q.Expected.Where(e => e.StartsWith("rules/", StringComparison.Ordinal))
                                  .Any(e => urls.Contains("/docs/user/" + e))) zhRule++;
                }
                _out.WriteLine($"  中文規則題 {ManualQuestionBank.RuleGrounded.Length} 題：命中規則段落 {zhRule}");
                _out.WriteLine("");
            }

            _out.WriteLine("※ 判讀：英文規則題上升而英文手冊題不掉 → 開關值得打開；");
            _out.WriteLine("   英文手冊題掉了 → 中文規則段落在跟英文手冊搶名額，要先解決名額問題（同缺口 G5）。");
            _out.WriteLine("※ 中文那兩列兩臂必須完全相同——不同就是這次改動有 bug，不是效果。");

            Assert.NotEmpty(ManualQuestionBank.RuleGroundedEn);
        }

        /// <summary>提問是不是英文。2026-08-31 起共用
        /// <see cref="ManualRetrievalBenchmarkTests.IsEnglish"/>（同一份題庫、同一個判準）——
        /// 原本這裡自己留一份，兩支測試各判一次正是註解要避免的情形。</summary>
        private static bool IsEnglish(string question) =>
            ManualRetrievalBenchmarkTests.IsEnglish(question);

        private async Task<(int At1, int At5, int Count, double Mrr, int ExtractSlots)> RecallAsync(
            Func<string, bool> pick, bool crossLang)
        {
            var at1 = 0;
            var at5 = 0;
            var count = 0;
            var reciprocal = 0d;
            var extractSlots = 0;

            foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
            {
                if (!pick(question)) continue;
                count++;

                var hits = await SearchAsync(question, TopK, crossLang);
                var url = "/docs/user/" + expected;
                var rank = hits.ToList().FindIndex(h => h.DocUrl == url) + 1;

                if (rank == 1) at1++;
                if (rank >= 1) at5++;
                if (rank >= 1) reciprocal += 1d / rank;
                extractSlots += hits.Count(h => h.DocUrl.StartsWith(
                    ExtractManifestImporter.RulesUrlRoot, StringComparison.Ordinal));
            }

            return (at1, at5, count, count == 0 ? 0 : reciprocal / count, extractSlots);
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private async Task<(int ManualZh, int ManualEn, int ExtractZh, int ExtractEn)> CorpusSizesAsync()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            return (await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Manual && c.Lang == "zh"),
                    await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Manual && c.Lang == "en"),
                    await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract && c.Lang == "zh"),
                    await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract && c.Lang != "zh"));
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

        /// <summary>
        /// 走正式檢索路徑，只把開關疊在既有組態之上——
        /// 換一份乾淨的 <c>IConfiguration</c> 會連帶把 <c>RetrievalMode</c>／<c>LexicalWeights</c>
        /// 一起換掉，那時兩臂之間差的就不只是這個開關。
        /// </summary>
        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(
            string question, int topK, bool crossLang)
        {
            using var scope = _app.Services.CreateScope();

            var config = new ConfigurationBuilder()
                .AddConfiguration(scope.ServiceProvider.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ManualSearchService.ExtractCrossLangKey] = crossLang ? "true" : "false",
                })
                .Build();

            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new CrossLangUserContext(),
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

        private sealed class CrossLangUserContext : IUserContext
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
