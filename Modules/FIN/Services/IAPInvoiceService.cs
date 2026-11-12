using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public interface IAPInvoiceService
    {
        /// <summary>
        /// 由 GoodsReceipt Confirm 後自動產生 APInvoice。
        /// 若同單據已有 Invoice（SourceDocId 唯一索引），則直接回傳既有 Invoice（idempotent）。
        /// </summary>
        Task<APInvoice> GenerateFromGoodsReceiptAsync(string goodsReceiptId, CancellationToken ct = default);

        /// <summary>
        /// 依「進正常倉的實際量」逐次累加認列應付（方案 A：一張進貨單維持一張 AP，金額累加）。
        /// 進貨單完成時不再整批認列；改由收貨明細每次進正常倉（IQC 合格放行或免檢直放）時，
        /// 就該批量呼叫本方法把數量累加進同一張 AP 發票的對應明細。
        /// 已認列數量即該 AP 明細現有 Qty（不需額外欄位追蹤）。冪等單位是「數量」而非「單據」：
        /// 每次呼叫都真的累加，呼叫端須確保只在實際進正常倉時呼叫、且傳入該次批量。
        /// 累計認列量不得超過該進貨明細的收貨量；AP 須仍為 Open（未付款/未同步）才可累加。
        /// </summary>
        Task<APInvoice> AccrueForReceivedQtyAsync(
            string goodsReceiptId, int goodsReceiptLineNo, decimal qty, CancellationToken ct = default);

        /// <summary>
        /// 進貨退出時以負量回沖來源進貨單的應付發票（方案：負向回沖原發票）。
        /// 依 goodsReceiptId + goodsReceiptLineNo 找到原 AP 發票對應明細，把已認列 Qty 扣減 qty
        /// （不低於 0），重算稅/總額，並入反向 GL 分錄（借 應付／貸 進貨暫估應付＋進項稅額）。
        /// 找不到發票/明細、或發票已非 Open（已付款/作廢）時不動作（呼叫端不因此中斷）。
        ///
        /// 回傳 <see cref="ReturnReversalResult"/> 而非發票物件：呼叫端**必須**知道
        /// 這次沖掉多少、還有多少沒沖——「已付款發票遇上退貨」的缺口原本完全無聲。
        /// </summary>
        Task<ReturnReversalResult> ReverseForReturnedQtyAsync(
            string goodsReceiptId, int goodsReceiptLineNo, decimal qty, CancellationToken ct = default);

        /// <summary>由 OutsourceReceipt Confirm 後自動產生 APInvoice</summary>
        Task<APInvoice> GenerateFromOutsourceReceiptAsync(string receiptId, CancellationToken ct = default);

        /// <summary>手動建立（廠商自送發票）</summary>
        Task<APInvoice> CreateManualAsync(APInvoice invoice,
            DocumentApprovalStatus? approvalStatus = null, CancellationToken ct = default);

        /// <summary>核准後補開手動發票的認列分錄（冪等；非手動發票不處理）。</summary>
        Task PostManualJournalAsync(string invoiceId, CancellationToken ct = default);

        /// <summary>作廢 Invoice（僅未核銷過任何付款者可作廢；已付款/部分付款須先沖銷付款單）</summary>
        Task VoidAsync(string invoiceId, CancellationToken ct = default);

        /// <summary>
        /// 已作廢的進貨應付依來源進貨重新認列一張（2026-10-22，與 AR 對稱）。每行重新認列「作廢那張上的已認列量」，
        /// 再由 <see cref="AccrueForReceivedQtyAsync"/> 的上限（收貨量 − 已過帳退貨量）截住。
        /// 作廢之後又過帳了退出時擋下——那段退貨沒有回沖到任何發票，重開量會算不準，請人手動處理。
        /// </summary>
        Task<APInvoice> ReissueFromSourceAsync(string voidedInvoiceId, CancellationToken ct = default);

        /// <summary>取得廠商未結餘額（本位幣）</summary>
        Task<decimal> GetOutstandingBalanceAsync(string supplierCode, string plantId, CancellationToken ct = default);
    }
}
