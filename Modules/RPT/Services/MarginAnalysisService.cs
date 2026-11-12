using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>
    /// 多維度銷售毛利分析服務。
    /// 營收：已確認出貨明細（GoodsShipmentLine ⨝ GoodsShipment，Confirmed，期間內）。
    /// 成本：出貨庫存異動（StockMovement TypeCode=GS、Direction=-1），以 SourceDocNo 對應回出貨單以取客戶。
    /// 依 Customer／Product／Month 維度於記憶體彙總，避免各維度重寫 SQL。
    /// 以明確 plantId 過濾，不依賴 HTTP 環境廠別，便於整合測試。
    /// </summary>
    public class MarginAnalysisService : IMarginAnalysisService
    {
        private readonly ApplicationDbContext _context;
        public MarginAnalysisService(ApplicationDbContext context) => _context = context;

        public async Task<MarginAnalysisReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, string dimension,
            CancellationToken ct = default)
        {
            dimension = Normalize(dimension);

            // 營收明細（含客戶、品號、出貨日、單號）
            var revenueRaw = await _context.GoodsShipmentLines
                .Where(l => l.GoodsShipment!.PlantId == plantId
                         && l.GoodsShipment.ShipDate >= fromDate
                         && l.GoodsShipment.ShipDate <= toDate
                         && // 已完成的出貨停在 Posted 不是 Confirmed（過帳時 SetDocPosted 推上去），
                         // 而成本腿看的 StockMovement 也是過帳當下才產生——只認 Confirmed 會讓
                         // 兩腿永遠不同時到齊：完成的出貨有成本沒營收、未完成的有營收沒成本。
                         // 對照 OutsourcePerformanceService 一開始就是 Confirmed || Posted。
                         (l.GoodsShipment.Status == DocStatus.Confirmed
                          || l.GoodsShipment.Status == DocStatus.Posted))
                .Select(l => new
                {
                    l.GoodsShipmentId,
                    Customer  = l.GoodsShipment!.CompanyCode,
                    l.ProductCode,
                    l.GoodsShipment.ShipDate,
                    l.LineAmount,
                })
                .AsNoTracking()
                .ToListAsync(ct);

            // 成本異動（GS 出庫）；SourceDocNo = 出貨單號，供客戶維度回溯
            var costRaw = await _context.StockMovements
                .Where(m => m.PlantId      == plantId
                         && m.TypeCode     == "GS"
                         && m.Direction    == -1
                         && m.MovementDate >= fromDate
                         && m.MovementDate <= toDate
                         // 已作廢出貨的原出庫列要排除：營收腿不算作廢單，成本腿卻照算的話，
                         // 作廢一張出貨毛利就憑空少一筆成本（作廢沖銷列方向是 +1，本來就不在這裡）。2026-10-22
                         && !_context.GoodsShipments.Any(s => s.GoodsShipmentId == m.DocNo && s.Status == DocStatus.Void))
                .Select(m => new { m.SourceDocNo, m.ProductCode, m.MovementDate, Cost = m.TotalCost ?? 0m })
                .AsNoTracking()
                .ToListAsync(ct);

            // 出貨單 → 客戶對照（供成本客戶維度）
            var shipCustomer = await _context.GoodsShipments
                .Where(s => s.PlantId == plantId && s.ShipDate >= fromDate && s.ShipDate <= toDate)
                .Select(s => new { s.GoodsShipmentId, s.CompanyCode })
                .AsNoTracking()
                .ToListAsync(ct);
            var shipCustomerMap = shipCustomer
                .GroupBy(s => s.GoodsShipmentId)
                .ToDictionary(g => g.Key, g => g.First().CompanyCode);

            // 依維度取鍵
            string RevKey(string customer, string product, DateOnly shipDate) => dimension switch
            {
                "Customer" => customer,
                "Month"    => shipDate.ToString("yyyy-MM"),
                _          => product,
            };
            string CostKey(string? sourceDocNo, string product, DateOnly moveDate) => dimension switch
            {
                "Customer" => sourceDocNo != null && shipCustomerMap.TryGetValue(sourceDocNo, out var c) ? c : "?",
                "Month"    => moveDate.ToString("yyyy-MM"),
                _          => product,
            };

            var revByKey = revenueRaw
                .GroupBy(r => RevKey(r.Customer, r.ProductCode, r.ShipDate))
                .ToDictionary(
                    g => g.Key,
                    g => (Revenue: g.Sum(x => x.LineAmount),
                          ShipCount: g.Select(x => x.GoodsShipmentId).Distinct().Count()));

            var costByKey = costRaw
                .GroupBy(m => CostKey(m.SourceDocNo, m.ProductCode, m.MovementDate))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Cost));

            // 顯示名對照
            var names = await ResolveNamesAsync(dimension, revByKey.Keys, ct);

            var rows = revByKey
                .Select(kv => new MarginRow
                {
                    DimensionKey  = kv.Key,
                    DimensionName = names.TryGetValue(kv.Key, out var n) ? n : kv.Key,
                    Revenue       = kv.Value.Revenue,
                    Cost          = costByKey.TryGetValue(kv.Key, out var c) ? c : 0m,
                    ShipCount     = kv.Value.ShipCount,
                })
                .OrderByDescending(r => r.Revenue)
                .ToList();

            return new MarginAnalysisReport { Dimension = dimension, Rows = rows };
        }

        private async Task<Dictionary<string, string>> ResolveNamesAsync(
            string dimension, IEnumerable<string> keys, CancellationToken ct)
        {
            var keyList = keys.Distinct().ToList();
            switch (dimension)
            {
                case "Customer":
                    return await _context.Companies.AsNoTracking()
                        .Where(c => keyList.Contains(c.CompanyCode))
                        .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName, ct);
                case "Product":
                    return await _context.Products.AsNoTracking()
                        .Where(p => keyList.Contains(p.ProductCode))
                        .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName, ct);
                default: // Month：鍵即名稱
                    return keyList.ToDictionary(k => k, k => k);
            }
        }

        private static string Normalize(string? dimension) => dimension switch
        {
            "Customer" => "Customer",
            "Month"    => "Month",
            _          => "Product",
        };
    }
}
