using System.Text.Json;
using AngleSharp.Html.Parser;
using SmartFactory.MES.Modules.QA.Contracts;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 手冊靜態搜尋索引的守門。
    ///
    /// <para><b>要擋的失敗：手冊改了、索引沒重建。</b>症狀是新寫的章節搜不到，
    /// 或更糟——搜到了但點進去的錨點已經不存在，使用者被丟在頁首。
    /// 兩者都不會有錯誤、不會有日誌，搜尋框看起來完全正常。
    /// 所以這裡逐位元比對：索引是產物，產物與來源不同步就是紅的。</para>
    ///
    /// <para><b>重建索引：</b></para>
    /// <code>DOCSEARCH_REGEN=1 dotnet test --filter DocSearchIndexFacts</code>
    /// </summary>
    public class DocSearchIndexFacts
    {
        private const string RegenEnvVar = "DOCSEARCH_REGEN";

        private readonly ITestOutputHelper _out;
        private readonly DocSearchIndexBuilder _builder = new(new ManualDocParser());

        public DocSearchIndexFacts(ITestOutputHelper output) => _out = output;

        [Fact]
        public void 重建索引()
        {
            if (Environment.GetEnvironmentVariable(RegenEnvVar) != "1")
            {
                _out.WriteLine($"未設 {RegenEnvVar}=1，略過重建（這是預設行為，不是失敗）。");
                return;
            }

            var written = _builder.Write(ManualCorpusFacts.ManualRoot(), out var result);

            _out.WriteLine($"收錄 {result.Pages} 頁；中文 {result.ZhChunks} 塊、英文 {result.EnChunks} 塊。");
            _out.WriteLine(written.Count == 0 ? "索引本來就是最新的，未寫檔。" : $"已寫入：{string.Join("、", written)}");
        }

        [Fact]
        public void 索引與手冊內容逐位元一致()
        {
            var root = ManualCorpusFacts.ManualRoot();
            var expected = _builder.Build(root, out var result);

            Assert.True(result.Pages >= ManualSearchScope.MinimumPages,
                $"只收錄 {result.Pages} 頁，少於下限 {ManualSearchScope.MinimumPages}——" +
                "手冊目錄結構或收錄範圍已變，守門形同關閉");

            foreach (var (lang, json) in expected)
            {
                var path = Path.Combine(root, DocSearchIndexBuilder.FileNameFor(lang));

                Assert.True(File.Exists(path),
                    $"缺少索引檔 {DocSearchIndexBuilder.FileNameFor(lang)}——" +
                    $"執行 `{RegenEnvVar}=1 dotnet test --filter DocSearchIndexFacts` 產生");

                Assert.True(File.ReadAllText(path) == json,
                    $"{DocSearchIndexBuilder.FileNameFor(lang)} 與目前的手冊內容不一致——" +
                    $"手冊改過但索引沒重建（搜尋會搜不到新內容，且不會有任何錯誤）。" +
                    $"執行 `{RegenEnvVar}=1 dotnet test --filter DocSearchIndexFacts` 重建。");
            }
        }

        [Fact]
        public void 每個錨點都真的存在於該頁()
        {
            var root = ManualCorpusFacts.ManualRoot();
            var parser = new HtmlParser();
            var anchorsByPage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var violations = new List<string>();
            var anchored = 0;
            var total = 0;

            foreach (var (lang, json) in _builder.Build(root, out _))
            {
                using var doc = JsonDocument.Parse(json);

                foreach (var entry in doc.RootElement.GetProperty("C").EnumerateArray())
                {
                    total++;
                    var page = entry.GetProperty("U").GetString()!;
                    var anchor = entry.GetProperty("A").GetString()!;

                    // 頁首塊（第一個 h2 之前）沒有錨點是正常的——它就該跳頁首。
                    if (anchor.Length == 0) continue;
                    anchored++;

                    if (!anchorsByPage.TryGetValue(page, out var ids))
                    {
                        var html = File.ReadAllText(Path.Combine(root, page.Replace('/', Path.DirectorySeparatorChar)));
                        ids = parser.ParseDocument(html)
                            .QuerySelectorAll("[id]")
                            .Select(e => e.GetAttribute("id")!)
                            .ToHashSet(StringComparer.Ordinal);
                        anchorsByPage[page] = ids;
                    }

                    if (!ids.Contains(anchor))
                        violations.Add($"{lang} {page}#{anchor} 在頁面上不存在——搜尋結果會靜默落在頁首");
                }
            }

            Assert.True(total > 1000, $"索引只有 {total} 塊，遠少於預期——切塊或收錄範圍已變，守門形同關閉");

            _out.WriteLine($"索引共 {total} 塊，其中 {anchored} 塊帶段落錨點（{anchored * 100.0 / total:F1}%）。");

            Assert.True(violations.Count == 0,
                $"有 {violations.Count} 個錨點指向不存在的位置：\n  " + string.Join("\n  ", violations.Take(20)));
        }
    }
}
