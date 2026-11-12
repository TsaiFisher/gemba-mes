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
    /// **詞彙通道標題權重的掃描量測**（規格 §10.9、待決 #48）。
    ///
    /// <para><b>這支測試的價值在印出來的表，不在它綠不綠。</b>它一次跑完數組
    /// <c>ts_rank</c> 權重，用的是同一份題庫、同一份索引、同一組權限——
    /// 分幾次跑的話，兩組數字之間差的不只是權重，還差一次索引重建與別的進程
    /// 對共用測試庫做過的事。</para>
    ///
    /// <para><b>對照臂是「等重」（<c>1,1,1,1</c>）而不是舊程式碼。</b>兩欄化之後
    /// 舊的單欄查詢已經不存在，硬留一份下來只會變成第二套永遠沒人維護的 SQL。
    /// 等重臂在語意上就是加權前的行為——標題與內文一樣重，正是 #48 說的那個問題。
    /// <b>而且不只是語意上等價</b>：2026-08-26 以 psql 逐列比對過
    /// <c>ts_rank('{1,1,1,1}', setweight(標題,'A') ‖ setweight(內文,'D'), q)</c> 與
    /// <c>ts_rank('{1,1,1,1}', to_tsvector(標題詞 ‖ ' ' ‖ 內文詞), q)</c>——
    /// zh 194 列、en 492 列、兩種 regconfig，<b>分數不同的列 0 筆、最大差 0</b>。
    /// 拆成兩欄本身沒有改變任何名次，變的只有權重。</para>
    ///
    /// <para><b>一律走純詞彙</b>：權重只影響詞彙通道。預設檢索模式是 semantic-first
    /// （定案 #27），量預設模式只會量到語意通道，權重怎麼調都看不出差別。</para>
    ///
    /// <para><b>刻意不設任何品質門檻。</b>首測值當目標值是反模式；而且這支測試的
    /// 用途是「選一組權重」，選完之後守門的是 <c>ManualRetrievalBenchmarkTests</c>。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualLexicalWeightSweepTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        /// <summary>
        /// 掃描的候選權重，順序是 PostgreSQL 規定的 <c>{D, C, B, A}</c>——本系統只用 D（內文）與 A（標題）。
        ///
        /// <para>倍率取 1／2／4／10／40 而不是等距：這種權重的效果是乘性的，
        /// 等距取樣會把力氣全花在「幾乎沒差」的那一段。10 倍那一格就是 PostgreSQL 的預設值。</para>
        /// </summary>
        private static readonly (string Label, string Weights)[] Sweep =
        {
            ("等重", "1,1,1,1"),
            ("1.5倍", "0.667,0.75,0.85,1.0"),
            ("2倍", "0.5,0.6,0.8,1.0"),
            ("3倍", "0.333,0.45,0.7,1.0"),
            ("4倍", "0.25,0.4,0.7,1.0"),
            ("10倍", "0.1,0.2,0.4,1.0"),
        };

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;

        public ManualLexicalWeightSweepTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _app = factory;
            _out = output;
        }

        /// <summary>進場時測試庫裡有幾列擷取物。用途見 <see cref="DisposeAsync"/>。</summary>
        private int _extractRowsOnEntry;

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
        /// **把測試庫還原成進場時的樣子。**
        ///
        /// <para>這支測試為了量稀釋會把版控裡的 manifest 匯進去，而
        /// <b>測試庫是跨進程共用的</b>（庫名寫死）：留著那 1288 列，
        /// 之後每一支檢索測試量到的都是另一份語料，
        /// 而它們不會報錯——只是數字悄悄變了，還會被當成自己的回歸。</para>
        ///
        /// <para>放在 <c>DisposeAsync</c> 而不是測試結尾：測試中途失敗時更需要還原，
        /// 否則一次紅燈會污染之後每一輪。**只在進場時本來就沒有擷取物才刪**——
        /// 別的測試（<c>ManualExtractImpactBenchmarkTests</c>）刻意留下的狀態不該被這裡清掉。</para>
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_extractRowsOnEntry > 0) return;

            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        [Fact]
        public async Task 量測_標題權重對詞彙通道的影響()
        {
            // ── 兩個語料臂 ──────────────────────────────────────────────
            // 加權在「只有手冊」與「手冊＋擷取物」底下不見得是同一個結論，而兩者只差一個變因。
            // 少了第一臂就分不出「加權本身沒有用」與「加權把擷取物推上來把手冊擠掉」——
            // 這兩件事的處置方式完全相反（前者是放棄，後者是分語料別給權重）。
            var removed = await DeleteExtractRowsAsync();
            _out.WriteLine($"【第一臂】只有手冊（清掉 {removed} 列擷取物）");
            var manualOnly = await SweepArmAsync();

            var imported = await ImportRepoManifestsAsync();
            Assert.True(imported.EntriesApproved > 0,
                $"匯入 {imported.EntriesTotal} 段但已核可 0 段——檢索只吃 Approved 的列，這一臂沒有競爭者。");

            var (manual, extract) = await CorpusSizesAsync();
            _out.WriteLine("");
            _out.WriteLine($"【第二臂】手冊 {manual} 塊 ＋ 擷取物 {extract} 塊（已核可 {imported.EntriesApproved}）");
            var withExtract = await SweepArmAsync();

            // ── 逐題名次（第二臂＝正式環境的語料）─────────────────────────
            // 彙總數字說得出「哪一組比較好」，說不出「好在哪一類提問」——
            // 而後者才是決定要不要採用的依據：專有名詞題變好、口語題變差，是完全不同的結論。
            _out.WriteLine("");
            _out.WriteLine("逐題名次（第二臂，0＝前 10 名外）");
            _out.WriteLine("提問                                    " +
                           string.Join("", Sweep.Select(s => $"{s.Label,7}")));
            for (var i = 0; i < ManualRetrievalBenchmarkTests.Bank.Length; i++)
            {
                var row = Sweep.Select(s => withExtract[s.Label][i]).ToList();
                if (row.Distinct().Count() == 1 && row[0] == 1) continue;   // 每一組都第 1 名的題目不必印
                _out.WriteLine(
                    $"{ManualRetrievalBenchmarkTests.Bank[i].Question,-40}" +
                    string.Join("", row.Select(r => $"{r,7}")));
            }

            // 唯一的斷言：各組真的跑出了不同的東西。
            // 權重沒有生效時（例如索引還停在舊格式、HeadingLexemes 全空），
            // 這支測試會印出一份每一欄都一模一樣的漂亮報表，而且是綠的。
            var distinct = withExtract.Values.Select(r => string.Join(",", r)).Distinct().Count();
            Assert.True(distinct > 1,
                $"{Sweep.Length} 組權重跑出完全相同的名次——權重沒有生效。" +
                "先確認 HeadingLexemes 有值（索引格式版號有沒有跟著 bump）。");

            Assert.NotEqual(
                string.Join(",", manualOnly[Sweep[0].Label]),
                string.Join(",", withExtract[Sweep[0].Label]));
        }

        /// <summary>把一個語料狀態下的每一組權重都跑完，回傳「權重標籤 → 逐題名次」。</summary>
        private async Task<Dictionary<string, List<int>>> SweepArmAsync()
        {
            var byLabel = new Dictionary<string, List<int>>();
            foreach (var (label, weights) in Sweep)
            {
                var ranks = new List<int>();
                foreach (var (question, expected) in ManualRetrievalBenchmarkTests.Bank)
                {
                    var hits = await SearchAsync(question, topK: 10, weights);
                    var idx = hits.ToList().FindIndex(h => h.DocUrl == "/docs/user/" + expected);
                    ranks.Add(idx < 0 ? 0 : idx + 1);
                }

                byLabel[label] = ranks;
                Report(label, ranks);
            }

            return byLabel;
        }

        private async Task<int> DeleteExtractRowsAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        // ── 輔助 ────────────────────────────────────────────────────────────

        private void Report(string label, List<int> ranks)
        {
            var n = ranks.Count;
            var top1 = ranks.Count(r => r == 1);
            var top3 = ranks.Count(r => r is >= 1 and <= 3);
            var top5 = ranks.Count(r => r is >= 1 and <= 5);
            var miss = ranks.Count(r => r == 0);
            var mrr = ranks.Where(r => r > 0).Sum(r => 1.0 / r) / n;
            _out.WriteLine(
                $"{label,-22} Recall@1={top1,2}/{n} @3={top3,2}/{n} @5={top5,2}/{n} " +
                $"MRR={mrr:F3} 前 10 名外={miss}");
        }

        private async Task<(int Manual, int Extract)> CorpusSizesAsync()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            return (await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Manual),
                    await qa.DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract));
        }

        /// <summary>匯入版控裡真正的 manifest——量的必須是即將出貨的那一批，不是測試自己捏的。</summary>
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

        /// <summary>
        /// 換一份只改了權重的組態去跑正式的檢索服務——不是測試專用的旁路，
        /// 量到的就是把那組權重寫進 <c>appsettings</c> 之後會發生的事。
        /// </summary>
        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(string question, int topK, string weights)
        {
            using var scope = _app.Services.CreateScope();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ManualSearchService.LexicalWeightsKey] = weights,
                }).Build();

            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new SweepUserContext(),
                // 純詞彙：權重只影響詞彙通道，而預設模式是語意為主，量它等於量不到權重。
                NoEmbeddingClient.Instance,
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                configuration,
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

        private sealed class SweepUserContext : IUserContext
        {
            public string? ActiveRoleId => RoleId;
            public string? PlantId => "P1";
            public string? TimeZoneId => "Asia/Taipei";
            public string? Language => "zh-TW";
            public string? UserId => "admin";
            public string? ActiveRolePath => null;
            public IReadOnlyList<string> AllowedPlantIds => Array.Empty<string>();
        }

        /// <summary>未設定嵌入模型的 client。<c>ManualSearchService</c> 見到空名稱就不走語意通道。</summary>
        private sealed class NoEmbeddingClient : IEmbeddingClient
        {
            public static readonly NoEmbeddingClient Instance = new();

            public string EmbeddingModelName => string.Empty;

            public Task<IReadOnlyList<float[]>> EmbedAsync(
                IReadOnlyList<string> inputs, EmbeddingKind kind, CancellationToken ct = default) =>
                throw new InvalidOperationException("純詞彙模式不應呼叫嵌入。");
        }
    }
}
