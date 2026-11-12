using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;

namespace SmartFactory.MES.Services
{
    public interface IPurchaseService
    {
        // ── PurchaseRequest ────────────────────────────────────────────────────

        Task<PurchaseRequest> CreatePurchaseRequestAsync(
            PurchaseRequestFormVm vm, string userId, string plantId, CancellationToken ct = default);

        Task<PurchaseRequest> UpdatePurchaseRequestAsync(
            PurchaseRequestFormVm vm, string userId, CancellationToken ct = default);

        /// <summary>刪除請購單（含明細）；若有已轉採購明細則拒絕刪除</summary>
        Task DeletePurchaseRequestAsync(string id, CancellationToken ct = default);

        // ── PurchaseOrder ──────────────────────────────────────────────────────

        Task<PurchaseOrder> CreatePurchaseOrderAsync(
            PurchaseOrderFormVm vm, string userId, string plantId, CancellationToken ct = default);

        Task<PurchaseOrder> UpdatePurchaseOrderAsync(
            PurchaseOrderFormVm vm, string userId, CancellationToken ct = default);

        /// <summary>刪除採購單；若有已進貨明細則拒絕刪除</summary>
        Task DeletePurchaseOrderAsync(string id, CancellationToken ct = default);

        /// <summary>取得指定請購單的未結案明細，供 PO 選入</summary>
        Task<List<PurchaseRequestLine>> GetOpenPrLinesAsync(
            string purchaseRequestId, string plantId, CancellationToken ct = default);

        // ── GoodsReceipt ───────────────────────────────────────────────────────

        Task<GoodsReceipt> CreateGoodsReceiptAsync(
            GoodsReceiptFormVm vm, string userId, string plantId, CancellationToken ct = default);

        Task<GoodsReceipt> UpdateGoodsReceiptAsync(
            GoodsReceiptFormVm vm, string userId, CancellationToken ct = default);

        /// <summary>刪除進貨單；恢復 PO_D.ReceivedQty</summary>
        Task DeleteGoodsReceiptAsync(string id, CancellationToken ct = default);

        /// <summary>作廢進貨單時回沖 PO 明細累計進貨量（含在途 Supply 預留還原）；不含 SaveChanges，由呼叫端一併提交。</summary>
        Task RestorePoReceivedQtyAsync(IEnumerable<GoodsReceiptLine> lines, CancellationToken ct = default);

        // ── GoodsReturn ────────────────────────────────────────────────────────

        Task<GoodsReturn> CreateGoodsReturnAsync(
            GoodsReturnFormVm vm, string userId, string plantId, CancellationToken ct = default);

        Task<GoodsReturn> UpdateGoodsReturnAsync(
            GoodsReturnFormVm vm, string userId, CancellationToken ct = default);

        Task DeleteGoodsReturnAsync(string id, CancellationToken ct = default);

        /// <summary>作廢退貨單時回沖 GR 明細累計退貨量；不含 SaveChanges，由呼叫端一併提交。</summary>
        Task RestoreGrReturnedQtyAsync(IEnumerable<GoodsReturnLine> lines, CancellationToken ct = default);
    }
}
