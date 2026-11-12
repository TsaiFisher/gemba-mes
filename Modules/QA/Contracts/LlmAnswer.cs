namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 一次生成的結果（規格 §10.8.1）。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。
///
/// <para><b>拿到這個物件就代表生成成功。</b>空字串、被截斷（<c>done_reason == "length"</c>）
/// 這些「HTTP 200 但沒有答案」的情形一律在 client 內轉成例外，不會走到這裡——
/// 呼叫端因此不必再判斷 <see cref="Text"/> 是否為空（§10.8.1 第 3 點）。</para>
/// </summary>
/// <param name="Text">模型的回覆全文，必定非空白。</param>
/// <param name="Model">實際回覆的模型名稱。與組態要求的可能不同（Ollama 會補 <c>:latest</c>）。</param>
/// <param name="DoneReason">Ollama 的 <c>done_reason</c>，能走到這裡必定是 <c>stop</c>；保留供記錄。</param>
/// <param name="PromptTokens">prompt 的 token 數，供估算 context 用量。</param>
/// <param name="CompletionTokens">生成的 token 數。</param>
/// <param name="Duration">
/// Ollama 回報的 <c>total_duration</c>（含載入模型）。**不是牆鐘時間**——
/// 開發機實測 client 端牆鐘 23.5 秒而此值 4.1 秒，差距是首次載入 9.6 GB 模型的時間。
/// 拿它當效能指標會低估使用者實際等待。
/// </param>
public readonly record struct LlmAnswer(
    string Text,
    string Model,
    string DoneReason,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration);

/// <summary>
/// 要嵌入的文字扮演什麼角色。**這個參數存在的唯一理由是前綴慣例**（§10.7）。
///
/// <para>e5 的 <c>query: </c>／<c>passage: </c>、arctic 的查詢前綴、embeddinggemma 的 task prompt——
/// 各家不同，漏加不會報錯，只會讓檢索品質默默下降。所以前綴不由呼叫端拼，
/// 而是由 <c>OllamaClient</c> 依「模型 × 角色」統一決定（§10.7 實作要求 1）。</para>
/// </summary>
public enum EmbeddingKind
{
    /// <summary>使用者的提問。查詢期使用。</summary>
    Query,

    /// <summary>手冊內容。索引期使用。</summary>
    Passage,
}

/// <summary>
/// 推論服務的健康狀態（§17.8：關閉與降級都要出聲）。
///
/// <para>存在的理由是**把三種完全不同的故障分開**：服務沒開、模型沒拉、組態填錯。
/// 三者的症狀在使用者眼中都是「助理不會回答」，但處理方式完全不同。</para>
/// </summary>
/// <param name="Reachable">能否連上 <c>/api/version</c>。false 時其餘欄位無意義。</param>
/// <param name="Version">Ollama 版本字串。</param>
/// <param name="ChatModelPresent">組態指定的生成模型是否已存在於該服務。</param>
/// <param name="EmbeddingModelPresent">組態指定的嵌入模型是否已存在於該服務。</param>
/// <param name="Detail">給人看的一句話，直接可寫進 log 或診斷畫面。</param>
public readonly record struct OllamaHealth(
    bool Reachable,
    string Version,
    bool ChatModelPresent,
    bool EmbeddingModelPresent,
    string Detail);
