using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public class OutsourcePerformanceService : IOutsourcePerformanceService
    {
        private readonly ApplicationDbContext _context;
        public OutsourcePerformanceService(ApplicationDbContext context) => _context = context;

        public async Task<OutsourcePerformanceReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, CancellationToken ct = default)
        {
            // 已確認/已過帳的委外收貨（草稿/作廢不列入；超耗扣款於 Confirm 時已算入明細）。
            // IgnoreQueryFilters + 明確 plantId：管理報表以廠別為維度呈現全廠資料（權限已於 Controller
            // 以 CanCreate 把關），不依賴 HTTP 環境廠別/資料授權列過濾，兼具可整合測試性。
            var receipts = await _context.OutsourceReceipts
                .IgnoreQueryFilters()
                .Where(r => r.PlantId == plantId
                         && r.ReceiptDate >= fromDate && r.ReceiptDate <= toDate
                         && (r.Status == DocStatus.Confirmed || r.Status == DocStatus.Posted))
                .Select(r => new
                {
                    r.SupplierCode,
                    r.OutsourcePurchaseOrderId,
                    r.ReceiptDate,
                    ReceivedQty   = r.Lines.Sum(l => l.ReceiptQty),
                    ProcessingFee = r.Lines.Sum(l => l.LineAmount),
                    ScrapPenalty  = r.Lines.Sum(l => l.ExcessScrapPenalty),
                })
                .AsNoTracking()
                .ToListAsync(ct);

            if (receipts.Count == 0) return new OutsourcePerformanceReport();

            // 準時率：對應委外採購單表頭預交日
            var opoIds = receipts.Select(r => r.OutsourcePurchaseOrderId).Distinct().ToList();
            var dueMap = (await _context.OutsourcePurchaseOrders
                    .IgnoreQueryFilters()
                    .Where(o => opoIds.Contains(o.OutsourcePurchaseOrderId))
                    .Select(o => new { o.OutsourcePurchaseOrderId, o.ExpectedDeliveryDate })
                    .AsNoTracking()
                    .ToListAsync(ct))
                .ToDictionary(o => o.OutsourcePurchaseOrderId, o => o.ExpectedDeliveryDate);

            // 供應商名稱
            var supplierCodes = receipts.Select(r => r.SupplierCode).Distinct().ToList();
            var nameMap = (await _context.Companies
                    .IgnoreQueryFilters()
                    .Where(c => supplierCodes.Contains(c.CompanyCode))
                    .Select(c => new { c.CompanyCode, c.CompanyName })
                    .AsNoTracking()
                    .ToListAsync(ct))
                .ToDictionary(c => c.CompanyCode, c => c.CompanyName);

            var rows = receipts
                .GroupBy(r => r.SupplierCode)
                .Select(g => new OutsourcePerformanceRow
                {
                    SupplierCode   = g.Key,
                    SupplierName   = nameMap.TryGetValue(g.Key, out var n) ? n : "",
                    ReceiptCount   = g.Count(),
                    EvaluableCount = g.Count(r => dueMap.TryGetValue(r.OutsourcePurchaseOrderId, out var d) && d.HasValue),
                    OnTimeCount    = g.Count(r => dueMap.TryGetValue(r.OutsourcePurchaseOrderId, out var d)
                                                  && d.HasValue && r.ReceiptDate <= d.Value),
                    ReceivedQty    = g.Sum(r => r.ReceivedQty),
                    ProcessingFee  = g.Sum(r => r.ProcessingFee),
                    ScrapPenalty   = g.Sum(r => r.ScrapPenalty),
                })
                .OrderByDescending(r => r.ScrapPenalty)
                .ThenBy(r => r.SupplierCode)
                .ToList();

            return new OutsourcePerformanceReport { Rows = rows };
        }
    }
}
