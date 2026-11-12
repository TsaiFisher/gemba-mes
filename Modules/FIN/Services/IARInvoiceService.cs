using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.FIN.Services
{
    public interface IARInvoiceService
    {
        /// <summary>由 GoodsShipment Confirm 後自動產生 ARInvoice（idempotent）</summary>
        Task<ARInvoice> GenerateFromShipmentAsync(string shipmentId, CancellationToken ct = default);

        /// <summary>
        /// 銷貨退回時以負量回沖來源出貨單的應收發票（方案：負向回沖原發票）。
        /// 依 shipmentId + shipmentLineNo 對應 AR 明細（<c>ARInvoiceLine.SourceLineNo</c> 開票時
        /// 即由 <c>GoodsShipmentLine.LineNo</c> 寫入），扣減已開立 Qty（不低於 0），重算稅/總額。
        /// 找不到發票/明細、或發票已非 Open（已收款/作廢）時 no-op。
        ///
        /// 回傳 <see cref="ReturnReversalResult"/> 而非發票物件：呼叫端**必須**知道
        /// 這次到底沖掉了多少、還有多少沒沖。原本回傳發票、而四個呼叫點全部把它丟掉，
        /// 「已收款發票遇上退貨 → 一毛都沒回沖」因此完全不留痕跡。
        ///
        /// 以行號而非品號比對：同一張出貨的同品號可能有多行（不同倉/批號/單價），
        /// 以品號比對會固定落在第一個尚有 Qty 的行，回沖到錯的單價與錯的行上——
        /// 總額也許碰巧相近，但明細的量價分佈已經錯位，且後續退第二行時會二度扣到同一行。
        /// 與 <c>IAPInvoiceService.ReverseForReturnedQtyAsync</c> 的行號口徑一致。
        /// </summary>
        Task<ReturnReversalResult> ReverseForReturnedQtyAsync(
            string shipmentId, int shipmentLineNo, decimal qty, CancellationToken ct = default);

        Task<ARInvoice> CreateManualAsync(ARInvoice invoice,
            DocumentApprovalStatus? approvalStatus = null, CancellationToken ct = default);

        /// <summary>核准後補開手動發票的認列分錄（冪等；非手動發票不處理）。</summary>
        Task PostManualJournalAsync(string invoiceId, CancellationToken ct = default);

        Task VoidAsync(string invoiceId, CancellationToken ct = default);

        /// <summary>
        /// 已作廢的出貨發票依來源出貨重新開立一張（2026-10-22）。系統只在出貨確認當下自動開票，
        /// 出貨已過帳後作廢發票就再也沒有入口——手動發票不連出貨，GS_NO_AR_INVOICE 清不掉、擋月結。
        /// 開票量照自動開票的規則（出貨量 − 已過帳退貨量）。來源出貨已作廢、或已有未作廢發票時擋下。
        /// </summary>
        Task<ARInvoice> ReissueFromSourceAsync(string voidedInvoiceId, CancellationToken ct = default);

        /// <summary>取得客戶未結餘額（本位幣）</summary>
        Task<decimal> GetOutstandingBalanceAsync(string customerCode, string plantId, CancellationToken ct = default);
    }
}
