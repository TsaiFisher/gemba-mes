using System.Reflection;
using SmartFactory.MES.Modules.QA.Models;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// 把 <see cref="DocChunk"/> 的持久化欄位清單**釘死**。
    ///
    /// <para><b>擋的是一個沒有錯誤訊息的失敗</b>：<c>ExtractManifestImporter.SameAsIndexed</c>
    /// 逐欄比對「這一份 manifest 建出來的列與庫裡的列是否相同」，相同就跳過重寫，
    /// 讓該份的向量留著（重嵌 1288 段在開發機 CPU 上約 30 分鐘）。
    /// <b>新增一個欄位卻忘了加進那個比對</b>，症狀是「改了 manifest 卻沒生效」——
    /// 匯入照樣回報成功、查詢照樣回得出東西，只是內容停在舊版。</para>
    ///
    /// <para>這支測試不驗比對本身對不對（那是 <c>ExtractManifestImporterTests</c> 的事），
    /// 它只保證**欄位一動就有人被叫回來做決定**。紅了之後的正確處理是二選一：
    /// 把新欄位加進 <c>SameAsIndexed</c>，或把它加進下面的「不參與比對」那一組並寫清楚為什麼。</para>
    /// </summary>
    public class DocChunkPersistedFieldsFacts
    {
        /// <summary>參與比對的欄位＝manifest 的內容。任何一個變了就代表這一份要重寫。</summary>
        private static readonly string[] Compared =
        {
            nameof(DocChunk.Corpus),
            nameof(DocChunk.FilePath),
            nameof(DocChunk.Lang),
            nameof(DocChunk.ChunkIndex),
            nameof(DocChunk.ContentHash),
            nameof(DocChunk.HeadingPath),
            nameof(DocChunk.Module),
            nameof(DocChunk.MenuKey),
            nameof(DocChunk.DocUrl),
            nameof(DocChunk.Text),
            nameof(DocChunk.Lexemes),
            nameof(DocChunk.HeadingLexemes),
            nameof(DocChunk.SourcePath),
            nameof(DocChunk.SourceHash),
            nameof(DocChunk.Approved),
            nameof(DocChunk.IsStale),
            nameof(DocChunk.ImagePaths),
        };

        /// <summary>不參與比對的欄位，每一個都要有理由——沒有理由的話它就該在上面那一組。</summary>
        private static readonly Dictionary<string, string> NotCompared = new()
        {
            [nameof(DocChunk.Id)] = "資料庫給的，不是 manifest 的內容。",
            [nameof(DocChunk.IndexedAt)] = "每一次匯入都不一樣，拿它比會永遠判定「變了」。",
            [nameof(DocChunk.Embedding)] = "這正是比對要保住的東西，不是要比的東西。",
            [nameof(DocChunk.EmbeddingModel)] = "同上；換模型的重嵌走 DocIndexBuilder.PendingQuery。",
            [nameof(DocChunk.EmbeddingDim)] = "同上。",
        };

        [Fact]
        public void DocChunk_的每一個欄位都要表態要不要參與匯入比對()
        {
            var actual = typeof(DocChunk)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            var declared = Compared.Concat(NotCompared.Keys).ToHashSet(StringComparer.Ordinal);

            var missing = actual.Except(declared).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var stale = declared.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.True(missing.Count == 0,
                "DocChunk 新增了欄位但沒有表態要不要進 ExtractManifestImporter.SameAsIndexed：" +
                string.Join("、", missing) +
                "。要嘛加進比對，要嘛加進 NotCompared 並寫清楚為什麼——" +
                "漏掉的症狀是「改了 manifest 卻沒生效」，而且不會有錯誤訊息。");

            Assert.True(stale.Count == 0,
                "下列欄位已經不在 DocChunk 上了，清單沒跟著改：" + string.Join("、", stale));
        }

        [Fact]
        public void 不參與比對的欄位每一個都要寫理由()
        {
            var blank = NotCompared.Where(kv => kv.Value.Trim().Length == 0).Select(kv => kv.Key).ToList();
            Assert.True(blank.Count == 0, "以下欄位被排除在比對之外卻沒寫理由：" + string.Join("、", blank));
        }
    }
}
