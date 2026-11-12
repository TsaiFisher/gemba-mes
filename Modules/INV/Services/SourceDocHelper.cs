using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 查詢來源單據尚可沖銷數量的靜態輔助工具。
    /// TypeCategory 對應 InventoryTransactionType.TypeCategory 設定值。
    /// </summary>
    public static class SourceDocHelper
    {
        /// <summary>
        /// 取得指定來源明細的剩餘可沖銷數量。
        /// 同時扣除尚未過帳（Draft / PendingApproval）的退回單，避免審核流程中超額沖銷。
        /// 回傳 null 表示 TypeCategory 不支援（不做數量驗證）。
        /// </summary>
        /// <param name="excludeReturnDocId">編輯現有沖銷單時傳入該單號，避免重複計入自身數量。</param>
        public static async Task<decimal?> GetRemainingQtyAsync(
            ApplicationDbContext context,
            string? sourceTypeCategory,
            string sourceDocId,
            int sourceLineNo,
            string? excludeReturnDocId = null,
            CancellationToken ct = default)
        {
            switch (sourceTypeCategory)
            {
                case "Loan":
                {
                    var src = await context.LoanDocLines
                        .Where(l => l.LoanDocId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => new { l.Qty, l.QtySourceCleared })
                        .FirstOrDefaultAsync(ct);
                    if (src == null) return null;
                    var pending = await context.LoanDocLines
                        .Where(r => r.SourceDocId == sourceDocId && r.SourceLineNo == sourceLineNo
                                 && (excludeReturnDocId == null || r.LoanDocId != excludeReturnDocId)
                                 && r.LoanDoc!.Status != DocStatus.Posted
                                 && r.LoanDoc!.Status != DocStatus.Void)
                        .SumAsync(r => (decimal?)r.Qty, ct) ?? 0m;
                    return src.Qty - src.QtySourceCleared - pending;
                }

                case "Sample":
                {
                    var src = await context.SampleDocLines
                        .Where(l => l.SampleDocId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => new { l.Qty, l.QtySourceCleared })
                        .FirstOrDefaultAsync(ct);
                    if (src == null) return null;
                    var pending = await context.SampleDocLines
                        .Where(r => r.SourceDocId == sourceDocId && r.SourceLineNo == sourceLineNo
                                 && (excludeReturnDocId == null || r.SampleDocId != excludeReturnDocId)
                                 && r.SampleDoc!.Status != DocStatus.Posted
                                 && r.SampleDoc!.Status != DocStatus.Void)
                        .SumAsync(r => (decimal?)r.Qty, ct) ?? 0m;
                    return src.Qty - src.QtySourceCleared - pending;
                }

                case "Rework":
                {
                    var src = await context.ReworkMaterialDocLines
                        .Where(l => l.ReworkMaterialDocId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => new { l.Qty, l.QtySourceCleared })
                        .FirstOrDefaultAsync(ct);
                    if (src == null) return null;
                    var pending = await context.ReworkMaterialDocLines
                        .Where(r => r.SourceDocId == sourceDocId && r.SourceLineNo == sourceLineNo
                                 && (excludeReturnDocId == null || r.ReworkMaterialDocId != excludeReturnDocId)
                                 && r.ReworkMaterialDoc!.Status != DocStatus.Posted
                                 && r.ReworkMaterialDoc!.Status != DocStatus.Void)
                        .SumAsync(r => (decimal?)r.Qty, ct) ?? 0m;
                    return src.Qty - src.QtySourceCleared - pending;
                }

                case "Outsource":
                {
                    var src = await context.OutsourceMaterialDocLines
                        .Where(l => l.OutsourceMaterialDocId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => new { l.Qty, l.QtySourceCleared })
                        .FirstOrDefaultAsync(ct);
                    if (src == null) return null;
                    var pending = await context.OutsourceMaterialDocLines
                        .Where(r => r.SourceDocId == sourceDocId && r.SourceLineNo == sourceLineNo
                                 && (excludeReturnDocId == null || r.OutsourceMaterialDocId != excludeReturnDocId)
                                 && r.OutsourceMaterialDoc!.Status != DocStatus.Posted
                                 && r.OutsourceMaterialDoc!.Status != DocStatus.Void)
                        .SumAsync(r => (decimal?)r.Qty, ct) ?? 0m;
                    return src.Qty - src.QtySourceCleared - pending;
                }

                case "Purchase":
                    return await context.GoodsReceiptLines
                        .Where(l => l.GoodsReceiptId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => (decimal?)(l.StockQty - l.QtySourceCleared))
                        .FirstOrDefaultAsync(ct);

                case "Scrap":
                    return await context.ScrapDocLines
                        .Where(l => l.ScrapDocId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => (decimal?)(l.Qty - l.QtySourceCleared))
                        .FirstOrDefaultAsync(ct);

                case "Adjustment":
                    return await context.StockAdjustmentLines
                        .Where(l => l.StockAdjustmentId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => (decimal?)(l.Qty - l.QtySourceCleared))
                        .FirstOrDefaultAsync(ct);

                case "Transfer":
                    return await context.StockTransferLines
                        .Where(l => l.StockTransferId == sourceDocId && l.LineNo == sourceLineNo)
                        .Select(l => (decimal?)(l.Qty - l.QtySourceCleared))
                        .FirstOrDefaultAsync(ct);

                default:
                    return null;
            }
        }
    }
}
