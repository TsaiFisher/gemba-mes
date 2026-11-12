namespace SmartFactory.MES.Modules.QA.Data;

/// <summary>
/// 檢索查詢的無鍵投影（keyless entity）。
///
/// <para>存在的理由只有一個：<c>ts_rank</c> 的分數不是 <see cref="Models.DocChunk"/> 的欄位，
/// 而 <c>FromSql</c> 對映實體時會**忽略結果集中多出來的欄位**——分數就這樣無聲無息地掉了。</para>
///
/// <para>分數的用途是 §10.8 的相關度門檻（「查無相關說明」與「有結果但不相關」是兩件事）。
/// 它**只在同一個檢索通道內可比**：S2 加入語意通道後，兩邊的分數尺度不同，
/// 融合一律走名次（RRF），不是分數。</para>
///
/// <para>順帶的好處是檢索走投影而非追蹤實體：檢索是純讀取，
/// 讓變更追蹤器抓著幾百列手冊內容沒有任何意義。</para>
/// </summary>
public class DocChunkHit
{
    public int Id { get; set; }
    public string Lang { get; set; } = string.Empty;
    public string HeadingPath { get; set; } = string.Empty;
    public string DocUrl { get; set; } = string.Empty;
    public string MenuKey { get; set; } = string.Empty;

    /// <summary>
    /// 語料別（<c>manual</c>／<c>extract</c>）。**排序後分艙用**（§17.9 缺口 G5）。
    ///
    /// <para><b>刻意讀這一欄，不用 <c>DocUrl</c> 前綴去猜。</b>
    /// 「<c>/docs/user/rules/</c> 開頭的就是擷取物」在今天成立，
    /// 但那是 <c>ExtractManifestImporter</c> 的一個命名慣例，不是約束——
    /// 哪天手冊多了一頁 <c>rules/</c>，分艙會安靜地把它算成擷取物。</para>
    /// </summary>
    public string Corpus { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public List<string> ImagePaths { get; set; } = new();

    /// <summary><c>ts_rank</c> 的原始分數。通道內可比，跨通道不可比。</summary>
    public double Score { get; set; }
}
