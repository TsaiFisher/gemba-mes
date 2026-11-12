using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>
    /// NCR 處置=Return → 自動建立採購退貨草稿單（roadmap-fin-cst-sub.md §5 / QCR 成本勾稽）。
    /// 與 NcrController 的 Scrap→ScrapDoc、Rework→WorkOrder 對稱，補齊 Return→GoodsReturn 一環。
    /// </summary>
    public class QcrReturnService : IQcrReturnService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;
        private readonly IDocNumberService _docNumber;

        public QcrReturnService(ApplicationDbContext db, IDocNumberService docNumber, IDateTimeService dt)
        {
            _db = db;
            _docNumber = docNumber;
            _dt = dt;
        }

        public async Task<string> CreateDraftReturnFromNcrAsync(
            NonConformanceReport ncr, string plantId, string userId, CancellationToken ct = default)
        {
            // 追溯進料來源：NCR → InspectionOrder(IQC) → GoodsReceipt
            if (ncr.SourceType != "InspectionOrder" || string.IsNullOrWhiteSpace(ncr.SourceId))
                throw new InvalidOperationException(PzText.Get("Qcr_ReturnOnlyForIqc"));

            // 追溯讀取以明確 plantId 過濾（IgnoreQueryFilters）：不依賴 HTTP 環境廠別即可運作
            // （Execute 時環境廠別本就等於本單 plantId），與其他跨模組服務同慣例、可整合測試。
            var order = await _db.InspectionOrders.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(o => o.InspectionOrderId == ncr.SourceId && o.PlantId == plantId, ct);
            if (order == null || order.InspectionType != "IQC" || string.IsNullOrWhiteSpace(order.SourceDocId))
                throw new InvalidOperationException(PzText.Get("Qcr_ReturnOnlyForIqc"));

            var receipt = await _db.GoodsReceipts.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(r => r.GoodsReceiptId == order.SourceDocId && r.PlantId == plantId, ct);
            if (receipt == null)
                throw new InvalidOperationException(PzText.Get("Qcr_SourceReceiptNotFound"));

            // 來源收貨明細（估價/倉位/幣別）：優先對應行號，否則同品號首行
            var rcptLine = await _db.GoodsReceiptLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.GoodsReceiptId == receipt.GoodsReceiptId
                         && (order.SourceLineNo != null ? l.LineNo == order.SourceLineNo : l.ProductCode == ncr.ProductCode))
                .OrderBy(l => l.LineNo)
                .FirstOrDefaultAsync(ct);

            var returnId  = await _docNumber.NextAsync("GoodsReturn", plantId, _dt.GetLocalDate(), ct);
            decimal unitPrice  = rcptLine?.UnitPrice ?? 0m;
            decimal lineAmount = Math.Round(unitPrice * ncr.Qty, 4, MidpointRounding.AwayFromZero);

            var ret = new GoodsReturn
            {
                GoodsReturnId  = returnId,
                ReturnDate     = _dt.GetLocalDate(),
                CreatedBy      = userId,
                SupplierCode   = receipt.SupplierCode,
                TypeCode       = "PF",
                TypeCategory   = "Purchase",
                Status         = DocStatus.Draft,
                ApprovalStatus = DocumentApprovalStatus.Draft,
                TotalAmount    = lineAmount,
                Remarks        = $"NCR {ncr.NcrId}",
                PlantId        = plantId,
            };
            ret.Lines.Add(new GoodsReturnLine
            {
                GoodsReturnId  = returnId,
                LineNo         = 1,
                GoodsReceiptId = receipt.GoodsReceiptId,
                ReceiptLineNo  = rcptLine?.LineNo,
                ProductCode    = ncr.ProductCode,
                ReturnQty      = ncr.Qty,
                StockQty       = ncr.Qty,
                UomCode        = ncr.UomCode ?? rcptLine?.UomCode,
                StockUomCode   = rcptLine?.StockUomCode ?? ncr.UomCode,
                UnitPrice      = unitPrice,
                CurrencyCode   = rcptLine?.CurrencyCode,
                LineAmount     = lineAmount,
                TotalAmount    = lineAmount,
                WhCode         = ncr.WhCode ?? rcptLine?.WhCode,
                LocationCode   = ncr.LocationCode ?? rcptLine?.LocationCode,
                LotNo          = ncr.LotNo ?? rcptLine?.LotNo,
                Remarks        = $"NCR {ncr.NcrId}",
            });

            _db.GoodsReturns.Add(ret);

            // 雙向連結：於 NCR 回填退貨單號（對稱 Scrap→ScrapDocId、Rework→ReworkWorkOrderId）。
            // ncr 由呼叫端以同一 scoped DbContext 追蹤載入（NcrController.Execute / 整合測試皆如此），
            // 故直接賦值即隨本次 SaveChanges 一併持久化。
            ncr.GoodsReturnId = returnId;

            await _db.SaveChangesAsync(ct);
            return returnId;
        }
    }
}
