using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.BAS.Services
{
    public class UomService : IUomService
    {
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private const string AllDpCacheKey = "Uom:AllDecimalPlaces";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        public UomService(ApplicationDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<decimal> ConvertAsync(string productCode, string fromUom, string toUom, decimal qty, CancellationToken ct = default)
        {
            var rate = await GetConversionRateAsync(productCode, fromUom, toUom, ct);
            return qty * rate;
        }

        public async Task<decimal> GetConversionRateAsync(string productCode, string fromUom, string toUom, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(fromUom) || string.IsNullOrEmpty(toUom))
                return 1.0m;

            if (fromUom.Trim().ToUpper() == toUom.Trim().ToUpper())
                return 1.0m;

            // 查找優先序：品號特定 → 全域 → 例外。最 specific 的先贏，
            // 全域表因此可以只放真正與品號無關的換算（KG↔G），品號差異（一箱幾入）放品號表。
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                // 1. 品號特定・直接換算 (From -> To)
                var pConv = await _db.ProductUomConversions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ProductCode == productCode
                                           && c.FromUomCode == fromUom && c.ToUomCode == toUom, ct);

                if (pConv != null && pConv.Denominator != 0)
                    return pConv.Numerator / pConv.Denominator;

                // 2. 品號特定・逆向換算 (To -> From)
                var pInv = await _db.ProductUomConversions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ProductCode == productCode
                                           && c.FromUomCode == toUom && c.ToUomCode == fromUom, ct);

                if (pInv != null && pInv.Numerator != 0)
                    return pInv.Denominator / pInv.Numerator;
            }

            // 3. 全域・直接換算 (From -> To)
            var conv = await _db.UomConversions
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.FromUomCode == fromUom && c.ToUomCode == toUom, ct);

            if (conv != null && conv.Denominator != 0)
                return conv.Numerator / conv.Denominator;

            // 4. 全域・逆向換算 (To -> From)
            var invConv = await _db.UomConversions
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.FromUomCode == toUom && c.ToUomCode == fromUom, ct);

            if (invConv != null && invConv.Numerator != 0)
                return invConv.Denominator / invConv.Numerator;

            // 5. 兩個單位不同、卻查不到任何換算 —— 這是資料缺漏，不是「比例剛好是 1」。
            //
            // 原本這裡回 1.0。後果是靜默的：收貨 10 BOX 會被當成 10 PC 記進庫存，
            // 帳面數量少一個數量級，畫面上單位還是對的，完全看不出來。
            // 而 test-hardening-plan.md §5-1 把超量守門改建在「庫存基準單位累計」之後，
            // 這個換算就是那道守門的分母——換算錯掉，守門跟著一起錯。
            //
            // 呼叫端（PurchaseService 七處、ORD 訂單／出貨／退貨四處）都在寫入庫存數量，
            // 寧可讓它當場失敗，也不要寫進一筆看不出錯的庫存。
            throw new UomConversionNotFoundException(productCode, fromUom, toUom);
        }

        public async Task<IReadOnlyDictionary<string, int>> GetAllDecimalPlacesAsync(CancellationToken ct = default)
        {
            if (_cache.TryGetValue(AllDpCacheKey, out IReadOnlyDictionary<string, int>? cached) && cached != null)
                return cached;

            // 刻意不濾 IsActive：小數位數是「這個單位怎麼記數」的性質，不是「還能不能挑選」。
            // 濾掉停用單位會讓既有單據的數量在重新存檔時退回 dp=0 被捨成整數，
            // 而畫面上單位還是原來那個，完全看不出庫存少了一截。
            // IsActive 只擋在下拉選單那一層（各 Controller 的 Options 查詢）。
            var map = await _db.Uoms
                .AsNoTracking()
                .Select(u => new { u.UomCode, u.DecimalPlaces })
                .ToDictionaryAsync(u => u.UomCode, u => u.DecimalPlaces, ct);

            _cache.Set(AllDpCacheKey, (IReadOnlyDictionary<string, int>)map, CacheTtl);
            return map;
        }

        public async Task<int> GetDecimalPlacesAsync(string? uomCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uomCode)) return 0;
            var map = await GetAllDecimalPlacesAsync(ct);
            // Clamp 是給既有非法資料的防呆：[Range] 上線前存進去的負數會讓 Math.Round 直接丟
            // ArgumentOutOfRangeException，而它是在存檔路徑深處被呼叫的，等於整張單據 500。
            return map.TryGetValue(uomCode, out var dp) ? DecimalPrecision.Clamp(dp) : 0;
        }

        public async Task<decimal> RoundQtyAsync(string? uomCode, decimal qty, CancellationToken ct = default)
        {
            var dp = await GetDecimalPlacesAsync(uomCode, ct);
            return Math.Round(qty, dp, MidpointRounding.AwayFromZero);
        }

        public async Task<ProductUomOptions?> GetProductUomsAsync(string productCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(productCode)) return null;
            var p = await _db.Products.AsNoTracking()
                .Where(x => x.ProductCode == productCode)
                .Select(x => new { x.StockUom, x.PurchaseUom, x.SalesUom })
                .FirstOrDefaultAsync(ct);
            if (p == null) return null;

            var preferred = new[] { p.StockUom, p.PurchaseUom, p.SalesUom }
                .Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!).ToList();
            if (string.IsNullOrWhiteSpace(p.StockUom))
                return new ProductUomOptions(null, p.PurchaseUom, p.SalesUom, preferred.Distinct().ToList());

            var stock = p.StockUom!;
            // 與 GetConversionRateAsync 同一套範圍：品號特定（雙向）＋全域（雙向），只看「與庫存單位直接相連」的那一層。
            var specific = await _db.ProductUomConversions.AsNoTracking()
                .Where(c => c.ProductCode == productCode && (c.FromUomCode == stock || c.ToUomCode == stock)
                            && c.Numerator != 0 && c.Denominator != 0)
                .Select(c => c.FromUomCode == stock ? c.ToUomCode : c.FromUomCode)
                .ToListAsync(ct);
            var global = await _db.UomConversions.AsNoTracking()
                .Where(c => (c.FromUomCode == stock || c.ToUomCode == stock) && c.Numerator != 0 && c.Denominator != 0)
                .Select(c => c.FromUomCode == stock ? c.ToUomCode : c.FromUomCode)
                .ToListAsync(ct);

            var reachable = new HashSet<string>(specific.Concat(global), StringComparer.Ordinal) { stock };
            // 採購／銷售單位若與庫存單位之間沒有換算，**不列**：列出來就是一張存檔必定失敗的單。
            var ordered = preferred.Where(reachable.Contains)
                .Concat(reachable.OrderBy(u => u, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).ToList();
            return new ProductUomOptions(stock, p.PurchaseUom, p.SalesUom, ordered);
        }

        public async Task<(string StockUom, decimal StockQty)> ToStockQtyAsync(
            string productCode, string? uomCode, decimal qty, CancellationToken ct = default)
        {
            var stockUom = await _db.Products.AsNoTracking()
                .Where(x => x.ProductCode == productCode)
                .Select(x => x.StockUom)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(stockUom))
                return (uomCode ?? "", await RoundQtyAsync(uomCode, qty, ct));

            var stockQty = string.IsNullOrWhiteSpace(uomCode) || string.Equals(uomCode, stockUom, StringComparison.OrdinalIgnoreCase)
                ? qty
                : await ConvertAsync(productCode, uomCode!, stockUom!, qty, ct);
            return (stockUom!, await RoundQtyAsync(stockUom, stockQty, ct));
        }

        public void InvalidateDecimalPlacesCache() => _cache.Remove(AllDpCacheKey);
    }
}
