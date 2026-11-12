using SmartFactory.MES.Modules.QA.Services;
using Xunit;

namespace SmartFactory.MES.Tests.QA
{
    /// <summary>
    /// RRF 融合（規格 §10.5）。這裡守的是**融合真的有互補作用**，
    /// 而不只是「把兩份清單接起來」——後者看起來也會通過大部分的測試。
    /// </summary>
    public class ReciprocalRankFusionTests
    {
        private static IReadOnlyList<int> L(params int[] ids) => ids;

        [Fact]
        public void 兩個通道都排前面的塊要勝過只有單一通道的冠軍()
        {
            // 這是混合檢索存在的全部理由：兩邊都覺得相關的東西，比任一邊的第一名更可信。
            var lexical = L(1, 2, 3);
            var semantic = L(9, 2, 3);

            var fused = ReciprocalRankFusion.Fuse(3, ReciprocalRankFusion.DefaultK, lexical, semantic);

            Assert.Equal(2, fused[0].ChunkId);   // 兩邊都是第 2 名
            Assert.Equal(3, fused[1].ChunkId);   // 兩邊都是第 3 名
        }

        [Fact]
        public void 單一通道時等同該通道的名次()
        {
            var fused = ReciprocalRankFusion.Fuse(3, ReciprocalRankFusion.DefaultK, L(5, 7, 9));

            Assert.Equal(new[] { 5, 7, 9 }, fused.Select(f => f.ChunkId));
        }

        [Fact]
        public void 某個通道為空不影響另一個通道()
        {
            // 語意通道查無相似內容（不是不可用）時會走到這裡。
            var fused = ReciprocalRankFusion.Fuse(
                2, ReciprocalRankFusion.DefaultK, L(4, 6), Array.Empty<int>());

            Assert.Equal(new[] { 4, 6 }, fused.Select(f => f.ChunkId));
        }

        [Fact]
        public void 同分時以_ChunkId_穩定排序()
        {
            // 同一個問題問兩次拿到不同引文，使用者只會覺得系統不可靠。
            var fused = ReciprocalRankFusion.Fuse(
                2, ReciprocalRankFusion.DefaultK, L(8), L(3));

            Assert.Equal(new[] { 3, 8 }, fused.Select(f => f.ChunkId));
        }

        [Fact]
        public void 分數必須遞減()
        {
            var fused = ReciprocalRankFusion.Fuse(
                4, ReciprocalRankFusion.DefaultK, L(1, 2, 3, 4), L(4, 3, 2, 1));

            for (var i = 1; i < fused.Count; i++)
                Assert.True(fused[i - 1].Score >= fused[i].Score);
        }

        [Fact]
        public void 只回傳前_K_名()
        {
            var fused = ReciprocalRankFusion.Fuse(
                2, ReciprocalRankFusion.DefaultK, L(1, 2, 3, 4, 5));

            Assert.Equal(2, fused.Count);
        }

        [Fact]
        public void k_越大名次差距越不重要()
        {
            // 記錄 k 的作用方向，讓「把 60 改掉」這件事有依據可循（§10.9 S2-c 之前不該動它）。
            var small = ReciprocalRankFusion.Fuse(2, k: 1, L(1, 2));
            var large = ReciprocalRankFusion.Fuse(2, k: 1000, L(1, 2));

            var smallGap = small[0].Score - small[1].Score;
            var largeGap = large[0].Score - large[1].Score;

            Assert.True(smallGap > largeGap);
        }

        [Fact]
        public void 沒有通道或_topK_為零時回空()
        {
            Assert.Empty(ReciprocalRankFusion.Fuse(5, ReciprocalRankFusion.DefaultK));
            Assert.Empty(ReciprocalRankFusion.Fuse(0, ReciprocalRankFusion.DefaultK, L(1, 2)));
        }
    }
}
