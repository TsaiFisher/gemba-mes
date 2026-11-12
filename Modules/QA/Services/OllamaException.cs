namespace SmartFactory.MES.Modules.QA.Services;

/// <summary>
/// 推論服務呼叫失敗（規格 §10.8.1）。
///
/// <para><b>為什麼是例外而不是回傳一個「失敗」結果</b>：這一層所有的失敗形狀都是
/// 「HTTP 200 卻沒有可用的答案」——空字串、被 <c>num_predict</c> 截斷、
/// 批次數量對不上。用回傳值表示，呼叫端漏判就會把空字串送到畫面上，
/// 而那正是 §10.8.1 第 3 點要禁止的事。例外漏接會炸掉並留下堆疊，
/// **失敗方向刻意選成吵鬧的那一邊**。</para>
///
/// <para><see cref="Message"/> 一律寫成使用者／維運看得懂的中文，並帶上可行動的下一步
/// （拉哪個模型、開哪個服務、改哪個組態鍵）。</para>
/// </summary>
public class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }

    public OllamaException(string message, Exception inner) : base(message, inner) { }
}
