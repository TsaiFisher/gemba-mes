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
    /// **難題題庫的檢索端量測**（規格 §10.7「難題題庫」、定案 #40）。
    /// 題目本身與其設計理由見 <see cref="ManualQuestionBank"/>；本檔只負責量。
    ///
    /// <para>四項指標，各自針對一種飽和來源：<b>近義干擾勝出率</b>（排序分辨力）、
    /// <b>需推理 Recall@1/@3</b>（標題幫不上忙的題）、<b>跨頁 Coverage@5</b>、
    /// 以及<b>離題重疊量</b>（§10.8 門檻問題的遺留）。</para>
    ///
    /// <para><b>本檔不設回歸守門值</b>（與舊題庫不同）。這是**選型用的量尺**，
    /// 用途是把候選模型排出高下；在還沒有第二顆模型的數據之前，任何門檻都是拿首測值當目標值，
    /// 而那正是 §10.9 已經寫過不要做的事。唯一的斷言是「有東西可量」。</para>
    /// </summary>
    [Collection("ScheduleIntegration")]
    public class ManualHardBenchmarkTests : IAsyncLifetime
    {
        private const string RoleId = "1";

        private readonly WebApplicationFactory<Program> _app;
        private readonly ITestOutputHelper _out;

        public ManualHardBenchmarkTests(AuthedScheduleFactory factory, ITestOutputHelper output)
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
        public async Task 量測_難題題庫的三類指標與分離度()
        {
            using var scope = _app.Services.CreateScope();
            var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
            Assert.False(embedding.EmbeddingModelName.Length == 0,
                "未設定 Features__QaAssistant__EmbeddingModel，這個測試無法量任何東西。");

            var builder = scope.ServiceProvider.GetRequiredService<IDocIndexBuilder>();
            var (embedded, pending) = await builder.EmbedPendingAsync();
            _out.WriteLine($"模型 {embedding.EmbeddingModelName}；本次補嵌入 {embedded} 塊，仍待嵌入 {pending} 塊");
            Assert.Equal(0, pending);

            var relevant = new List<double>();

            // ── 近義干擾 ──────────────────────────────────────────────────
            var duel = ManualQuestionBank.Hard.Where(h => h.Kind == HardKind.Distractor).ToList();
            var won = 0;
            var lost = new List<string>();
            foreach (var q in duel)
            {
                var pages = await PagesAsync(q.Question, topK: 20);
                var right = RankOfPage(pages, q.Expected[0]);
                var wrong = RankOfPage(pages, q.Distractor!);
                if (Beats(right, wrong)) won++;
                else lost.Add($"  {q.Question,-34} 正解 {Fmt(right)} ／ 干擾 {Fmt(wrong)}  ({q.Distractor})");
                CollectScore(relevant, pages, q.Expected[0]);
            }

            _out.WriteLine("");
            _out.WriteLine($"【近義干擾】正解壓過干擾 {won}/{duel.Count} ({won * 100.0 / duel.Count:F0}%)");
            foreach (var line in lost) _out.WriteLine(line);

            // ── 需推理 ────────────────────────────────────────────────────
            var infer = ManualQuestionBank.Hard.Where(h => h.Kind == HardKind.Inference).ToList();
            var inferRanks = new List<int>();
            var inferMiss = new List<string>();
            foreach (var q in infer)
            {
                var pages = await PagesAsync(q.Question, topK: 20);
                var rank = RankOfPage(pages, q.Expected[0]);
                inferRanks.Add(rank);
                if (rank is 0 or > 3) inferMiss.Add($"  {q.Question,-34} {Fmt(rank)}  期望 {q.Expected[0]}");
                CollectScore(relevant, pages, q.Expected[0]);
            }

            var i1 = inferRanks.Count(r => r == 1);
            var i3 = inferRanks.Count(r => r is >= 1 and <= 3);
            _out.WriteLine("");
            _out.WriteLine($"【需推理】Recall@1={i1}/{infer.Count} @3={i3}/{infer.Count} " +
                           $"MRR={inferRanks.Where(r => r > 0).Sum(r => 1.0 / r) / infer.Count:F3}");
            foreach (var line in inferMiss) _out.WriteLine(line);

            // ── 跨頁 ──────────────────────────────────────────────────────
            var cross = ManualQuestionBank.Hard.Where(h => h.Kind == HardKind.CrossPage).ToList();
            var coverage = new List<double>();
            _out.WriteLine("");
            _out.WriteLine("【跨頁】Coverage@5（前 5 個不同頁面涵蓋幾份正解）");
            foreach (var q in cross)
            {
                var pages = await PagesAsync(q.Question, topK: 20);
                var top5 = pages.Take(5).ToHashSet();
                var got = q.Expected.Count(e => top5.Contains("/docs/user/" + e));
                coverage.Add((double)got / q.Expected.Length);
                _out.WriteLine($"  {q.Question,-34} {got}/{q.Expected.Length}");
                foreach (var e in q.Expected) CollectScore(relevant, pages, e);
            }

            _out.WriteLine($"  平均 Coverage@5 = {coverage.Average():P0}");

            // ── 分離度（§10.8 門檻、定案 #29 的遺留問題）───────────────────
            var irrelevant = new List<double>();
            var detail = new List<string>();
            foreach (var q in ManualQuestionBank.OutOfScope)
            {
                var hits = await SearchAsync(q, topK: 10);
                var best = hits.Where(h => h.SemanticScore is not null).ToList();
                if (best.Count == 0) continue;
                var top = best.MaxBy(h => h.SemanticScore!.Value);
                irrelevant.Add(top.SemanticScore!.Value);
                detail.Add($"  {q,-16} {top.SemanticScore:F4}  ← {top.HeadingPath}");
            }

            _out.WriteLine("");
            _out.WriteLine($"【分離度】正解語意分數        {Describe(relevant)}");
            _out.WriteLine($"          離題提問最高分      {Describe(irrelevant)}");
            foreach (var line in detail) _out.WriteLine(line);

            if (relevant.Count > 0 && irrelevant.Count > 0)
            {
                var gap = relevant.Min() - irrelevant.Max();
                _out.WriteLine($"          正解最低 {relevant.Min():F4} − 離題最高 {irrelevant.Max():F4} = {gap:F4} → " +
                               (gap > 0 ? "兩群分離，門檻可訂在中間" : "仍重疊，訂不出安全門檻"));

                // ── 尺度無關的重疊量（2026-08-06 補）─────────────────────────
                // **絕對餘弦差不能跨模型比。** 實測四顆的分數落點差很多
                // （granite 全部落在 0.60–0.86、embeddinggemma 落在 0.27–0.74），
                // 同樣是「差 0.1」在兩把尺上的意義完全不同。
                // 改量「有幾題離題提問的分數高過正解的下界」——**這是名次性質的，沒有單位**。
                var above = irrelevant.Count(x => x >= relevant.Min());
                _out.WriteLine($"          離題分數高過正解下界者 {above}/{irrelevant.Count} " +
                               $"（越少越好，這一項才可跨模型比）");

                // 正解沒進榜就完全不會進 relevant，於是**模型越差、樣本越乾淨**——
                // 不印出來的話，一顆常常漏掉正解的模型會拿到虛高的下界。
                var expectedSamples = duel.Count + infer.Count + cross.Sum(c => c.Expected.Length);
                _out.WriteLine($"          正解樣本 {relevant.Count}/{expectedSamples}" +
                               (relevant.Count < expectedSamples
                                   ? $"——**有 {expectedSamples - relevant.Count} 題正解未進前 20 名，"
                                     + "其分數未納入下界計算，故上面的分離度對本模型偏樂觀**"
                                   : "（全數進榜，下界未被倖存者偏差灌水）"));

                _out.WriteLine("");
                _out.WriteLine($"★ 換模型比這四項——重疊 {above}/{irrelevant.Count}（主）／" +
                               $"推理@1 {i1}/{infer.Count} @3 {i3}/{infer.Count}／" +
                               $"干擾勝出 {won}/{duel.Count}／跨頁 {coverage.Average():P0}；" +
                               $"絕對分離度 {gap:F4}（**同模型縱比可用，跨模型不可比**）");
            }

            // 唯一的斷言：量得到東西。**不設效能門檻**——首測值當目標值正是 §10.9 說過不要做的事。
            Assert.NotEmpty(relevant);
            Assert.NotEmpty(irrelevant);
        }

        /// <summary>名次比較。0 代表沒進榜；沒進榜的一方永遠輸給有進榜的一方。</summary>
        private static bool Beats(int right, int wrong) =>
            right > 0 && (wrong == 0 || right < wrong);

        private static string Fmt(int rank) => rank == 0 ? "未進榜" : $"第 {rank} 名";

        /// <summary>
        /// 取前 <paramref name="topK"/> 名並**去重成不同頁面**。
        ///
        /// <para>去重是必要的：同一頁的多個塊會各自佔一個名次，
        /// 「前 5 名涵蓋幾份正解」若不去重，一頁吃掉 5 格就會把跨頁題的涵蓋率壓成 1/3——
        /// 那量到的是切塊粒度，不是檢索品質。</para>
        /// </summary>
        private async Task<IReadOnlyList<string>> PagesAsync(string question, int topK)
        {
            var hits = await SearchAsync(question, topK);

            // 每題重置。不清的話，上一題留下的分數會被下一題的 CollectScore 取走——
            // 那是把「別題的正解分數」記成「本題的正解分數」，分離度的下界會被灌水。
            _lastScores.Clear();

            var seen = new List<string>();
            foreach (var h in hits)
            {
                if (!seen.Contains(h.DocUrl)) seen.Add(h.DocUrl);
                if (h.SemanticScore is { } s && !_lastScores.ContainsKey(h.DocUrl))
                    _lastScores[h.DocUrl] = s;
            }
            return seen;
        }

        /// <summary>最近一次 <see cref="PagesAsync"/> 各頁的最佳語意分數（每頁取最前面那一塊）。</summary>
        private readonly Dictionary<string, double> _lastScores = new();

        private void CollectScore(List<double> into, IReadOnlyList<string> pages, string expected)
        {
            if (_lastScores.TryGetValue("/docs/user/" + expected, out var s)) into.Add(s);
        }

        private static int RankOfPage(IReadOnlyList<string> pages, string file)
        {
            var idx = pages.ToList().IndexOf("/docs/user/" + file);
            return idx < 0 ? 0 : idx + 1;
        }

        private static string Describe(List<double> xs)
        {
            if (xs.Count == 0) return "（無）";
            var s = xs.OrderBy(x => x).ToList();
            return $"n={s.Count} min={s[0]:F4} 中位={s[s.Count / 2]:F4} max={s[^1]:F4}";
        }

        private async Task<IReadOnlyList<DocSearchHit>> SearchAsync(string question, int topK)
        {
            using var scope = _app.Services.CreateScope();
            var service = new ManualSearchService(
                scope.ServiceProvider.GetRequiredService<QaDbContext>(),
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                new HardBenchUserContext(),
                scope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
                scope.ServiceProvider.GetRequiredService<IDocVectorStore>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ManualSearchService>>());
            return await service.SearchAsync(question, topK);
        }

        /// <summary>
        /// 授出**索引裡出現過的每一個 MenuKey**，不是只授正解那幾頁。
        ///
        /// <para>只授正解的話，干擾頁會被權限過濾掉，「正解壓過干擾」就永遠 100%——
        /// 一個由測試設定造出來的滿分。量檢索品質時，權限必須完全不是變因。</para>
        /// </summary>
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

        private sealed class HardBenchUserContext : IUserContext
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
