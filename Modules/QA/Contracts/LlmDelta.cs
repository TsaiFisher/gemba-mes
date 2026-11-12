namespace SmartFactory.MES.Modules.QA.Contracts;

/// <summary>
/// 串流生成的一小段（規格 §11.2 C4）。<c>readonly record struct</c>：見 <c>ParsedChunk</c> 對 R5b 的說明。
///
/// <para><b>序列的形狀是固定的：0..n 個「只有 <see cref="Text"/>」的項，最後恰好一個
/// 「<see cref="Final"/> 非 null 且 <see cref="Text"/> 為空」的項。</b>
/// 呼叫端可以靠 <see cref="Final"/> 判斷「串流正常結束」，而不是靠「列舉自然跑完」——
/// 兩者不一樣：連線中斷時列舉也會跑完，但不會有帶 <see cref="Final"/> 的那一項。
/// 少了這個區分，被截斷的半截答案會長得像完整答案。</para>
///
/// <para><b>與 <c>ILlmClient.ChatAsync</c> 的契約一致：拿到 <see cref="Final"/> 就代表成功。</b>
/// 空內容與 <c>done_reason == "length"</c> 一樣在 client 內轉成例外，
/// 差別只在於例外可能在**已經吐出一部分 token 之後**才拋——呼叫端要能處理
/// 「畫面上已經有半段字了，然後失敗」，這是串流與非串流唯一的行為差異。</para>
/// </summary>
/// <param name="Text">這一段新增的文字。可能是半個詞、甚至半個字的位元組邊界後的結果。</param>
/// <param name="Final">
/// 非 null 代表串流已正常結束，內容是完整答案與統計。
/// <c>Final.Text</c> 是**累積的全文**，可直接拿去 <c>QaPromptBuilder.Parse</c>。
/// </param>
public readonly record struct LlmDelta(string Text, LlmAnswer? Final);
