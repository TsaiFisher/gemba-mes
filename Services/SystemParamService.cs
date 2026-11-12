using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services
{
    public interface ISystemParamService
    {
        Task<Dictionary<string, string?>> GetAllAsync();
        Task<string?> GetAsync(string key);
        Task SetAsync(string key, string? value);
        Task SetBatchAsync(Dictionary<string, string?> values);

        /// <summary>公司名稱（含語系 fallback）</summary>
        Task<string> GetCompanyNameAsync(bool isEnglish);

        /// <summary>公司簡稱（無簡稱→公司名稱→SmartFactory）</summary>
        Task<string> GetCompanyShortAsync(bool isEnglish);

        /// <summary>Logo 網址（未設定則回傳 null）</summary>
        Task<string?> GetLogoUrlAsync();
    }

    public class SystemParamService : ISystemParamService
    {
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private const string CacheKey = "SystemParams";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        public SystemParamService(ApplicationDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<Dictionary<string, string?>> GetAllAsync()
        {
            if (_cache.TryGetValue(CacheKey, out Dictionary<string, string?>? cached) && cached != null)
                return cached;

            var dict = await _db.SystemParams
                .AsNoTracking()
                .ToDictionaryAsync(p => p.Key, p => p.Value);

            _cache.Set(CacheKey, dict, CacheDuration);
            return dict;
        }

        public async Task<string?> GetAsync(string key)
        {
            var all = await GetAllAsync();
            return all.TryGetValue(key, out var v) ? v : null;
        }

        public async Task SetAsync(string key, string? value)
        {
            var existing = await _db.SystemParams.FindAsync(key);
            if (existing == null)
                _db.SystemParams.Add(new SystemParam { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            _cache.Remove(CacheKey);
        }

        public async Task SetBatchAsync(Dictionary<string, string?> values)
        {
            var keys = values.Keys.ToList();
            var existing = await _db.SystemParams
                .Where(p => keys.Contains(p.Key))
                .ToDictionaryAsync(p => p.Key);

            foreach (var (key, value) in values)
            {
                if (existing.TryGetValue(key, out var record))
                {
                    record.Value = value;
                    record.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.SystemParams.Add(new SystemParam { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
                }
            }
            await _db.SaveChangesAsync();
            _cache.Remove(CacheKey);
        }

        public async Task<string> GetCompanyNameAsync(bool isEnglish)
        {
            var all = await GetAllAsync();
            if (isEnglish)
            {
                if (all.TryGetValue(SystemParamKeys.CompanyNameEn, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
                if (all.TryGetValue(SystemParamKeys.CompanyNameZh, out var zh) && !string.IsNullOrWhiteSpace(zh)) return zh;
            }
            else
            {
                if (all.TryGetValue(SystemParamKeys.CompanyNameZh, out var zh) && !string.IsNullOrWhiteSpace(zh)) return zh;
                if (all.TryGetValue(SystemParamKeys.CompanyNameEn, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
            }
            return "SmartFactory";
        }

        public async Task<string> GetCompanyShortAsync(bool isEnglish)
        {
            var all = await GetAllAsync();
            if (isEnglish)
            {
                if (all.TryGetValue(SystemParamKeys.CompanyShortEn, out var sen) && !string.IsNullOrWhiteSpace(sen)) return sen;
                if (all.TryGetValue(SystemParamKeys.CompanyShortZh, out var szh) && !string.IsNullOrWhiteSpace(szh)) return szh;
                if (all.TryGetValue(SystemParamKeys.CompanyNameEn, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
                if (all.TryGetValue(SystemParamKeys.CompanyNameZh, out var zh) && !string.IsNullOrWhiteSpace(zh)) return zh;
            }
            else
            {
                if (all.TryGetValue(SystemParamKeys.CompanyShortZh, out var szh) && !string.IsNullOrWhiteSpace(szh)) return szh;
                if (all.TryGetValue(SystemParamKeys.CompanyShortEn, out var sen) && !string.IsNullOrWhiteSpace(sen)) return sen;
                if (all.TryGetValue(SystemParamKeys.CompanyNameZh, out var zh) && !string.IsNullOrWhiteSpace(zh)) return zh;
                if (all.TryGetValue(SystemParamKeys.CompanyNameEn, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
            }
            return "SmartFactory";
        }

        public async Task<string?> GetLogoUrlAsync()
        {
            var path = await GetAsync(SystemParamKeys.CompanyLogo);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }
}
