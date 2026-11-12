using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Modules.QA.Data;

namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 語意檢索的一筆命中。**只有名次與分數**，欄位由呼叫端依 <see cref="ChunkId"/> 補齊——
/// 融合階段要的是名次（RRF 用 1/(k+rank)），把整列文字帶進來只是浪費。
/// </summary>
public readonly record struct VectorHit(int Rank, int ChunkId, double Score);

/// <summary>
/// 語意通道（規格 §10.6、§10.7）。
///
/// <para><b>權限與語系不由本層決定</b>——由呼叫端把已解析好的集合傳進來。
/// 這是刻意的：§10.10 要求每則提問**只查一次**權限，若本層自己再查一次，
/// 兩個通道就會各有一份權限判定，而它們遲早會不一致。</para>
/// </summary>
public interface IDocVectorStore
{
    /// <summary>
    /// 以餘弦相似度取前 <paramref name="topK"/> 名。
    /// </summary>
    /// <exception cref="DocIndexOutOfDateException">
    /// 索引中的向量與現行嵌入模型／維度不一致時拋出。**不會退化成「用得到的算一算」**（§10.7）。
    /// </exception>
    /// <param name="crossLangExtract">
    /// 擷取物分支是否略過語系比對（§17.9 缺口 G4）。**由呼叫端傳入，本層不讀組態**——
    /// 理由同權限與語系：兩個通道各讀一次組態，遲早會在同一則提問裡用不同的條件檢索。
    /// </param>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryVector, string lang, string[] allowedKeys, string[] allowedModules,
        bool crossLangExtract, int topK, CancellationToken ct = default);
}

/// <summary>
/// 精確餘弦，全部在記憶體算完。
///
/// <para><b>沒有 ANN 索引，也沒有跨請求的向量快取</b>，兩者都是刻意的：</para>
/// <list type="bullet">
///   <item>語料 563 塊 × 768 維約 1.7 MB（§10.6 實測），逐筆點積是微秒等級——
///     ANN 索引買不到效能，只會多一份要維護、要調參、且會回錯答案的東西。</item>
///   <item>快取要處理失效（索引重建、換模型），而它省下的是幾十毫秒，
///     卻換來「手冊改了但答案還是舊的」這種查不出來的錯。
///     一則提問本來就要等數秒的推論，這裡不值得用正確性換速度。</item>
/// </list>
/// <para>兩個決定都會隨語料規模改變而失效——但語料規模由手冊頁數決定，
/// 且業務資料絕不進入向量庫（§8.7），所以它不會自己長大。</para>
/// </summary>
public class DocVectorStore : IDocVectorStore
{
    private readonly QaDbContext _qa;
    private readonly IEmbeddingClient _embedding;

    public DocVectorStore(QaDbContext qa, IEmbeddingClient embedding)
    {
        _qa = qa;
        _embedding = embedding;
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        float[] queryVector, string lang, string[] allowedKeys, string[] allowedModules,
        bool crossLangExtract, int topK, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        if (topK <= 0 || queryVector.Length == 0) return Array.Empty<VectorHit>();

        // 過濾條件與詞彙通道共用同一份 SQL 片段（DocChunkFilter），不是各寫一份。
        var sql = $"""
            SELECT c."Id", c."Embedding", c."EmbeddingModel", c."EmbeddingDim"
            FROM "DocChunks" AS c
            WHERE {DocChunkFilter.LangAndPermission(crossLangExtract)}
              AND c."Embedding" IS NOT NULL
            """;

        var rows = await _qa.Set<DocChunkVector>()
            .FromSqlRaw(sql, lang, allowedKeys, allowedModules)
            .ToListAsync(ct);

        return Rank(rows, queryVector, _embedding.EmbeddingModelName, topK);
    }

    /// <summary>
    /// 純函式的排序核心：候選集 → 名次。**與資料庫無關，因此測得起來**——
    /// 上面那段 <c>FromSqlRaw</c> 只能靠整合測試，一致性檢查與餘弦排序卻是最需要細測的部分。
    /// </summary>
    public static IReadOnlyList<VectorHit> Rank(
        IReadOnlyList<DocChunkVector> rows, float[] queryVector, string configuredModel, int topK)
    {
        if (rows.Count == 0 || topK <= 0 || queryVector.Length == 0) return Array.Empty<VectorHit>();

        Verify(rows, configuredModel, queryVector.Length);

        var queryNorm = Norm(queryVector);
        if (queryNorm == 0) return Array.Empty<VectorHit>();

        return rows
            .Select(r => new { r.Id, Score = Cosine(queryVector, queryNorm, r.Embedding!) })

            // Id 作為第二排序鍵：同分時的順序必須穩定，
            // 否則同一個問題兩次問會拿到不同引文（與詞彙通道同樣的理由）。
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id)
            .Take(topK)
            .Select((x, i) => new VectorHit(i + 1, x.Id, x.Score))
            .ToList();
    }

    /// <summary>
    /// 模型／維度一致性檢查。**不一致就拒絕檢索，不做「盡量用」**（§10.7）——
    /// 不同模型的向量空間不可比，混用不會拋錯也不會回 0 筆，
    /// 只會照樣回得出 Top-K 而且全錯。這種失敗沒有任何外顯訊號，所以必須在這裡擋。
    /// </summary>
    private static void Verify(IReadOnlyList<DocChunkVector> rows, string configured, int queryDim)
    {
        var wrongModel = rows.FirstOrDefault(r =>
            !string.Equals(r.EmbeddingModel, configured, StringComparison.OrdinalIgnoreCase));

        if (wrongModel is not null)
            throw new DocIndexOutOfDateException(
                $"手冊索引是用 {Describe(wrongModel.EmbeddingModel)} 產生的，現行組態是 {Describe(configured)}。" +
                "不同模型的向量空間不可比，硬算出來的相似度是雜訊而不是錯誤，故拒絕檢索。請重建手冊索引。");

        var wrongDim = rows.FirstOrDefault(r => r.EmbeddingDim != queryDim || r.Embedding!.Length != queryDim);

        if (wrongDim is not null)
            throw new DocIndexOutOfDateException(
                $"手冊索引的向量是 {wrongDim.EmbeddingDim} 維，本次查詢是 {queryDim} 維。" +
                "同一個模型名稱也可能換維度（量化或 Matryoshka 截斷），故拒絕檢索。請重建手冊索引。");
    }

    private static string Describe(string model) => model.Length == 0 ? "（未記錄模型）" : model;

    private static double Norm(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        return Math.Sqrt(sum);
    }

    /// <summary>
    /// 餘弦相似度。**不假設向量已正規化**：模型是否輸出單位向量是實作細節，
    /// 假設錯了不會報錯，只會讓長度不同的塊之間的分數失真。
    /// </summary>
    private static double Cosine(float[] query, double queryNorm, float[] doc)
    {
        double dot = 0, docSum = 0;
        for (var i = 0; i < query.Length; i++)
        {
            dot += (double)query[i] * doc[i];
            docSum += (double)doc[i] * doc[i];
        }

        var docNorm = Math.Sqrt(docSum);
        return docNorm == 0 ? 0 : dot / (queryNorm * docNorm);
    }
}
