using SmartFactory.MES.Modules.QA.Contracts;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="DocCorpus"/> 的字面約束（規格 §10.11.7）。
    ///
    /// <para>這兩個值會被**字面**拼進 <c>DocChunkFilter.LangAndPermission</c> 的 SQL，
    /// 同時也是 <c>DocChunk.Corpus</c>（<c>varchar(20)</c>）的欄位值。
    /// 它們全是程式碼裡寫死的識別字、不含使用者輸入，所以這裡不是在防注入，
    /// 而是要讓「有人哪天塞進帶引號的字串或超長的值」變成紅燈，
    /// 而不是變成一段壞掉的 SQL 或一次資料庫端的截斷。</para>
    ///
    /// <para>截斷的失敗模式特別值得擋：SQL 比對的是完整字串，被截短的值永遠對不上任何分支，
    /// 結果是那一整種語料索引得進去、卻對每一個使用者都檢索不到，而且沒有任何訊號——
    /// 與 <c>ManualCorpusPolicy.PublicModules</c> 當初的問題同一個形狀。</para>
    /// </summary>
    public class DocCorpusFacts
    {
        [Fact]
        public void 語料別是小寫英文字且不重複()
        {
            var all = DocCorpus.All;

            Assert.NotEmpty(all);
            Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());

            foreach (var c in all)
            {
                Assert.Matches("^[a-z]+$", c);
                Assert.True(c.Length <= 20, $"「{c}」共 {c.Length} 字元，超過 DocChunk.Corpus 的 varchar(20)。");
            }
        }

        /// <summary>
        /// <c>All</c> 必須真的列全。少列一個的後果是這份守門對它完全沒有作用——
        /// 一個沒被檢查過的值照樣會被拼進 SQL。
        /// </summary>
        [Fact]
        public void All涵蓋每一個已宣告的語料別()
        {
            Assert.Contains(DocCorpus.Manual, DocCorpus.All);
            Assert.Contains(DocCorpus.Extract, DocCorpus.All);

            var declared = typeof(DocCorpus)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();

            Assert.Equal(
                declared.OrderBy(x => x, StringComparer.Ordinal),
                DocCorpus.All.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// 手冊的值必須是 <c>manual</c>——它同時是 <c>DocChunk.Corpus</c> 的 C# 屬性預設值、
        /// 資料庫欄位預設值，以及 <c>AddDocChunkCorpus</c> migration 寫進既有列的值。
        /// 這三處若與常數不一致，既有的 652 列手冊會落在一個沒有任何過濾分支接得住的語料別上，
        /// 整份手冊從 Q&amp;A 消失而不留下任何錯誤。
        /// </summary>
        [Fact]
        public void 手冊語料別的字面是既有列的回填值()
        {
            Assert.Equal("manual", DocCorpus.Manual);
            Assert.Equal("manual", new Modules.QA.Models.DocChunk().Corpus);
        }

        /// <summary>
        /// 新建的塊預設**未核可、未過期**。方向很重要：擷取器只負責產草稿，
        /// 核可是人的動作（§10.11.4 定案 #43）。預設 <c>true</c> 等於把唯一防線拿掉，
        /// 而且拿掉之後不會有任何徵兆。
        /// </summary>
        [Fact]
        public void 新塊預設未核可()
        {
            var chunk = new Modules.QA.Models.DocChunk();

            Assert.False(chunk.Approved);
            Assert.False(chunk.IsStale);
        }
    }
}
