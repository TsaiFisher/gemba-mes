using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Services;
using static SmartFactory.MES.Modules.RPT.Services.ContributionMarginCalculator;

namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>
    /// 產品邊際貢獻服務（路線 A）。
    /// 營收：已確認出貨明細；成本：GS StockMovement（混合 AVCO）。
    /// 變動占比：由 WorkOrderActualCost（工單實際材料/人工/製費）⨝ WorkOrder（取品號）依分類旗標推得；
    ///   某產品期間內無工單成本要素 → 退回「整體平均占比」（仍無 → 0，並標記 HasCostBasis=false）。
    /// 分類旗標存於系統參數（管理者可調），亦可單次查詢覆寫。
    /// </summary>
    public class ContributionMarginService : IContributionMarginService
    {
        private readonly ApplicationDbContext _context;
        private readonly ISystemParamService _systemParam;

        // 系統參數鍵（ProjectZ 自訂，非 Core SystemParamKeys）
        public const string KeyMaterial = "ContributionMargin.MaterialVariable";
        public const string KeyLabor    = "ContributionMargin.LaborVariable";
        public const string KeyOverhead = "ContributionMargin.OverheadVariable";

        public ContributionMarginService(ApplicationDbContext context, ISystemParamService systemParam)
        {
            _context     = context;
            _systemParam = systemParam;
        }

        public async Task<VariablePolicy> GetDefaultPolicyAsync(CancellationToken ct = default)
        {
            var all = await _systemParam.GetAllAsync();
            bool Flag(string key, bool dflt) =>
                all.TryGetValue(key, out var v) && v != null ? v == "true" : dflt;
            // 缺省：材料+人工＝變動、製費＝固定
            return new VariablePolicy(Flag(KeyMaterial, true), Flag(KeyLabor, true), Flag(KeyOverhead, false));
        }

        public async Task SaveDefaultPolicyAsync(VariablePolicy policy, CancellationToken ct = default)
        {
            await _systemParam.SetBatchAsync(new Dictionary<string, string?>
            {
                [KeyMaterial] = policy.Material ? "true" : "false",
                [KeyLabor]    = policy.Labor    ? "true" : "false",
                [KeyOverhead] = policy.Overhead ? "true" : "false",
            });
        }

        public async Task<ContributionMarginReport> BuildAsync(
            DateOnly fromDate, DateOnly toDate, string plantId,
            VariablePolicy? policyOverride = null, CancellationToken ct = default)
        {
            var policy = policyOverride ?? await GetDefaultPolicyAsync(ct);

            // 營收：已確認出貨明細，依品號彙總
            var revenueByProduct = (await _context.GoodsShipmentLines
                .Where(l => l.GoodsShipment!.PlantId == plantId
                         && l.GoodsShipment.ShipDate >= fromDate
                         && l.GoodsShipment.ShipDate <= toDate
                         && // 已完成的出貨停在 Posted 不是 Confirmed（過帳時 SetDocPosted 推上去），
                         // 而成本腿看的 StockMovement 也是過帳當下才產生——只認 Confirmed 會讓
                         // 兩腿永遠不同時到齊：完成的出貨有成本沒營收、未完成的有營收沒成本。
                         // 對照 OutsourcePerformanceService 一開始就是 Confirmed || Posted。
                         (l.GoodsShipment.Status == DocStatus.Confirmed
                          || l.GoodsShipment.Status == DocStatus.Posted))
                .Select(l => new { l.ProductCode, l.LineAmount })
                .AsNoTracking().ToListAsync(ct))
                .GroupBy(x => x.ProductCode)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.LineAmount));

            if (revenueByProduct.Count == 0)
                return new ContributionMarginReport
                {
                    MaterialVariable = policy.Material, LaborVariable = policy.Labor, OverheadVariable = policy.Overhead,
                };

            // 成本：GS 出庫，依品號彙總
            var costByProduct = (await _context.StockMovements
                .Where(m => m.PlantId == plantId && m.TypeCode == "GS" && m.Direction == -1
                         && m.MovementDate >= fromDate && m.MovementDate <= toDate
                         // 已作廢出貨的原出庫列要排除：營收腿不算作廢單，成本腿卻照算的話，
                         // 作廢一張出貨毛利就憑空少一筆成本（作廢沖銷列方向是 +1，本來就不在這裡）。2026-10-22
                         && !_context.GoodsShipments.Any(s => s.GoodsShipmentId == m.DocNo && s.Status == DocStatus.Void))
                .Select(m => new { m.ProductCode, Cost = m.TotalCost ?? 0m })
                .AsNoTracking().ToListAsync(ct))
                .GroupBy(x => x.ProductCode)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Cost));

            // 成本要素：WorkOrderActualCost ⨝ WorkOrder（取品號），期間內以 CreatedAt 篩
            var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var toUtc   = toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var elementRows = await (
                from c in _context.WorkOrderActualCosts.AsNoTracking()
                join w in _context.WorkOrders.AsNoTracking() on c.WorkOrderId equals w.WorkOrderId
                where c.PlantId == plantId && c.CreatedAt >= fromUtc && c.CreatedAt < toUtc
                select new { w.ProductCode, c.ActualMaterial, c.ActualLabor, c.ActualOverhead })
                .ToListAsync(ct);

            var elementByProduct = elementRows
                .GroupBy(x => x.ProductCode)
                .ToDictionary(g => g.Key, g => (
                    Material: g.Sum(x => x.ActualMaterial),
                    Labor:    g.Sum(x => x.ActualLabor),
                    Overhead: g.Sum(x => x.ActualOverhead)));

            // 整體平均占比（fallback）：全部要素合算
            decimal? overallRatio = null;
            if (elementRows.Count > 0)
                overallRatio = VariableRatio(
                    elementRows.Sum(x => x.ActualMaterial),
                    elementRows.Sum(x => x.ActualLabor),
                    elementRows.Sum(x => x.ActualOverhead), policy);

            // 品名對照
            var productCodes = revenueByProduct.Keys.ToList();
            var names = await _context.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName, ct);

            var rows = new List<ContributionMarginRow>();
            foreach (var (product, revenue) in revenueByProduct)
            {
                decimal? productRatio = null;
                if (elementByProduct.TryGetValue(product, out var e))
                    productRatio = VariableRatio(e.Material, e.Labor, e.Overhead, policy);

                var ratio = productRatio ?? overallRatio ?? 0m;
                rows.Add(new ContributionMarginRow
                {
                    ProductCode   = product,
                    ProductName   = names.TryGetValue(product, out var n) ? n : "",
                    Revenue       = revenue,
                    Cost          = costByProduct.TryGetValue(product, out var c) ? c : 0m,
                    VariableRatio = ratio,
                    HasCostBasis  = productRatio != null,
                });
            }

            return new ContributionMarginReport
            {
                MaterialVariable = policy.Material,
                LaborVariable    = policy.Labor,
                OverheadVariable = policy.Overhead,
                Rows = rows.OrderByDescending(r => r.ContributionMargin).ToList(),
            };
        }
    }
}
