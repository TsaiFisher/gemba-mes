using System.ComponentModel.DataAnnotations;

namespace SmartFactory.MES.Modules.QA.Models;

/// <summary>
/// 使用手冊切塊後的一列（規格 §10.3）。**只存手冊內容，永遠不存業務資料**（§8.7 架構約束）。
///
/// <para><b>刻意不實作 <c>IPlantEntity</c> 與 <c>IDataAuthEntity</c></b>（與 CLAUDE.md 的一般規則相反）：
/// 手冊是全公司同一份，沒有廠別維度。若納入 global query filter，索引建置在背景服務中執行
/// （無 <c>HttpContext</c> → <c>PlantId</c> 為 null）會退化成 <c>PlantId == null</c>，
/// 結果是**索引寫得進去、查詢一筆都讀不到**，而且日誌完全乾淨。</para>
///
/// <para><b>權限不靠 global filter，靠 <see cref="MenuKey"/> 顯式過濾</b>（§10.10）：
/// 每則提問先取得使用者的可讀 MenuKey 集合，再於檢索時過濾。這是刻意的——
/// 隱式過濾器在這裡的失敗模式是「安靜地回傳空結果」，顯式過濾至少查得出原因。</para>
///
/// <para>本表位於獨立的 <c>QaDbContext</c>（<c>__EFMigrationsHistory_Qa</c>），
/// 只在 <c>Features:QaAssistant:Enabled</c> 時才建立與遷移（§17.3）。</para>
/// </summary>
public class DocChunk
{
    public int Id { get; set; }

