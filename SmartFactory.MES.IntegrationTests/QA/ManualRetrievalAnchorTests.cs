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
    /// **檢索前提的金絲雀**（接替 §10.8.2 之九那個已經失效的錨）。
    ///
    /// <para><b>要解決的問題。</b>之九能抓到「前提被別的進程改掉」，靠的是
    /// 「檢索給到正解 52/53 這個值對每顆模型都相同、本來就不該動」——
    /// 有人動了權限，它就掉下來。**擷取物語料上線之後那個值會動了**（之十二 ② 實測 52→50），
    /// 於是它再也分不出「語料變多」與「前提壞了」。而它防的正是最難發現的一類錯誤：
    /// 前提壞掉、數字照樣好看、沒有任何紅燈。</para>
    ///
    /// <para><b>替代的不變量：把擷取物排除在候選之外再算同一個值。</b>
    /// 這樣它不隨語料規模動（擷取物再多也不參與），也不隨生成模型動（整條路徑不碰生成模型），
    /// 但**權限一被收窄它就會掉**——權限過濾在 <c>DocChunkFilter</c> 的 SQL 裡，
    /// 手冊分支照樣要比對 <c>MenuKey</c>。</para>
    ///
    /// <para><b>做法是「取深一點再濾」，不是加一個 SQL 參數。</b><c>DocChunkFilter</c> 的
    /// 參數編號 <c>{0}</c>–<c>{2}</c> 是與所有呼叫端的約定（其餘從 <c>{3}</c> 起編），
    /// 多塞一個會把每個呼叫端的編號推移一位。而且不必改：<c>semantic-first</c> 下
    /// 名次是一個總序，**把擷取物從較深的候選裡濾掉再取前 <c>TopK</c> 個手冊塊，
    /// 等同於「只在手冊裡取前 <c>TopK</c>」**。
    /// ⚠️ <b>這個等價只在 <c>semantic-first</c> 成立</b>（定案 #27 的預設）。
    /// RRF 的名次由候選集合決定，濾完再算 ≠ 濾過之後才融合——所以下面會先驗模式。</para>
    ///
    /// <para><b>為什麼這裡可以把首測值寫成常數，而 §10.8.2 的量測不行。</b>
    /// 定案 #29／#40 說「首測值當目標值是反模式」，講的是**品質指標**——
    /// 那是要拿來比較模型好壞的。這一支不量品質，它量的是**環境還是不是原來那個環境**。
    /// 對前提而言，「和上次一樣」正是唯一有意義的斷言。</para>
    ///
    /// <para><b>它掉下來時該做的事，依可能性排序：</b>
    /// ① 有人收窄了角色 1 的權限而沒還原（之九的原形，跨進程共用測試庫時最常見）；
    /// ② 嵌入模型被換掉（向量空間不可比，名次會整批重排）；
    /// ③ 手冊真的改版了。**只有 ③ 該更新下面那個常數，而且要跟手冊的變更同一個 commit**——
    /// 單獨改這個數字等於把金絲雀關掉。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualRetrievalAnchorTests : IAsyncLifetime
    {
        private const string RoleId = "1";
        private const int TopK = 5;

        /// <summary>
        /// 候選取多深。只要大於「擷取物可能佔掉的名額 ＋ <see cref="TopK"/>」即可；
        /// 取 40 是為了讓它與擷取物的則數脫鉤——語料還會再長，
        /// 而這個值一旦不夠深，症狀是錨自己開始隨語料規模動，正好是本測試要防的事。
        /// </summary>
        private const int ProbeDepth = 40;

        /// <summary>
        /// **記錄值，不是目標值**（見類別註解）。2026-08-17 首測：`snowflake-arctic-embed2`、
        /// 手冊 134 頁／652 塊、擷取物 472 段在索引裡但不參與候選。
        /// </summary>
        private const int Anchor = 52;

        private readonly AuthedScheduleFactory _factory;
        private readonly ITestOutputHelper _out;

        public ManualRetrievalAnchorTests(AuthedScheduleFactory factory, ITestOutputHelper output)
        {
            _factory = factory;
            _out = output;
        }

        public async Task InitializeAsync()
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();

            // **刻意把擷取物匯進來。**這一支要證明的正是「擷取物在索引裡也不影響這個值」，
            // 不匯的話它只是在量一個沒有擷取物的世界，證不到任何東西。
            await ImportRepoManifestsAsync();
            await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().EmbedPendingAsync();

            await GrantEveryIndexedKeyAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                scope.ServiceProvider.GetRequiredService<QaDbContext>());
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [LiveOllamaFact]
        public async Task 手冊檢索的錨不隨擷取物語料而動()
        {
            using var scope = _factory.Services.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            // 等價關係只在 semantic-first 成立（見類別註解）。設成 rrf 時這支測試量到的
            // 是另一件事，而它會安靜地給出一個看起來很正常的數字。
            var mode = config[ManualSearchService.RetrievalModeKey];
            Assert.True(
                string.IsNullOrEmpty(mode) ||
                !string.Equals(mode, "rrf", StringComparison.OrdinalIgnoreCase),
                $"{ManualSearchService.RetrievalModeKey}={mode}：RRF 的名次由候選集合決定，" +
                "「取深一點再濾」不等於「只在手冊裡取前 K」，這個錨在該模式下沒有意義。");

            var extractsIndexed = await scope.ServiceProvider.GetRequiredService<QaDbContext>()
                .DocChunks.CountAsync(c => c.Corpus == DocCorpus.Extract);
            Assert.True(extractsIndexed > 0,
                "索引裡一則擷取物都沒有——這一輪證不到「不受擷取物影響」，只是量了一個沒有擷取物的世界。");

            var hit = 0;
            var misses = new List<string>();
            foreach (var q in ManualQuestionBank.Hard)
            {
                var manualTop = await ManualOnlyTopAsync(q.Question);
                if (q.Expected.Any(e => manualTop.Contains(e))) hit++;
                else misses.Add($"  {q.Question,-30} 手冊前 {TopK} 名：{string.Join("、", manualTop)}" +
                                $"／期望 {string.Join("、", q.Expected)}");
            }

            _out.WriteLine($"索引裡的擷取物 {extractsIndexed} 塊（不參與候選）");
            _out.WriteLine($"手冊-only 檢索給到正解：{hit}/{ManualQuestionBank.Hard.Length}（錨 = {Anchor}）");
            foreach (var m in misses) _out.WriteLine(m);

            Assert.True(hit == Anchor,
                $"檢索前提變了：手冊-only 給到正解 {hit}/{ManualQuestionBank.Hard.Length}，錨是 {Anchor}。" +
                "依可能性排序：① 有人收窄了角色 1 的權限而沒還原；② 嵌入模型被換掉；③ 手冊真的改版。" +
                "只有 ③ 該更新 Anchor，而且要跟手冊的變更同一個 commit——單獨改這個數字等於把金絲雀關掉。");
        }

        /// <summary>
        /// 取深一點再濾掉擷取物，回傳手冊的前 <see cref="TopK"/> 個頁面代號。
        /// </summary>
        private async Task<List<string>> ManualOnlyTopAsync(string question)
        {
            using var scope = _factory.Services.CreateScope();
            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new AnchorUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());

            var hits = await service.SearchAsync(question, ProbeDepth);

            return hits
                .Select(h => QaPromptBuilder.PageCode(h.DocUrl))
                .Where(code => !code.StartsWith(QaCitationPresenter.RulePrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(TopK)
                .ToList();
        }

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

        private sealed class AnchorUserContext : IUserContext
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
