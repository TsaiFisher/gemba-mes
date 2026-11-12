using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public class GrossMarginService : IGrossMarginService
    {
        private readonly ApplicationDbContext _context;
        public GrossMarginService(ApplicationDbContext context) => _context = context;

        public async Task<GrossMarginReport> GetReportAsync(
            DateOnly fromDate, DateOnly toDate, string plantId, CancellationToken ct = default)
        {
            // 收入：**已過帳**的出貨明細。
            //
            // 原本取的是 Confirmed，而正常流程走完之後單據是 Posted——兩者互斥，
            // 於是收入腿與成本腿系統性地各取一半：Confirmed 的單沒有 GS 出庫異動（成本 0，
            // 毛利率 100%），Posted 的單收入為 0（毛利率負值）。兩邊都不會報錯，
            // 畫面上就只是一個比較好看或比較難看的毛利率。
            // 成本腿取的是 StockMovement，而異動只在過帳後才存在，所以收入腿必須同步取 Posted，
            // 兩條腿才是同一批單據。
            var revenueRows = await _context.GoodsShipmentLines
                .Where(l => l.GoodsShipment!.PlantId == plantId
                         && l.GoodsShipment.ShipDate >= fromDate
                         && l.GoodsShipment.ShipDate <= toDate
                         && l.GoodsShipment.Status   == DocStatus.Posted)
                .GroupBy(l => l.ProductCode)
                .Select(g => new {
                    ProductCode = g.Key,
                    Revenue     = g.Sum(x => x.LineAmount),
                    ShipCount   = g.Select(x => x.GoodsShipmentId).Distinct().Count()
                })
                .AsNoTracking()
                .ToListAsync(ct);

            // COGS: StockMovement TypeCode=GS (Goods Shipment OUT) in date range
            var costRows = await _context.StockMovements
                .Where(m => m.PlantId      == plantId
                         && m.TypeCode     == "GS"
                         && m.Direction    == -1
                         && m.MovementDate >= fromDate
                         && m.MovementDate <= toDate
                         // 已作廢出貨的原出庫列要排除：營收腿不算作廢單，成本腿卻照算的話，
                         // 作廢一張出貨毛利就憑空少一筆成本（作廢沖銷列方向是 +1，本來就不在這裡）。2026-10-22
                         && !_context.GoodsShipments.Any(s => s.GoodsShipmentId == m.DocNo && s.Status == DocStatus.Void))
                .GroupBy(m => m.ProductCode)
                .Select(g => new {
                    ProductCode = g.Key,
                    Cost        = g.Sum(x => x.TotalCost ?? 0m)
                })
                .AsNoTracking()
                .ToListAsync(ct);

            var costMap = costRows.ToDictionary(r => r.ProductCode, r => r.Cost);

            var rows = revenueRows
                .OrderByDescending(r => r.Revenue)
                .Select(r => new GrossMarginRow
                {
                    ProductCode = r.ProductCode,
                    Revenue     = r.Revenue,
                    Cost        = costMap.TryGetValue(r.ProductCode, out var c) ? c : 0m,
                    ShipCount   = r.ShipCount
                })
                .ToList();

            return new GrossMarginReport { Rows = rows };
        }
    }
}
