namespace SmartFactory.MES.Modules.QA.Data;

/// <summary>
/// 語意檢索查詢的無鍵投影（keyless entity），理由同 <see cref="DocChunkHit"/>。
///
/// <para>只取算餘弦需要的四個欄位。**不要順手把 Text 也撈進來**：
/// 語意通道要回傳的是名次（融合走 RRF），欄位由呼叫端依 Id 補齊，
/// 而候選集是「使用者有權讀的全部塊」——把每一塊的全文都撈出來只為了丟掉，
/// 在 563 塊的規模看不出來，在手冊長大之後就是每則提問多搬幾 MB。</para>
/// </summary>
public class DocChunkVector
{
    public int Id { get; set; }

    /// <summary>PostgreSQL 的 <c>real[]</c>。查詢已濾掉 NULL，但型別保持可空以對齊實體定義。</summary>
    public float[]? Embedding { get; set; }

    public string EmbeddingModel { get; set; } = string.Empty;

    public int EmbeddingDim { get; set; }
}
