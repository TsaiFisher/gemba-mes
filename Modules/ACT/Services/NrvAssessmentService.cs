using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.ACT.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.FIN.Models;

namespace SmartFactory.MES.Modules.ACT.Services
{
    public class NrvAssessmentService : INrvAssessmentService
    {
        private readonly ApplicationDbContext _db;
        private readonly IUomService _uom;
        private readonly SmartFactory.MES.Services.IDateTimeService _dateTime;
        private readonly SmartFactory.MES.Services.ISystemParamService? _sysParams;

        public NrvAssessmentService(ApplicationDbContext db, IUomService uom,
            SmartFactory.MES.Services.IDateTimeService dateTime,
            SmartFactory.MES.Services.ISystemParamService? sysParams = null)
        {
            _db  = db;
            _uom = uom;
            _dateTime = dateTime;
            _sysParams = sysParams;
        }

        public async Task<IList<InventoryNrvAssessment>> RunAssessmentAsync(
            string periodId, string plantId,
            decimal sellingCostRatio = 0.02m,
            CancellationToken ct = default)
        {
            // 刪除本期間舊評估
            var old = await _db.InventoryNrvAssessments
                .Where(a => a.PeriodId == periodId && a.PlantId == plantId)
                .ToListAsync(ct);
            _db.InventoryNrvAssessments.RemoveRange(old);

            // 期末當時的在庫數量與帳面價值（2026-10-24 月結序列後重寫），逐品號：
            //  ① 帳面成本依計價方式取：均價料＝在手量 × 均價；FIFO 料＝成本層剩餘價值。
            //     原本一律用 Inventory.UnitCost——FIFO 料該欄恆為 0，帳面成本 0，永遠不會提列跌價。
            //  ② 排除客供倉：客戶的料不計價（單價 0），混進來會把平均帳面成本拉低、少提跌價。
            //  ③ 倒推到期末：減去期末日之後的異動（數量與價值）。原本取現在的庫存，
            //     反關帳重做上一期時會拿今天的庫存去評上一期。
            // 找不到期間（舊呼叫端）以當地營業日為準。
            var periodEnd = await _db.AccountingPeriods.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.PeriodId == periodId && p.PlantId == plantId)
                .Select(p => (DateOnly?)p.EndDate).FirstOrDefaultAsync(ct);
            var asOf = periodEnd ?? _dateTime.GetLocalDate();

            var csLocations = await _db.StorageLocations.IgnoreQueryFilters().AsNoTracking()
                .Join(_db.Warehouses.IgnoreQueryFilters().AsNoTracking()
                        .Where(w => w.WarehouseType == Modules.BAS.Models.WarehouseType.CustomerSupplied),
                    l => l.WhCode, w => w.WhCode, (l, w) => l.LocationCode)
                .ToListAsync(ct);
            var globalFifo = string.Equals(_sysParams == null ? null
                    : await _sysParams.GetAsync(SmartFactory.MES.Models.ProjectZSystemParamKeys.InvDefaultCostMethod),
                "FIFO", StringComparison.OrdinalIgnoreCase);
            var methods = await _db.Products.IgnoreQueryFilters().AsNoTracking()
                .Select(p => new { p.ProductCode, Method = p.Category == null ? Modules.BAS.Models.CostMethod.SystemDefault : p.Category.CostMethod })
                .ToDictionaryAsync(p => p.ProductCode, p => p.Method, ct);
            bool IsFifo(string product)
            {
                var m = methods.GetValueOrDefault(product, Modules.BAS.Models.CostMethod.SystemDefault);
                return m == Modules.BAS.Models.CostMethod.Fifo || (m == Modules.BAS.Models.CostMethod.SystemDefault && globalFifo);
            }

