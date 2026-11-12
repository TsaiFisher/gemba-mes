using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartFactory.MES.IntegrationTests.MFG;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.IntegrationTests.QA
{
    /// <summary>
    /// 擷取物 manifest 匯入器的守門（規格 §10.11.9）。
    ///
    /// <para><b>這裡幾乎每一條驗的都是「不該發生的事」。</b>擷取物這條路上的失效
    /// 沒有一種會拋例外給使用者看：過期的規則會被自信地引用、被清空的語料只是「答不出來」、
    /// 未核可的段落若漏出去也不會有任何訊號。功能面（放了就查得到）反而是最不需要測的部分。</para>
    ///
    /// <para>沿用 "ScheduleIntegration" collection，理由同 <see cref="ManualIndexIntegrationTests"/>：
    /// 本專案所有整合測試共用同一個 PostgreSQL 測試庫。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ExtractManifestImporterTests : IAsyncLifetime
    {
        private readonly WebApplicationFactory<Program> _app;
        private readonly string _dir;

        public ExtractManifestImporterTests(AuthedScheduleFactory factory)
        {
            _app = factory;
            _dir = Path.Combine(Path.GetTempPath(), "qa-extract-manifests", Guid.NewGuid().ToString("N"));
        }

        public Task InitializeAsync() => CleanAsync();

        public async Task DisposeAsync()
        {
            await CleanAsync();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        /// <summary>
        /// 擷取物是本檔自己種的，跑完必須清乾淨——測試庫是跨進程共用的，
        /// 殘留會讓別的測試因為與它們無關的原因而紅或假綠。
        /// </summary>
        private async Task CleanAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .ExecuteDeleteAsync();
        }

        // ── 過期閘門（§10.11.3） ─────────────────────────────────────────

        [Fact]
        public async Task 來源檔未變動時不算過期且核可的段落可檢索()
        {
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);

            var result = await ImportAsync();

            Assert.Equal(1, result.ManifestsScanned);
            Assert.Equal(0, result.EntriesStale);
            Assert.Equal(1, result.EntriesApproved);

            var row = await SingleRowAsync();
            Assert.False(row.IsStale);
            Assert.True(row.Approved);
            Assert.Equal(source, row.SourcePath);

            // 引文頁碼的形狀必須與手冊一致（rules/<slug>.html）——2026-08-13 的三輪實測
            // 顯示模型是在對齊一個具體字面，換形狀等於在沒量測的情況下動引文行為。
            Assert.Equal("/docs/user/rules/currency-rate-direction.html", row.DocUrl);
            Assert.Equal("rules/currency-rate-direction.html", QaPromptBuilder.PageCode(row.DocUrl));
        }

        [Fact]
        public async Task 來源檔內容變了就整份標記過期()
        {
            // manifest 記的是「這批文字根據哪一版原文寫的」。對不上就是過期，
            // 而過期的擷取物會用非常有自信的語氣講舊規則，且中間產物沒有人會去看。
            WriteManifest("currency", ".claude/specifications/currency.md", new string('a', 64), approved: true);

            var result = await ImportAsync();

            Assert.Equal(1, result.EntriesStale);
            Assert.True((await SingleRowAsync()).IsStale);
        }

        [Fact]
        public async Task 來源檔不存在也算過期而不是略過檢查()
        {
            // 「找不到就跳過檢查」會讓檔案被改名或刪掉變成悄悄放行——
            // 而那正是最需要擋下來的一種來源變動。
            WriteManifest("gone", ".claude/specifications/this-file-was-deleted.md",
                new string('b', 64), approved: true);

            var result = await ImportAsync();

            Assert.Equal(1, result.EntriesStale);
            Assert.True((await SingleRowAsync()).IsStale);
        }

        // ── 最危險的那一條：正式機沒有 manifest 目錄 ─────────────────────

        [Fact]
        public async Task manifest目錄不存在時什麼都不做_一列都不能刪()
        {
            // 正式機的正常狀態：擷取物索引在建置期寫進 DB 並隨 DB 出貨，
            // manifest 與來源都不進 publish 產物（§10.11.6 實測）。
            //
            // 若在目錄缺席時照常執行「manifest 沒提到的就刪」，**每一次正式機啟動都會
            // 把整批出貨的擷取物清空，而下一次啟動也補不回來**。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();
            Assert.Equal(1, await ExtractCountAsync());

            Directory.Delete(_dir, recursive: true);
            var result = await ImportAsync();

            Assert.Equal(0, result.ManifestsScanned);
            Assert.Equal(0, result.ManifestsRemoved);
            Assert.Equal(1, await ExtractCountAsync());
        }

        [Fact]
        public async Task manifest被刪掉時對應的段落要跟著消失()
        {
            // 與上一條的差別只有一個：目錄還在。**目錄在＝這台機器是索引的來源，
            // manifest 沒提到的就是被移除的**；目錄不在＝這台機器只是消費者，不做任何推論。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            WriteManifest("second", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();
            Assert.Equal(2, await ExtractCountAsync());

            File.Delete(Path.Combine(_dir, "second.json"));
            var result = await ImportAsync();

            Assert.Equal(1, result.ManifestsRemoved);
            Assert.Equal(1, await ExtractCountAsync());
        }

        // ── 格式檢查一律爆掉，不略過 ─────────────────────────────────────

        [Theory]
        [InlineData("Bad_Slug", "zh", "有內容", "slug 不合法會產生一個永遠對不上的引文頁碼")]
        [InlineData("ok-slug", "jp", "有內容", "語系寫錯會讓那一段對兩種語系的使用者都消失（§10.4 是硬過濾）")]
        [InlineData("ok-slug", "zh", "   ", "空白內文會佔一列卻永遠檢索不到")]
        public async Task 格式不合法的manifest讓整批中止(string slug, string lang, string text, string why)
        {
            WriteManifestRaw("bad", new ExtractManifest
            {
                SourcePath = ".claude/specifications/currency.md",
                SourceHash = new string('c', 64),
                Entries = new List<ExtractEntry>
                {
                    new() { Slug = slug, Lang = lang, HeadingPath = "標題", Text = text, Approved = false },
                },
            });

            // 略過壞掉那一份的後果是那些規則在 Q&A 中永久消失，而且沒有任何訊號——
            // 使用者只會覺得助理不知道這件事。故整批中止，與 DocIndexBuilder 同一立場。
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync());
            Assert.Contains("bad", ex.Message, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(why));
        }

        [Fact]
        public async Task 同一份裡slug重複要爆掉()
        {
            // 兩段共用一個引文頁碼，使用者點進去只會看到其中一段。
            WriteManifestRaw("dup", new ExtractManifest
            {
                SourcePath = ".claude/specifications/currency.md",
                SourceHash = new string('d', 64),
                Entries = new List<ExtractEntry>
                {
                    new() { Slug = "same", Lang = "zh", HeadingPath = "一", Text = "甲", Approved = false },
                    new() { Slug = "same", Lang = "zh", HeadingPath = "二", Text = "乙", Approved = false },
                },
            });

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync());
            Assert.Contains("重複", ex.Message, StringComparison.Ordinal);
        }

        // ── 核可（§10.11.4） ─────────────────────────────────────────────

        [Fact]
        public async Task 未核可的段落照樣匯入但標記為未核可()
        {
            // 保留而不丟棄是刻意的：審核者要看得到「這一段被產出來但我沒放行」，
            // 丟掉的話下一次產生器會再產一次同樣的東西。它由 DocChunkFilter 擋在檢索之外。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: false);

            var result = await ImportAsync();

            Assert.Equal(1, result.EntriesTotal);
            Assert.Equal(0, result.EntriesApproved);
            Assert.False((await SingleRowAsync()).Approved);
        }

        [Fact]
        public async Task 擷取物不帶MenuKey與Module()
        {
            // 帶了的話它會同時落進手冊那條 MenuKey 分支，變成兩套權限規則都套用——
            // 而 SQL 的 OR 只要一邊成立就通過，實際效果是**核可與過期閘門被繞過**。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();

            var row = await SingleRowAsync();
            Assert.Equal("", row.MenuKey);
            Assert.Equal("", row.Module);
            Assert.NotEqual("", row.Lexemes);
        }

        [Fact]
        public async Task 內容沒變的_manifest_原地不動_向量留著()
        {
            // 這一條守的是**開發與量測的熱路徑**：重寫會把該份的向量一起清掉，
            // 而重嵌 1288 段在開發機 CPU 上約 30 分鐘，於是每一次啟動都付一次。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();

            await StampEmbeddingAsync();

            var again = await ImportAsync();

            Assert.Equal(1, again.ManifestsUnchanged);
            Assert.Equal(0, again.EntriesRewritten);
            Assert.Equal(1, again.EntriesTotal);

            var row = await SingleRowAsync();
            Assert.NotNull(row.Embedding);
            Assert.Equal(ProbeModel, row.EmbeddingModel);
        }

        [Fact]
        public async Task 核可翻轉一定要重寫_即使一個字都沒改()
        {
            // **這是「拿 ContentHash 當跳過依據」會踩的那個坑。**
            // ContentHash 只算 text，而退回一則不會改動任何一個字——
            // 以它為準的話，退回會安靜地不生效：檢索照樣撈得到那一段。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();

            var before = await SingleRowAsync();
            Assert.True(before.Approved);

            WriteManifest("currency", source, HashOfRepoFile(source), approved: false);
            var again = await ImportAsync();

            Assert.Equal(0, again.ManifestsUnchanged);
            Assert.Equal(1, again.EntriesRewritten);
            Assert.False((await SingleRowAsync()).Approved);
        }

        [Fact]
        public async Task 重新匯入是全量重寫而不是疊加()
        {
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);

            await ImportAsync();
            await ImportAsync();

            Assert.Equal(1, await ExtractCountAsync());
        }

        [Fact]
        public async Task 手冊重建之後擷取物仍在()
        {
            // 與 ManualIndexIntegrationTests 那條同一個陷阱，換從匯入這一側再驗一次：
            // 擷取物的來源檔不在 wwwroot/docs/user 底下，未收斂範圍的重建會把它們
            // 全部判為「已刪除的手冊頁」。
            var source = ".claude/specifications/currency.md";
            WriteManifest("currency", source, HashOfRepoFile(source), approved: true);
            await ImportAsync();

            using (var scope = _app.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>().RebuildAsync();

            Assert.Equal(1, await ExtractCountAsync());
        }

        /// <summary>
        /// **版控裡真正的那幾份 manifest 必須通過匯入器的格式檢查。**
        ///
        /// <para>這一條擋的是核可流程本身：核可＝人把 <c>approved</c> 改成 true 再送 PR
        /// （定案 #45），也就是說**手改 JSON 是正常作業，不是意外**。改壞一個逗號的後果
        /// 不是這份不生效，而是整批中止——`Validate` 一律拋例外——症狀是正式機啟動時
        /// 所有規則類問題一起答不出來，而手冊照常運作，看起來完全不像匯入壞了。</para>
        ///
        /// <para><b>刻意不驗 stale。</b>來源檔改了而 manifest 沒重生，設計上由執行期處理
        /// （stale 直接排除於檢索之外，§10.11.3）——那是「寧可少答」的安全方向。
        /// 在 CI 加一道硬閘門會把安全的降級變成建置失敗，而且會逼所有改規格的人
        /// 先在本機跑一次 LLM 重生擷取物，連改錯字都要。**過期的可見性歸畫面管，不歸 CI 管。**</para>
        /// </summary>
        [Fact]
        public async Task 版控裡的每一份manifest都要通得過格式檢查()
        {
            var dir = Path.Combine(RepoRoot(), ExtractManifestImporter.DefaultManifestDir);
            if (!Directory.Exists(dir)) return;   // 還沒有任何擷取物時本條無事可做

            var files = Directory.GetFiles(dir, "*.json");
            if (files.Length == 0) return;

            using var scope = _app.Services.CreateScope();
            var importer = new ExtractManifestImporter(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ExtractManifestImporter.ManifestDirKey] = dir,
                }).Build(),
                scope.ServiceProvider.GetRequiredService<ILogger<ExtractManifestImporter>>());

            var result = await importer.ImportAsync();

            Assert.Equal(files.Length, result.ManifestsScanned);
            Assert.True(result.EntriesTotal > 0,
                $"{files.Length} 份 manifest 一則段落都沒寫進去——多半是 entries 空了。");
        }

        // ── 輔助 ─────────────────────────────────────────────────────────

        private async Task<ExtractImportResult> ImportAsync()
        {
            using var scope = _app.Services.CreateScope();

            // 直接建構受測服務並指定 manifest 目錄：組態要在 Host 建立前就設好才會生效，
            // 而本檔每個測試都要自己一組 manifest。理由同 ManualIndexIntegrationTests.SearchAsync。
            var importer = new ExtractManifestImporter(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ExtractManifestImporter.ManifestDirKey] = _dir,
                }).Build(),
                scope.ServiceProvider.GetRequiredService<ILogger<ExtractManifestImporter>>());

            return await importer.ImportAsync();
        }

        private async Task<Modules.QA.Models.DocChunk> SingleRowAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .OrderBy(c => c.Id)
                .FirstAsync();
        }

        /// <summary>假裝這一列已經被嵌入過。用來驗「原地不動」真的把向量留下來了。</summary>
        private const string ProbeModel = "probe-model";

        private async Task StampEmbeddingAsync()
        {
            using var scope = _app.Services.CreateScope();
            var qa = scope.ServiceProvider.GetRequiredService<QaDbContext>();
            var row = await qa.DocChunks
                .Where(c => c.Corpus == DocCorpus.Extract)
                .OrderBy(c => c.Id)
                .FirstAsync();

            row.Embedding = new[] { 0.1f, 0.2f, 0.3f };
            row.EmbeddingModel = ProbeModel;
            row.EmbeddingDim = 3;
            await qa.SaveChangesAsync();
        }

        private async Task<int> ExtractCountAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<QaDbContext>().DocChunks
                .CountAsync(c => c.Corpus == DocCorpus.Extract);
        }

        private void WriteManifest(string name, string sourcePath, string sourceHash, bool approved) =>
            WriteManifestRaw(name, new ExtractManifest
            {
                SourcePath = sourcePath,
                SourceHash = sourceHash,
                Entries = new List<ExtractEntry>
                {
                    new()
                    {
                        Slug = "currency-rate-direction",
                        Lang = "zh",
                        HeadingPath = "匯率換算 › 方向慣例",
                        Text = "本幣金額等於外幣金額乘以匯率。單據上的匯率是建立當下的快照，之後匯率變動不會回頭改已開立的單據。",
                        Approved = approved,
                    },
                },
            });

        private void WriteManifestRaw(string name, ExtractManifest manifest)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(
                Path.Combine(_dir, name + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                Encoding.UTF8);
        }

        /// <summary>
        /// 拿 repo 裡真的存在的檔案算 SHA256——**必須與匯入器用同一種算法**，
        /// 測試自己抄一份的話，兩份漂移的方向恰好是「測試仍綠、實際全部誤判過期」。
        /// </summary>
        private static string HashOfRepoFile(string relative)
        {
            var root = RepoRoot();
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"測試依賴的來源檔不存在：{full}");

            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(File.ReadAllText(full)))).ToLowerInvariant();
        }

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

            throw new DirectoryNotFoundException("找不到 repo 根目錄（.git）");
        }
    }
}
