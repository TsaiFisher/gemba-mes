using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN.Services
{
    /// <summary>
    /// 折讓單服務（W3-F3 Phase 2）。AR／AP 兩側各一支實作，介面刻意分開
    /// ——方向相反（減應收 vs 減應付）、分錄科目不同，硬併成一支只會讓
    /// 每個方法都帶一個 bool 參數，而那個 bool 一旦傳錯是靜默的。
    /// </summary>
    public interface IARCreditNoteService
    {
        /// <summary>
        /// 由已過帳的銷退單產生折讓單草稿——只涵蓋「來源發票已（部分）收款」的那些行。
        ///
        /// 冪等：同一張銷退單重複呼叫只會有一張折讓單（唯一索引 + 前置查詢雙保險）。
        /// 沒有任何一行需要折讓時回傳 <c>null</c>，**不建空單**。
        /// </summary>
        Task<ARCreditNote?> CreateDraftFromSalesReturnAsync(string salesReturnId, CancellationToken ct = default);

        /// <summary>
        /// 確認折讓：累加原發票的 <c>CreditedAmount</c>、產生分錄、單據轉 Posted。
        /// 分錄與 Open 發票的既有回沖完全相同：借 銷貨收入 ＋ 借 銷項稅額 ／ 貸 應收帳款。
        /// </summary>
        Task ConfirmAsync(string creditNoteId, CancellationToken ct = default);

        /// <summary>作廢：回沖分錄、把 <c>CreditedAmount</c> 減回去。草稿直接轉 Void。</summary>
        Task VoidAsync(string creditNoteId, string? reason = null, CancellationToken ct = default);
    }

    /// <summary>進貨折讓單服務（AP 側，與 <see cref="IARCreditNoteService"/> 對稱）。</summary>
    public interface IAPDebitNoteService
    {
        /// <summary>由已過帳的進貨退出單產生折讓單草稿；語意與 AR 側相同。</summary>
        Task<APDebitNote?> CreateDraftFromGoodsReturnAsync(string goodsReturnId, CancellationToken ct = default);

        /// <summary>確認折讓：借 應付帳款 ／ 貸 進貨暫估應付 ＋ 貸 進項稅額（與 AP 既有回沖相同）。</summary>
        Task ConfirmAsync(string debitNoteId, CancellationToken ct = default);

        /// <summary>作廢。</summary>
        Task VoidAsync(string debitNoteId, string? reason = null, CancellationToken ct = default);
    }
}
