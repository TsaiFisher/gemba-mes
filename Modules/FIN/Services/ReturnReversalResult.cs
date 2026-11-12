namespace SmartFactory.MES.Modules.FIN.Services
{
    /// <summary>退貨回沖的結果分類。</summary>
    public enum ReturnReversalOutcome
    {
        /// <summary>來源單根本沒開票／沒認列（例如全走待驗未放行）——本來就沒東西可回沖。</summary>
        NoInvoice = 0,

        /// <summary>正常回沖。</summary>
        Reversed = 1,

        /// <summary>
        /// 發票已（部分）收款／付款。本方案不回沖已結清的發票——
        /// 動已收款發票的金額會讓憑證與稅務申報前後不一致，正解是另開折讓單。
        /// 缺口金額放在 <see cref="ReturnReversalResult.UnreversedGrossLocal"/>。
        /// </summary>
        SettledInvoice = 2,

        /// <summary>發票已作廢——沒有應收可沖，缺口為 0。</summary>
        VoidInvoice = 3,

        /// <summary>對不到來源行號（明細已被沖完，或行號口徑不一致）。</summary>
        LineNotFound = 4,
    }

    /// <summary>
    /// 退貨回沖的結果。
    ///
    /// 這個型別存在的理由只有一個：**「沒回沖」必須傳得出去**。
    /// 原本 AR／AP 兩支服務都回傳發票物件，而四個呼叫點
    /// （銷退確認、銷退上架、採購退貨確認、退貨下架）**全部把回傳值丟掉**——
    /// 於是「來源發票已收款，本次退貨一毛都沒回沖」這件事在系統裡不留任何痕跡：
    /// 畫面不提示、單據上看不到、稽核也沒有一條在看。
    /// 客戶多付的錢就這樣消失在一個 early return 裡。
    ///
    /// 金額一律**本位幣含稅**：呼叫端要拿去顯示給人看，換算口徑必須只有一個。
    /// </summary>
    /// <param name="InvoiceId">來源發票單號；沒有來源發票時為 null。</param>
    /// <param name="ReversedGrossLocal">實際回沖掉的金額（本位幣、含稅）。</param>
    /// <param name="UnreversedGrossLocal">**應該回沖但沒有回沖**的金額（本位幣、含稅）。</param>
    /// <param name="Outcome">分類，供呼叫端決定要不要提示、提示什麼。</param>
    /// <remarks>
    /// 宣告成 <c>readonly record struct</c> 而非 class：它是一個沒有身分的小結果值，
    /// 而 R5b（模組命名空間下 public class 的欄位名必須有 resx 鍵）掃的是 class——
    /// `HasGap`／`ReversedGrossLocal` 這種欄位沒有任何表單會繫結，補 resx 鍵等於造假欄位名。
    /// 值型別是這條規則寫明的正規解法之一（另兩條是 nested private 與 baseline，
    /// 後者只給既有程式用）。
    /// </remarks>
    public readonly record struct ReturnReversalResult(
        string? InvoiceId,
        decimal ReversedGrossLocal,
        decimal UnreversedGrossLocal,
        ReturnReversalOutcome Outcome)
    {
        /// <summary>來源單沒有發票可回沖。</summary>
        public static ReturnReversalResult NoInvoice { get; }
            = new(null, 0m, 0m, ReturnReversalOutcome.NoInvoice);

        /// <summary>有缺口＝有錢沒被沖掉，呼叫端必須讓人看見。</summary>
        public bool HasGap => UnreversedGrossLocal > 0m;
    }
}
