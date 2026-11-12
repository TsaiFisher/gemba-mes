using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.QCR.Models;

namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>
    /// SPC 製程能力分析服務：撈取指定廠別/期間內、有量測值（MeasuredValue）的檢驗項目
    /// （InspectionItem ⨝ InspectionOrder），依（品號 × 特性名）分組，經 <see cref="SpcCalculator"/>
    /// 計算 Cp/Cpk/超規；當鎖定單一品號時，另組時間序管制圖序列。
    /// 查詢以明確 plantId 過濾（InspectionOrder 帶 PlantId），不依賴 HTTP 環境廠別，便於整合測試。
    /// </summary>
    public class SpcAnalysisService : ISpcAnalysisService
    {
        private readonly ApplicationDbContext _db;

        /// <summary>單一品號管制圖每特性最多取樣點（避免圖表過載）。</summary>
        private const int MaxChartPoints = 300;

        public SpcAnalysisService(ApplicationDbContext db) => _db = db;

        public async Task<SpcAnalysisReport> BuildAsync(
            string plantId, string? productCode, DateOnly from, DateOnly to,
            CancellationToken ct = default)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var toUtc   = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); // 迄日含整日

            // 量測值 + 其檢驗單（時間/品號）。InspectionItem 無 InspectedAt，取自 Order。
            var q = from it in _db.InspectionItems.AsNoTracking()
                    join o in _db.InspectionOrders.AsNoTracking()
                        on it.InspectionOrderId equals o.InspectionOrderId
                    where o.PlantId == plantId
                       && it.MeasuredValue != null
                       && o.InspectedAt != null
                       && o.InspectedAt >= fromUtc && o.InspectedAt < toUtc
                    select new
                    {
                        o.ProductCode,
                        it.ItemName,
                        it.UomCode,
                        it.LowerSpec,
                        it.UpperSpec,
                        it.TargetValue,
                        Measured = it.MeasuredValue!.Value,
                        o.InspectedAt,
                    };

            if (!string.IsNullOrWhiteSpace(productCode))
                q = q.Where(x => x.ProductCode == productCode);

            var raw = await q.ToListAsync(ct);

            var report = new SpcAnalysisReport();
            if (raw.Count == 0) return report;

            // 品名對照
            var productCodes = raw.Select(r => r.ProductCode).Distinct().ToList();
            var productNames = await _db.Products.AsNoTracking()
                .Where(p => productCodes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.ProductName, ct);

            var groups = raw
                .GroupBy(r => new { r.ProductCode, r.ItemName })
                .OrderBy(g => g.Key.ProductCode).ThenBy(g => g.Key.ItemName);

            foreach (var g in groups)
            {
                var ordered = g.OrderBy(x => x.InspectedAt).ToList();
                var values  = ordered.Select(x => x.Measured).ToList();

                // 規格上下限：取該組多數（首個非空）的規格值
                var lsl = ordered.Select(x => x.LowerSpec).FirstOrDefault(v => v.HasValue);
                var usl = ordered.Select(x => x.UpperSpec).FirstOrDefault(v => v.HasValue);
                var tgt = ordered.Select(x => x.TargetValue).FirstOrDefault(v => v.HasValue);
                var uom = ordered.Select(x => x.UomCode).FirstOrDefault(v => !string.IsNullOrEmpty(v));

                var s = SpcCalculator.Compute(values, lsl, usl);

                report.Rows.Add(new SpcCharacteristicRow
                {
                    ProductCode = g.Key.ProductCode,
                    ProductName = productNames.TryGetValue(g.Key.ProductCode, out var pn) ? pn : "",
                    ItemName    = g.Key.ItemName,
                    UomCode     = uom,
                    SampleCount = s.Count,
                    Mean        = s.Mean,
                    StdDev      = s.StdDev,
                    MinValue    = s.Min,
                    MaxValue    = s.Max,
                    LowerSpec   = lsl,
                    TargetValue = tgt,
                    UpperSpec   = usl,
                    Cp          = s.Cp,
                    Cpk         = s.Cpk,
                    OutOfSpecCount = s.OutOfSpecCount,
                });

                // 單一品號 → 附管制圖趨勢
                if (!string.IsNullOrWhiteSpace(productCode))
                {
                    var pts = ordered.TakeLast(MaxChartPoints).ToList();
                    report.Charts.Add(new SpcChartSeries
                    {
                        ItemName    = g.Key.ItemName,
                        UomCode     = uom,
                        Labels      = pts.Select(x => x.InspectedAt!.Value.ToString("MM-dd HH:mm")).ToList(),
                        Values      = pts.Select(x => x.Measured).ToList(),
                        Mean        = s.Mean,
                        Ucl         = s.Ucl,
                        Lcl         = s.Lcl,
                        LowerSpec   = lsl,
                        UpperSpec   = usl,
                        TargetValue = tgt,
                    });
                }
            }

            return report;
        }
    }
}
