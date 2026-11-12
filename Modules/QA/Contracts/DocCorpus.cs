namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// <c>DocChunk.Corpus</c> 的合法值（規格 §10.11.7 定案 #42）。
///
/// <para><b>兩種語料同住一張表</b>，不是兩張表：兩張表等於兩處都得記得重嵌，
/// 而漏掉的那一半不會報錯，只會安靜地用舊向量檢索——正是 §10.7 存
/// <c>EmbeddingModel</c>／<c>EmbeddingDim</c> 要防的那種失敗。</para>
///
/// <para><b>代價要記著：同一張表、兩套權限規則。</b>手冊走 MenuKey 過濾、
/// 擷取物開放查詢（定案 #31／#32），兩套都寫在 <c>DocChunkFilter</c> 的同一段 SQL 裡。
/// 寫的時候很容易只記得其中一套，所以那段 SQL 的兩個分支必須並排、不得拆散。</para>
///
/// <para><b>為什麼是字串常數而不是 enum</b>：這個值要落到資料庫欄位並被字面拼進 SQL。
/// 存序數的話，日後在列舉中間插一個成員就會讓既有列全部改變意義，而且不會有任何錯誤；
/// 存名稱則是把「值」與「宣告順序」解耦。</para>
/// </summary>
public static class DocCorpus
{
    /// <summary>使用手冊 HTML 原文（§10.11.2）。權限走 <c>MenuKey</c>，回答「這個畫面怎麼操作」。</summary>
    public const string Manual = "manual";

    /// <summary>
    /// 規格 <c>.md</c> 與程式碼的**擷取改寫物**（§10.11.2）。原文永遠不進索引。
    /// 不綁畫面故開放查詢，回答「規則是什麼、為什麼」。
    /// </summary>
    public const string Extract = "extract";

    /// <summary>
    /// 兩個值都會被**字面**拼進 <c>DocChunkFilter</c> 的 SQL，故成員必須是小寫英文字。
    /// 由 <c>DocCorpusFacts</c> 斷言。
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[] { Manual, Extract };
}
