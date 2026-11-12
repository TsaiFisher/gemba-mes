using SmartFactory.MES.Modules.QA.Data;
using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// <see cref="DocVectorStore.Rank"/> 的守門。
    ///
    /// <para>這裡守的重點不是「餘弦算得對不對」（那是課本公式），
    /// 而是**兩種會安靜回錯答案的情形**：換了嵌入模型沒重建索引、向量維度變了。
    /// 兩者都不會拋錯、不會回 0 筆，只會照樣給出排序合理的 Top-K 而內容全錯（§10.7）。</para>
    /// </summary>
    public class DocVectorStoreTests
    {
        private const string Model = "embeddinggemma";

        private static DocChunkVector Row(int id, float[] v, string? model = null) => new()
        {
            Id = id,
            Embedding = v,
            EmbeddingModel = model ?? Model,
            EmbeddingDim = v.Length,
        };

        [Fact]
        public void 依餘弦相似度由高到低排名()
        {
            var rows = new[]
            {
                Row(1, new[] { 0f, 1f, 0f }),      // 與查詢正交
                Row(2, new[] { 1f, 0f, 0f }),      // 與查詢同向
                Row(3, new[] { 1f, 1f, 0f }),      // 夾角 45°
            };

            var hits = DocVectorStore.Rank(rows, new[] { 1f, 0f, 0f }, Model, topK: 3);

            Assert.Equal(new[] { 2, 3, 1 }, hits.Select(h => h.ChunkId));
            Assert.Equal(new[] { 1, 2, 3 }, hits.Select(h => h.Rank));
            Assert.Equal(1.0, hits[0].Score, precision: 6);
            Assert.Equal(0.0, hits[2].Score, precision: 6);
        }

        [Fact]
        public void 向量長度不影響相似度()
        {
            // 不假設模型輸出單位向量。假設錯了不會報錯，只會讓長短不同的塊之間的分數失真——
            // 而「長的塊分數莫名偏高」正是沒人查得出來的那種偏差。
            var rows = new[] { Row(1, new[] { 5f, 0f, 0f }), Row(2, new[] { 0.01f, 0f, 0f }) };

            var hits = DocVectorStore.Rank(rows, new[] { 1f, 0f, 0f }, Model, topK: 2);

            Assert.Equal(1.0, hits[0].Score, precision: 6);
            Assert.Equal(1.0, hits[1].Score, precision: 6);
        }

        [Fact]
        public void 同分時以_Id_穩定排序()
        {
            // 同一個問題問兩次拿到不同引文，使用者只會覺得系統不可靠。
            var rows = new[] { Row(7, new[] { 1f, 0f }), Row(3, new[] { 2f, 0f }), Row(5, new[] { 9f, 0f }) };

            var hits = DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 3);

            Assert.Equal(new[] { 3, 5, 7 }, hits.Select(h => h.ChunkId));
        }

        [Fact]
        public void 只回傳前_K_名()
        {
            var rows = Enumerable.Range(1, 10).Select(i => Row(i, new[] { i, 1f })).ToArray();

            Assert.Equal(3, DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 3).Count);
        }

        [Fact]
        public void 索引用的模型與組態不符必須拒絕檢索()
        {
            // 「盡量用」在這裡是最糟的選擇：向量空間不可比，硬算的分數是雜訊而不是錯誤，
            // 檢索照樣回得出 Top-K，只是全錯，而且沒有任何外顯訊號。
            var rows = new[] { Row(1, new[] { 1f, 0f }, model: "multilingual-e5-large") };

            var ex = Assert.Throws<DocIndexOutOfDateException>(
                () => DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 5));

            Assert.Contains("multilingual-e5-large", ex.Message);
            Assert.Contains(Model, ex.Message);
        }

        [Fact]
        public void 混雜舊模型的列也要拒絕而不是略過()
        {
            // 只把不符的列濾掉＝安靜地用剩下的算——那正是「盡量用」，規格明文禁止。
            var rows = new[]
            {
                Row(1, new[] { 1f, 0f }),
                Row(2, new[] { 0f, 1f }, model: "granite-embedding"),
            };

            Assert.Throws<DocIndexOutOfDateException>(
                () => DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 5));
        }

        [Fact]
        public void 維度不符必須拒絕檢索()
        {
            // 同一個模型名稱也可能換維度（量化、Matryoshka 截斷）。
            var rows = new[] { Row(1, new[] { 1f, 0f, 0f }) };

            var ex = Assert.Throws<DocIndexOutOfDateException>(
                () => DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 5));

            Assert.Contains("3 維", ex.Message);
            Assert.Contains("2 維", ex.Message);
        }

        [Fact]
        public void 沒有候選時回空而不是拋錯()
        {
            // 使用者沒有任何畫面權限、或索引尚未嵌入時會走到這裡。這不是錯誤。
            Assert.Empty(DocVectorStore.Rank(Array.Empty<DocChunkVector>(), new[] { 1f, 0f }, Model, topK: 5));
        }

        [Fact]
        public void 零向量的候選不得產生_NaN()
        {
            // 0/0 會讓排序結果不確定，且完全看不出成因。
            var rows = new[] { Row(1, new[] { 0f, 0f }), Row(2, new[] { 1f, 0f }) };

            var hits = DocVectorStore.Rank(rows, new[] { 1f, 0f }, Model, topK: 2);

            Assert.All(hits, h => Assert.False(double.IsNaN(h.Score)));
            Assert.Equal(2, hits[0].ChunkId);
        }
    }
}
