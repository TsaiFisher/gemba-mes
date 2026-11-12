using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 把「倉庫／儲位預設值」收斂成目標廠別真正可用的值；不可用時一律退回「未設定」。
    ///
    /// <para><b>為什麼需要</b>：<see cref="Warehouse"/> 的主鍵是 <c>WhCode</c> 單欄（全域唯一）
    /// 且實作 <c>IPlantEntity</c>，所以一個倉庫代碼**只屬於一個廠**。而
    /// <c>mfg_fg_warehouse</c>／<c>mfg_default_staging_wh</c> 這類系統參數是全域單值——
    /// 在設定它的那個廠以外，該值指向的是**別廠的倉庫**。預設值指到別廠或指到不存在／已停用的
    /// 倉庫都不該被靜默採用：寧可回傳 null 讓使用者自己選，也不要把一個該廠用不了的代碼
    /// 帶進表單或備料單。</para>
    ///
    /// <para>儲位單獨判定：倉庫可用但儲位不可用時，只清掉儲位而保留倉庫。</para>
    ///
    /// <para>見 <c>.claude/specifications/plant-scoped-system-params.md</c> §2 ①／§5 P1。
    /// 本服務是「參數值 → 該廠可用值」的單一語意點，新的呼叫端請一律走這裡，
    /// 不要各自寫一份存在性檢查。</para>
    /// </summary>
    public interface IPlantDefaultLocationResolver
    {
        Task<(string? WhCode, string? LocationCode)> ResolveAsync(
            string? whCode, string? locationCode, string? plantId, CancellationToken ct = default);

        /// <summary>
        /// 某品號的「正常存放處」＝其產品類別的預設倉儲，經本廠可用性驗證後才採用；
        /// 取不到（沒設、缺儲位、或該廠用不了）一律退回呼叫端給的 fallback。
        ///
        /// <para>倉庫與儲位必須成對才算數：儲位是出庫驗證與成本層的實際單位，
        /// 只有倉庫而沒有儲位的預設值形同沒有設定。</para>
        ///
        /// <para>兩個呼叫端刻意共用這一份：完工後沖的**出庫**倉儲，與完工入庫的**入庫**倉儲。
        /// 兩者若各自決定，一旦分倉部署（原料倉／成品倉分開）就會一邊指到成品倉、
        /// 一邊指到原料倉，而後沖與入庫包在同一個交易裡——錯的那一邊會讓整張工單再也無法入庫，
        /// 且錯誤訊息（「可用量不足」）指向完全不相干的地方（人工驗收 W2-F6）。</para>
        /// </summary>
        Task<(string WhCode, string LocationCode)> ResolveForProductAsync(
            string productCode, string? plantId, string fallbackWh, string fallbackLoc,
            CancellationToken ct = default);
    }

    /// <inheritdoc />
    public class PlantDefaultLocationResolver : IPlantDefaultLocationResolver
    {
        private readonly ApplicationDbContext _db;

        public PlantDefaultLocationResolver(ApplicationDbContext db) => _db = db;

        public async Task<(string? WhCode, string? LocationCode)> ResolveAsync(
            string? whCode, string? locationCode, string? plantId, CancellationToken ct = default)
        {
            var wh  = whCode?.Trim();
            var loc = locationCode?.Trim();

            // 沒有倉庫就沒有儲位可談——儲位代碼只在倉庫底下唯一。
            if (string.IsNullOrEmpty(wh)) return (null, null);

            // 明確帶 plantId 比對並略過全域過濾：本方法也可能被無 HTTP 上下文的路徑呼叫，
            // 此時 CurrentPlantId 為 null，而廠別過濾是 `PlantId == CurrentPlantId`（不是放行），
            // 靠環境過濾會一筆都撈不到，於是每個預設值都被判成不可用。
            var whOk = await _db.Set<Warehouse>().IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(w => w.WhCode == wh && w.PlantId == plantId && w.IsActive, ct);
            if (!whOk) return (null, null);

            if (string.IsNullOrEmpty(loc)) return (wh, null);

            // 儲位不帶 PlantId（廠別由所屬倉庫決定），倉庫既已驗過廠別，比對倉庫即可。
            var locOk = await _db.Set<StorageLocation>().IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(l => l.WhCode == wh && l.LocationCode == loc && l.IsActive, ct);

            return (wh, locOk ? loc : null);
        }

        /// <inheritdoc />
        public async Task<(string WhCode, string LocationCode)> ResolveForProductAsync(
            string productCode, string? plantId, string fallbackWh, string fallbackLoc,
            CancellationToken ct = default)
        {
            var def = await (from p in _db.Products.IgnoreQueryFilters().AsNoTracking()
                             join c in _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
                                 on p.CategoryCode equals c.CategoryCode
                             where p.ProductCode == productCode
                             select new { c.DefaultWarehouseCode, c.DefaultLocationCode })
                            .FirstOrDefaultAsync(ct);
            if (def == null) return (fallbackWh, fallbackLoc);

            var (wh, loc) = await ResolveAsync(def.DefaultWarehouseCode, def.DefaultLocationCode, plantId, ct);

            return string.IsNullOrEmpty(wh) || string.IsNullOrEmpty(loc)
                ? (fallbackWh, fallbackLoc)
                : (wh, loc);
        }
    }
}
