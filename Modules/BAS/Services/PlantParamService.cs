using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 廠別參數解析（`.claude/specifications/plant-scoped-system-params.md` §3.2–3.4）。
    ///
    /// <para>解析順序：<b>本廠覆寫 → 全域值 → 程式內建預設</b>。
    /// 前兩層由本服務負責，第三層留給呼叫端——沿用既有規則「未設定（null）等同舊行為」，
    /// 服務不寫預設列，讓「從未設定」與「明確設為 false」可區分。</para>
    ///
    /// <para><b>刻意不提供沒有 plantId 的多載。</b> 這是「背景服務靜默退回全域值」那個失效模式的
    /// 根治法：介面一開始就不給「自動抓當前廠別」的糖，呼叫端必須明示，於是無 HTTP 上下文的
    /// <c>MrpEngine</c>／<c>SchedulingEngine</c>／背景服務不可能寫出會靜默失效的程式碼。
    /// 靠型別擋，不靠紀律擋。請求期的呼叫端從 <c>IUserContext.PlantId</c> 取值即可。</para>
    ///
    /// <para>⚠️ 與 <c>ISystemParamService</c> 同樣的既有規則：<b>不可直接對 DbContext 增刪
    /// <c>PlantParams</c> 列</b>——只有本服務的寫入方法會清快取，繞過去會留下髒快取，
    /// 而且會繞過「有沒有這一列」與「值是不是 null」的區分。</para>
    /// </summary>
    public interface IPlantParamService
    {
        /// <summary>取該廠生效值：本廠覆寫 &gt; 全域值 &gt; null（＝兩層都未設定，由呼叫端套內建預設）。</summary>
        Task<string?> GetAsync(string key, string? plantId, CancellationToken ct = default);

        /// <summary>該廠是否有覆寫（用於設定畫面的三態：有列＝覆寫，無列＝繼承全域）。</summary>
        Task<bool> HasOverrideAsync(string key, string? plantId, CancellationToken ct = default);

        /// <summary>該廠所有覆寫（設定畫面一次載入用）。</summary>
        Task<IReadOnlyDictionary<string, string?>> GetOverridesAsync(string plantId, CancellationToken ct = default);

        /// <summary>寫入該廠覆寫。傳 null 代表「該廠明確設為空值」，與「沒有覆寫」不同——後者用 <see cref="ClearAsync"/>。</summary>
        Task SetAsync(string key, string plantId, string? value, CancellationToken ct = default);

        /// <summary>移除該廠覆寫，回到繼承全域。</summary>
        Task ClearAsync(string key, string plantId, CancellationToken ct = default);
    }

    /// <inheritdoc />
    public class PlantParamService : IPlantParamService
    {
        private readonly ApplicationDbContext _db;
        private readonly ISystemParamService _systemParams;
        private readonly IMemoryCache _cache;

        private const string CacheKey = "PlantParams";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        public PlantParamService(ApplicationDbContext db, ISystemParamService systemParams, IMemoryCache cache)
        {
            _db = db;
            _systemParams = systemParams;
            _cache = cache;
        }

        // 整份覆寫一次查完存一顆快取（不做 per-plant 快取，以免 N 廠 N 份）。
        // 覆寫列的數量本質上是「參數數 × 有客製的廠數」，遠小於任何單據表。
        private async Task<Dictionary<(string Key, string PlantId), string?>> GetAllOverridesAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKey, out Dictionary<(string, string), string?>? cached) && cached != null)
                return cached;

            var rows = await _db.PlantParams.AsNoTracking().ToListAsync(ct);
            var dict = rows.ToDictionary(r => (r.Key, r.PlantId), r => r.Value);

            _cache.Set(CacheKey, dict, CacheDuration);
            return dict;
        }

        public async Task<string?> GetAsync(string key, string? plantId, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(plantId))
            {
                var all = await GetAllOverridesAsync(ct);
                // 有這一列就用它的值——即使值是 null。「該廠明確設為空」是覆寫的一種，
                // 不該被當成沒設定而回頭吃全域值。
                if (all.TryGetValue((key, plantId), out var overridden)) return overridden?.Trim();
            }

            // 讀取端一律去前後空白（2026-10-20 第 2 節）：呼叫端大多直接 string.Equals(v, "true")，
            // 直接改資料庫或匯入留下的 " true" 會被當成 false——開關靜默失效，畫面勾選框卻顯示已勾。
            return (await _systemParams.GetAsync(key))?.Trim();
        }

        public async Task<bool> HasOverrideAsync(string key, string? plantId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(plantId)) return false;
            var all = await GetAllOverridesAsync(ct);
            return all.ContainsKey((key, plantId));
        }

        public async Task<IReadOnlyDictionary<string, string?>> GetOverridesAsync(string plantId, CancellationToken ct = default)
        {
            var all = await GetAllOverridesAsync(ct);
            return all.Where(kv => kv.Key.PlantId == plantId)
                      .ToDictionary(kv => kv.Key.Key, kv => kv.Value);
        }

        public async Task SetAsync(string key, string plantId, string? value, CancellationToken ct = default)
        {
            var existing = await _db.PlantParams
                .FirstOrDefaultAsync(p => p.Key == key && p.PlantId == plantId, ct);

            if (existing == null)
            {
                _db.PlantParams.Add(new PlantParam
                {
                    Key = key, PlantId = plantId, Value = value, UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey);
        }

        public async Task ClearAsync(string key, string plantId, CancellationToken ct = default)
        {
            var existing = await _db.PlantParams
                .FirstOrDefaultAsync(p => p.Key == key && p.PlantId == plantId, ct);
            if (existing == null) return;

            _db.PlantParams.Remove(existing);
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey);
        }
    }
}
