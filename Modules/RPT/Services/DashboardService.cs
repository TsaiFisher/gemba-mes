using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.RPT.Models;

namespace SmartFactory.MES.Modules.RPT.Services
{
    public class DashboardService : IDashboardService
    {
        private readonly ApplicationDbContext _context;
        private readonly IDateTimeService _clock;
        public DashboardService(ApplicationDbContext context, IDateTimeService clock)
        {
            _context = context;
            _clock = clock;
        }

        public async Task<DashboardKpi> GetKpiAsync(string plantId, CancellationToken ct = default)
        {
            var today    = _clock.GetLocalDate();
            var thisYear = today.Year;
            var yearStart = new DateOnly(thisYear, 1, 1);
            var yearEnd = yearStart.AddYears(1);
            var localMonthStart = new DateTime(thisYear, today.Month, 1);
            var monthStart = _clock.ToUtcTime(localMonthStart);
            var monthEnd = _clock.ToUtcTime(localMonthStart.AddMonths(1));

            // 12-month revenue trend (GoodsShipment, Confirmed)
            var revTrend = await _context.GoodsShipments
                // 同 GrossMarginService：完成的出貨是 Posted，只認 Confirmed 會漏掉整條趨勢。
                .Where(s => s.PlantId == plantId
                         && (s.Status == DocStatus.Confirmed || s.Status == DocStatus.Posted)
                         && s.ShipDate >= yearStart && s.ShipDate < yearEnd)
                .GroupBy(s => s.ShipDate.Month)
                .Select(g => new { Month = g.Key, Amount = g.Sum(x => x.TotalAmount) })
                .AsNoTracking()
                .ToListAsync(ct);

            // 12-month purchase trend (GoodsReceipt, Confirmed)
            var purTrend = await _context.GoodsReceipts
                // 進貨同理：上架完成後是 Posted。
                .Where(r => r.PlantId == plantId
                         && (r.Status == DocStatus.Confirmed || r.Status == DocStatus.Posted)
                         && r.ReceiptDate >= yearStart && r.ReceiptDate < yearEnd)
                .GroupBy(r => r.ReceiptDate.Month)
                .Select(g => new { Month = g.Key, Amount = g.Sum(x => x.TotalAmount) })
                .AsNoTracking()
                .ToListAsync(ct);

            // Inventory value (QtyOnHand × UnitCost)
            var invValue = await _context.Inventories
                .Where(i => i.PlantId == plantId && i.QtyOnHand > 0)
                .SumAsync(i => i.QtyOnHand * i.UnitCost, ct);

            // Current month revenue and purchase
            var thisMonth = today.Month;
            var monthRev  = revTrend.FirstOrDefault(r => r.Month == thisMonth)?.Amount ?? 0m;
            var monthPur  = purTrend.FirstOrDefault(r => r.Month == thisMonth)?.Amount ?? 0m;

            // Work order on-time rate (current month Closed orders)
            var closedWos = await _context.WorkOrders
                .Where(w => w.PlantId == plantId
                         && w.Status == WorkOrderStatus.Closed
                         && w.PlannedEndDate >= monthStart
                         && w.PlannedEndDate < monthEnd)
                .Select(w => new { w.WorkOrderId, w.PlannedEndDate, w.UpdatedAt })
                .AsNoTracking()
                .ToListAsync(ct);
            var onTime     = closedWos.Count(w => w.UpdatedAt <= w.PlannedEndDate);
            var onTimeRate = closedWos.Count == 0 ? 0m
                : Math.Round((decimal)onTime / closedWos.Count * 100, 1);

            // Open work orders
            var openWoCount = await _context.WorkOrders
                .CountAsync(w => w.PlantId == plantId
                    && (w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.InProgress), ct);

            static string MonthLabel(int m) => m.ToString("00");

            return new DashboardKpi
            {
                MonthlyRevenue      = monthRev,
                MonthlyPurchase     = monthPur,
                InventoryValue      = invValue,
                WorkOrderOnTimeRate = onTimeRate,
                OpenWorkOrders      = openWoCount,
                RevenueTrend  = Enumerable.Range(1, 12)
                    .Select(m => new MonthlyTrendPoint(
                        MonthLabel(m),
                        revTrend.FirstOrDefault(r => r.Month == m)?.Amount ?? 0m))
                    .ToList(),
                PurchaseTrend = Enumerable.Range(1, 12)
                    .Select(m => new MonthlyTrendPoint(
                        MonthLabel(m),
                        purTrend.FirstOrDefault(r => r.Month == m)?.Amount ?? 0m))
                    .ToList()
            };
        }
    }
}
