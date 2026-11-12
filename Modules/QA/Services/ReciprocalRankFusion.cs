namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 融合多個檢索通道的名次（規格 §10.5、§10.7）。
///
/// <para><b>為什麼是名次而不是分數</b>：詞彙通道的 <c>ts_rank</c> 與語意通道的餘弦
/// 是兩種完全不同的尺度——前者實測落在 0.01–0.09，後者是 −1 到 1，
/// 而且各自的分佈形狀也不同。硬要正規化就得引入一組沒有依據的魔術參數，
/// 而那些參數會隨語料改變默默失準。RRF 只用名次，**沒有任何要調的東西**。</para>
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>
    /// RRF 的平滑常數。<c>1/(k + rank)</c> 的 k 控制「名次差距換算成分數差距」的陡峭程度：
    /// k 太小則第 1 名壓倒一切（等於只用單一通道的冠軍），k 太大則所有名次趨於等權（等於沒融合）。
    ///
    /// <para><b>60 是 RRF 原始論文的值，本專案尚未自行調整。</b>
    /// 記在這裡是為了讓「這個數字是借來的」這件事不會被誤讀成量測結果——
    /// 要動它必須先有塊層級的評測數據（§10.9 S2-c）。</para>
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    /// 依名次融合。輸入是各通道**已排好序**的 ChunkId（第 0 個是第 1 名）。
    /// </summary>
    /// <returns>融合後由高到低的 (ChunkId, 分數)，同分時以 ChunkId 遞增穩定排序。</returns>
    public static IReadOnlyList<(int ChunkId, double Score)> Fuse(
        int topK, int k = DefaultK, params IReadOnlyList<int>[] channels)
    {
        if (topK <= 0 || channels.Length == 0) return Array.Empty<(int, double)>();

        var scores = new Dictionary<int, double>();

        foreach (var channel in channels)
        {
            for (var i = 0; i < channel.Count; i++)
            {
                var id = channel[i];
                var contribution = 1.0 / (k + i + 1);   // i 自 0 起，名次自 1 起
                scores[id] = scores.TryGetValue(id, out var existing) ? existing + contribution : contribution;
            }
        }

        return scores
            // 同分時以 ChunkId 遞增：順序必須穩定，否則同一個問題兩次問會拿到不同引文。
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
