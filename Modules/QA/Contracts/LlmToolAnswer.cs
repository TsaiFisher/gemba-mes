namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>模型選定的一次工具呼叫。</summary>
/// <param name="ToolKey">模型回的工具名。⚠️ **未經驗證**——呼叫端必須重新確認它在授權清單內（§8.7）。</param>
/// <param name="ArgumentsJson">參數的原始 JSON，直接餵給 <c>IReportToolRunner.RunAsync</c>。</param>
public readonly record struct LlmToolCall(string ToolKey, string ArgumentsJson);

/// <summary>
/// 帶工具生成的結果。<see cref="ToolCalls"/> 為空 ＋ <see cref="Content"/> 有值
/// ＝ 模型判定沒有對應工具（§9.2 的誠實拒答），**這是正常路徑不是錯誤**。
///
/// <para><c>readonly record struct</c> 與 <see cref="LlmAnswer"/> 同形狀：
/// 除了一致性，也因為 R5b 會把 class 上的 <c>int</c>／<c>TimeSpan</c> 屬性當成隱含必填的
/// 表單欄位而要求 resx 雙語鍵——這是推論的計量欄位，不會出現在任何表單上。</para>
/// </summary>
public readonly record struct LlmToolAnswer(
    IReadOnlyList<LlmToolCall> ToolCalls,
    string Content,
    string Model,
    int PromptEvalCount,
    int EvalCount,
    TimeSpan Duration);