            var onHand = await _db.Inventories.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.PlantId == plantId && !csLocations.Contains(i.LocationCode))
                .Select(i => new { i.ProductCode, i.QtyOnHand, i.UnitCost })
                .ToListAsync(ct);
            var layerValue = await _db.InventoryCostLayers.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.PlantId == plantId && !csLocations.Contains(l.LocationCode))
                .GroupBy(l => l.ProductCode)
                .Select(g => new { ProductCode = g.Key, Value = g.Sum(l => l.QtyRemaining * l.UnitCost) })
                .ToDictionaryAsync(x => x.ProductCode, x => x.Value, ct);
            var after = await _db.StockMovements.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.PlantId == plantId && m.MovementDate > asOf && !csLocations.Contains(m.LocationCode))
                .GroupBy(m => m.ProductCode)
                .Select(g => new
                {
                    ProductCode = g.Key,
                    Qty   = g.Sum(m => m.Direction * m.StockQty),
                    Value = g.Sum(m => m.Direction * m.StockQty * (m.UnitCost ?? 0m)),
                })
                .ToDictionaryAsync(x => x.ProductCode, x => (x.Qty, x.Value), ct);

            var products = onHand.Select(i => i.ProductCode).Concat(after.Keys).Distinct().ToList();
            var inventories = products
                .Select(code =>
                {
                    var rows = onHand.Where(i => i.ProductCode == code).ToList();
                    var qtyNow = rows.Sum(i => i.QtyOnHand);
                    var valueNow = IsFifo(code) ? layerValue.GetValueOrDefault(code) : rows.Sum(i => i.QtyOnHand * i.UnitCost);
                    var (aq, av) = after.GetValueOrDefault(code);
                    var qty = qtyNow - aq;
                    var value = valueNow - av;
                    return new { ProductCode = code, TotalQty = qty, UnitCost = qty > 0m ? Math.Round(value / qty, 6, MidpointRounding.AwayFromZero) : 0m };
                })
                .Where(i => i.TotalQty > 0m)
                .ToList();

            // 近 90 天售價：以**本幣、每庫存單位**的加權平均（Σ本幣金額 ÷ Σ庫存數量）。
            // 原本是 ARInvoiceLine.UnitPrice 的簡單平均——交易幣（美金單價直接與台幣成本比）、
            // 銷售單位（打與個混在一起）、不加權（1 個的特價單與 1000 個的大單同權重）。
            // 取樣窗是「期末往前 90 天」：原本是今天往前 90 天，補跑過去期間會用到錯的售價。
            var cutoff = asOf.AddDays(-90);
            var saleLines = await _db.ARInvoices
                .Where(i => i.PlantId == plantId && i.InvoiceDate >= cutoff && i.InvoiceDate <= asOf && i.Status != ARInvoiceStatus.Void)
                .SelectMany(i => i.Lines.Select(l => new
                {
                    l.ProductCode, l.UomCode, l.Qty, l.LineAmount, i.ExchangeRate,
                }))
                .Where(l => l.ProductCode != null && l.Qty > 0)
                .AsNoTracking()
                .ToListAsync(ct);

            var codes = saleLines.Select(l => l.ProductCode!).Distinct().ToList();
            var stockUoms = await _db.Products.AsNoTracking()
                .Where(p => codes.Contains(p.ProductCode))
                .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom, ct);

            var sums = new Dictionary<string, (decimal Amount, decimal Qty)>();
            foreach (var l in saleLines)
            {
                var code     = l.ProductCode!;
                var stockUom = stockUoms.GetValueOrDefault(code);
                // 缺換算會拋 UomConversionNotFoundException（訊息含品號與兩個單位），
                // 由呼叫端回到畫面——跳過只會讓那個品號靜默地不評估。
                var stockQty = string.IsNullOrEmpty(l.UomCode) || string.IsNullOrEmpty(stockUom) || l.UomCode == stockUom
                    ? l.Qty
                    : await _uom.ConvertAsync(code, l.UomCode, stockUom, l.Qty, ct);
                var (amt, qty) = sums.GetValueOrDefault(code);
                sums[code] = (amt + l.LineAmount * l.ExchangeRate, qty + stockQty);
            }
            var avgSalePrices = sums
                .Where(kv => kv.Value.Qty > 0)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value.Amount / kv.Value.Qty, 6, MidpointRounding.AwayFromZero));

            var results = new List<InventoryNrvAssessment>();

            foreach (var inv in inventories)
            {
                if (!avgSalePrices.TryGetValue(inv.ProductCode, out var nrvPrice))
                    continue;  // 近 90 天無銷售記錄，跳過

                var sellingCost = Math.Round(nrvPrice * sellingCostRatio, 4);
                var nrv         = nrvPrice - sellingCost;
                var impairment  = Math.Max(0, (inv.UnitCost - nrv) * inv.TotalQty);

                var row = new InventoryNrvAssessment
                {
                    PeriodId           = periodId,
                    ProductCode        = inv.ProductCode,
                    CarryingCost       = inv.UnitCost,
                    QtyOnHand          = inv.TotalQty,
                    NrvPrice           = nrvPrice,
                    SellingCost        = sellingCost,
                    NetRealizableValue = nrv,
                    ImpairmentLoss     = impairment,
                    AssessedAt         = DateTime.UtcNow,
                    PlantId            = plantId,
                };
                results.Add(row);
                _db.InventoryNrvAssessments.Add(row);
            }

            await _db.SaveChangesAsync(ct);
            return results;
        }
    }
}