    /// <summary>相對於 <c>wwwroot/docs/user</c> 的路徑，如 <c>ord/delivery-rate.html</c>。</summary>
    [MaxLength(200)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// <c>zh</c> 或 <c>en</c>。**檢索時是硬過濾條件，不是排序參考**（§10.4）：
    /// multilingual embedding 會讓「Sales Ranking」與「銷售排名」高度相似，
    /// 交給模型自己分辨的結果是英文使用者拿到中文引文。
    /// </summary>
    [MaxLength(2)]
    public string Lang { get; set; } = string.Empty;

    /// <summary>同一頁同一語系內的順序，自 0 起。</summary>
    public int ChunkIndex { get; set; }

    /// <summary>
    /// **整份 HTML**（雙語同檔）的 SHA256，驅動增量重嵌（§10.3）。
    /// 取整份而非單塊：中英文本來就一起改，一次變更同時重建該檔的兩組 chunk 才是對的。
    /// 不設人工 <c>DocVersion</c> 欄位——人工版號的宿命是沒人記得 bump。
    /// </summary>
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>麵包屑（「交期達成率 › 欄位說明」）。同時是引文標題，切詞後存進 <see cref="HeadingLexemes"/>。</summary>
    [MaxLength(400)]
    public string HeadingPath { get; set; } = string.Empty;

    /// <summary>手冊模組目錄（<c>ord</c>／<c>inv</c>…）。</summary>
    [MaxLength(10)]
    public string Module { get; set; } = string.Empty;

    /// <summary>
    /// 對應畫面的選單鍵（＝controller 名稱，如 <c>DeliveryRate</c>），供 L1 權限過濾。
    /// <b>空字串代表沒有對應畫面</b>（模組目錄頁、手冊首頁），這類頁面不受權限限制——
    /// 它們只有畫面清單與導覽，沒有任何欄位或數字。
    /// </summary>
    [MaxLength(100)]
    public string MenuKey { get; set; } = string.Empty;

    /// <summary>引文連結（<c>/docs/user/ord/delivery-rate.html</c>）。語系參數於顯示時再加。</summary>
    [MaxLength(300)]
    public string DocUrl { get; set; } = string.Empty;

    /// <summary>單一語系的正規化純文字。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 詞彙檢索用的切詞結果＝<c>CjkTokenizer.Tokenize(Text)</c>，**只有內文，不含標題**。
    ///
    /// 存起來而非查詢時即算，是因為 <c>to_tsvector</c> 必須吃到與查詢期**完全相同**的切法；
    /// 把切詞放在寫入端，索引期與查詢期就只有一份程式碼可以走錯（見 <c>CjkTokenizer</c> 的說明）。
    ///
    /// <para>⚠️ <b>2026-08-26 之前這一欄是「標題＋內文」平鋪</b>（待決 #48）。改成兩欄是為了
    /// 讓標題能在 <c>ts_rank</c> 拿到較高的權重；沿用舊語意的列會讓標題權重完全失效，
    /// 而症狀只是「排序沒有變好」——不會有錯誤。舊列的重建由
    /// <c>DocIndexBuilder.IndexFormatVersion</c> 摻進 <see cref="ContentHash"/> 強制觸發。</para>
    /// </summary>
    public string Lexemes { get; set; } = string.Empty;

    /// <summary>
    /// 標題（麵包屑）的切詞結果＝<c>CjkTokenizer.Tokenize(HeadingPath)</c>。
    ///
    /// <para>與 <see cref="Lexemes"/> 分成兩欄的唯一理由是**加權**：詞彙通道把這一欄
    /// <c>setweight(…, 'A')</c>、內文 <c>setweight(…, 'D')</c> 之後再 <c>ts_rank</c>。
    /// 專有名詞多半出現在標題而非內文，兩者等重時「畫面」「設定」「客戶」這種常用詞
    /// 一多，正解就被新頁面擠下去（§10.9 實測：語料 149 頁 Recall@1=16、154 頁 14）。</para>
    ///
    /// <para><b>可以是空字串</b>：沒有麵包屑的塊（手冊首頁的前言）本來就沒有標題可切，
    /// 那時退化成「只有內文參與排序」，與加權前的行為一致。</para>
    /// </summary>
    public string HeadingLexemes { get; set; } = string.Empty;

    /// <summary>
    /// 可直接放進 <c>&lt;img src&gt;</c> 的網站絕對路徑；英文塊已解析成 <c>img/en/…</c> 並做過存在性 fallback。
    /// Npgsql 原生對應 <c>text[]</c>，不需另建子表或 JSON 欄位。
    /// </summary>
    public List<string> ImagePaths { get; set; } = new();

    public DateTime IndexedAt { get; set; }

    /// <summary>
    /// 語意檢索用的向量（規格 §10.7）。**null 代表這一塊還沒嵌入**——
    /// S1 建立的列、以及嵌入模型當下不可用時建立的列都是 null，屬正常狀態而非損毀。
    ///
    /// <para>型別是 <c>real[]</c>（Npgsql 原生對應 <c>float[]</c>），<b>不是 pgvector 的 <c>vector</c></b>：
    /// 全表 563 列 × 768 維約 1.7 MB，全部載入記憶體做精確餘弦是微秒等級，
    /// ANN 索引買不到東西（§10.6）。要換 pgvector 時改 <c>IDocVectorStore</c> 的實作即可。</para>
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// 產生 <see cref="Embedding"/> 的模型名稱。**與現行組態不一致時必須拒絕檢索並要求重建**，
    /// 不做「盡量用」（§10.7）——不同模型的向量空間不可比，
    /// 混用不會出錯，只會讓相似度變成雜訊：照樣回得出 Top-K，只是全錯。
    ///
    /// <para>這個欄位不是理論需求。本規格已經因為來源限制撤換過一次嵌入模型選型，
    /// **選型真的會變。**</para>
    /// </summary>
    [MaxLength(100)]
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Embedding"/> 的維度。與模型名稱一起存，是因為同一個模型名稱也可能換維度
    /// （Matryoshka 截斷、量化版本），而維度不符的餘弦計算會直接算出錯的數字或例外。
    /// </summary>
    public int EmbeddingDim { get; set; }

    // ── 以下為擷取物語料（§10.11）新增。手冊列一律 Corpus=manual，其餘四欄維持預設值。 ──

    /// <summary>
    /// 這一列屬於哪一種語料，值見 <see cref="Contracts.DocCorpus"/>（§10.11.7 定案 #42）。
    ///
    /// <para><b>它同時決定權限規則</b>：<c>manual</c> 走 <see cref="MenuKey"/> 過濾、
    /// <c>extract</c> 開放查詢但要求 <see cref="Approved"/> 且未 <see cref="IsStale"/>。
    /// 兩套規則寫在 <c>DocChunkFilter</c> 的同一段 SQL 內。</para>
    ///
    /// <para>預設 <c>manual</c> 使既有列與既有寫入路徑不需改動即為正確值；
    /// migration 也因此不需要資料回填。</para>
    /// </summary>
    [MaxLength(20)]
    public string Corpus { get; set; } = Contracts.DocCorpus.Manual;

    /// <summary>
    /// 擷取來源的 repo 相對路徑（<c>.claude/specifications/currency.md</c>）。手冊列為空字串。
    ///
    /// <para><b>只作為溯源與重擷取的依據，不進索引也不出現在引文裡。</b>
    /// 擷取物的引文指向的是它自己的說明頁，把 <c>.md</c> 路徑露給使用者等於洩漏原文位置，
    /// 而原文正是 §10.11.1 判定不可公開的東西。</para>
    /// </summary>
    [MaxLength(300)]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 擷取當下**來源檔全文**的 SHA256（§10.11.3）。手冊列為空字串——
    /// 手冊的等價欄位是 <see cref="ContentHash"/>，兩者刻意分開：
    /// 手冊的 hash 算的是進索引的那份內容本身，擷取物的 hash 算的是**它衍生自的別份檔案**。
    /// 混用一個欄位會讓「內容變了」與「來源變了」這兩件不同的事無法區分。
    /// </summary>
    [MaxLength(64)]
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    /// 人工核可（§10.11.4）。**未核可就不進檢索**，且擷取器不得自行寫入 <c>true</c>。
    ///
    /// <para>這不是流程裝飾：擷取本身會幻覺——讓 LLM 讀 <c>BuildJournalLines</c> 寫出分錄規則，
    /// 寫錯了沒有任何機制會發現，而它會被當成權威答案引用。
    /// 搭配 §10.11.5 的開放查詢後，<b>這一關是唯一防線</b>。</para>
    ///
    /// <para>手冊列預設 <c>false</c> 而**不受影響**：權限 SQL 只在 <c>Corpus=extract</c>
    /// 的分支檢查本欄。這是刻意的——手冊原文本來就是寫給使用者看的，不需要逐段核可，
    /// 若讓它也吃這道閘門，一次 migration 就會讓整份手冊從 Q&amp;A 消失。</para>
    /// </summary>
    public bool Approved { get; set; }

    /// <summary>
    /// 來源已變動、這份擷取物尚未重生（§10.11.3）。**stale 直接排除於檢索之外，不做「先用著」。**
    ///
    /// <para>擷取物最大的風險是它會過期而且不會出聲：程式碼改了、說明沒重生，
    /// Q&amp;A 會用非常有自信的語氣講舊規則。<b>這比手冊過期嚴重——手冊過期至少有人會看到，
    /// 中間產物沒人會去看。</b>失敗方向與 <c>DocIndexOutOfDateException</c> 一致：
    /// 寧可少答，不可錯答。</para>
    ///
    /// <para><b>為什麼是**物化**的欄位而不是查詢期比對 <see cref="SourceHash"/> 與現檔</b>：
    /// 正式機根本沒有來源檔（§10.11.6 實測 publish 產物 <c>.cs</c> 0 份、
    /// <c>.claude/specifications/</c> 一份都沒有）。查詢期比對在正式機上只有兩種結果——
    /// 全部誤判為 stale（整個語料消失），或一律當作新鮮（閘門形同虛設）。
    /// 故過期判定只發生在建置期，結果隨 DB 出貨。</para>
    /// </summary>
    public bool IsStale { get; set; }
}
