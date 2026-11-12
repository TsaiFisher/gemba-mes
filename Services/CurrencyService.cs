using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;

namespace SmartFactory.MES.Services
{
    // ────────────────────────────────────────────────────────────────────────────
    // 匯率類型
    // ────────────────────────────────────────────────────────────────────────────
    public enum RateType
    {
        /// <summary>買入匯率（公司付款給外幣廠商）</summary>
        Buy,
        /// <summary>賣出匯率（公司收款來自外幣客戶）</summary>
        Sell,
        /// <summary>中間匯率（(Buy+Sell)÷2，用於成本/存貨評價）</summary>
        Mid
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 匯率快照（儲存至單據時使用）
    // ────────────────────────────────────────────────────────────────────────────
    public record CurrencyRateSnapshot(
        string CurrencyCode,
        DateOnly RateDate,
        decimal Rate,
        RateType RateType);

    // ────────────────────────────────────────────────────────────────────────────
    // 介面
    // ────────────────────────────────────────────────────────────────────────────
    public interface ICurrencyService
    {
        // ── 基本查詢 ──────────────────────────────────────────────────────────

        /// <summary>取得本幣代號（來自 SystemParam，快取 10 分鐘）</summary>
        Task<string?> GetBaseCurrencyAsync();

        /// <summary>
        /// 取得幣別設定（含 DecimalPlaces、Symbol，快取 10 分鐘）。
        /// 含已停用幣別——停用只該擋新單據挑選，不該讓既有單據的記數方式跟著改變。
        /// </summary>
        Task<Currency?> GetCurrencyAsync(string currencyCode);

        /// <summary>取得所有啟用中幣別（供下拉選單，快取 10 分鐘）</summary>
        Task<List<Currency>> GetActiveCurrenciesAsync();

        /// <summary>取得所有幣別的小數位數對照（含停用；CurrencyCode → DecimalPlaces，快取 10 分鐘）</summary>
        Task<IReadOnlyDictionary<string, int>> GetAllDecimalPlacesAsync(CancellationToken ct = default);

        /// <summary>清除幣別與小數位數快取（修改幣別後呼叫）</summary>
        void InvalidateCache();

        // ── 匯率取用 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 取得指定幣別在單據日期當日或之前最近一筆的指定類型匯率。
        /// • 幣別 = 本幣 → 直接回傳 1。
        /// • 找不到匯率 → 回傳 null（呼叫端應驗證並告知使用者）。
        /// • Rate = 0 → 視同找不到，回傳 null。
        /// • <see cref="RateType.Mid"/> 需買賣兩邊都有值；只維護單邊時回 null 而非取半。
        /// 匯率方向：1 外幣 = Rate 本幣（如 USD=32 表 1 USD = 32 本幣）；本幣 = 外幣 × Rate
        /// </summary>
        Task<decimal?> GetRateAsync(
            string currencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy);

        /// <summary>
        /// 取得實際使用的匯率日期（用於顯示「使用 YYYY-MM-DD 匯率」提示）。
        /// 幣別 = 本幣 → 回傳 documentDate。找不到 → 回傳 null。
        /// </summary>
        Task<DateOnly?> GetRateDateAsync(string currencyCode, DateOnly documentDate);

        /// <summary>
        /// 取得完整匯率快照（含日期、類型）。
        /// 適合在儲存單據時一次取得所有需要的資訊。
        /// </summary>
        Task<CurrencyRateSnapshot?> GetSnapshotAsync(
            string currencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy);

        // ── 金額換算 ─────────────────────────────────────────────────────────

        /// <summary>本幣金額 → 外幣金額（依 Currency.DecimalPlaces 四捨五入）</summary>
        Task<decimal?> ToForeignAsync(
            decimal baseAmount,
            string foreignCurrencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy);

        /// <summary>外幣金額 → 本幣金額（依本幣 DecimalPlaces 四捨五入）</summary>
        Task<decimal?> ToBaseAsync(
            decimal foreignAmount,
            string foreignCurrencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy);

        // ── 顯示格式 ─────────────────────────────────────────────────────────

        /// <summary>依幣別小數位數四捨五入（MidpointRounding.AwayFromZero）</summary>
        Task<decimal> RoundAsync(decimal amount, string currencyCode);

        /// <summary>
        /// 格式化顯示：加符號、千分位、依小數位數。
        /// 例：NT$ 1,234.56 / $ 3.20 / ¥ 370
        /// </summary>
        Task<string> FormatAsync(decimal amount, string currencyCode);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 實作
    // ────────────────────────────────────────────────────────────────────────────
    public class CurrencyService : ICurrencyService
    {
        private readonly ApplicationDbContext _db;
        private readonly ISystemParamService  _sysParam;
        private readonly IMemoryCache         _cache;

        private static readonly TimeSpan CurrencyCacheDuration = TimeSpan.FromMinutes(10);
        private const string CurrencyListCacheKey = "CurrencyList";
        private const string CurrencyDpCacheKey   = "Currency:AllDecimalPlaces";

        public CurrencyService(
            ApplicationDbContext db,
            ISystemParamService sysParam,
            IMemoryCache cache)
        {
            _db       = db;
            _sysParam = sysParam;
            _cache    = cache;
        }

        // ── 基本查詢 ──────────────────────────────────────────────────────────

        public Task<string?> GetBaseCurrencyAsync() =>
            _sysParam.GetAsync(ProjectZSystemParamKeys.BaseCurrency);

        // 取幣別設定（小數位數、符號）刻意不濾 IsActive：那是「這個幣別怎麼記數」的性質，
        // 不是「還能不能挑選」。停用日圓之後，既有日圓單據的金額會從 dp=0 變成 dp=2 的
        // 回退值，整批外幣帳的重算結果位移，而畫面上幣別還是 JPY。
        // IsActive 只擋在 GetActiveCurrenciesAsync（下拉選單）那一層。
        public async Task<Currency?> GetCurrencyAsync(string currencyCode)
        {
            var list = await GetAllCurrenciesAsync();
            return list.FirstOrDefault(c => c.CurrencyCode == currencyCode);
        }

        public async Task<List<Currency>> GetActiveCurrenciesAsync()
        {
            var all = await GetAllCurrenciesAsync();
            return all.Where(c => c.IsActive).ToList();
        }

        private async Task<List<Currency>> GetAllCurrenciesAsync()
        {
            if (_cache.TryGetValue(CurrencyListCacheKey, out List<Currency>? cached) && cached != null)
                return cached;

            var list = await _db.Currencies
                .AsNoTracking()
                .OrderBy(c => c.CurrencyCode)
                .ToListAsync();

            _cache.Set(CurrencyListCacheKey, list, CurrencyCacheDuration);
            return list;
        }

        public async Task<IReadOnlyDictionary<string, int>> GetAllDecimalPlacesAsync(CancellationToken ct = default)
        {
            if (_cache.TryGetValue(CurrencyDpCacheKey, out IReadOnlyDictionary<string, int>? cached) && cached != null)
                return cached;

            // 同 GetCurrencyAsync：小數位數不隨停用而失效
            var map = await _db.Currencies
                .AsNoTracking()
                .Select(c => new { c.CurrencyCode, c.DecimalPlaces })
                .ToDictionaryAsync(c => c.CurrencyCode, c => c.DecimalPlaces, ct);

            _cache.Set(CurrencyDpCacheKey, (IReadOnlyDictionary<string, int>)map, CurrencyCacheDuration);
            return map;
        }

        public void InvalidateCache()
        {
            _cache.Remove(CurrencyListCacheKey);
            _cache.Remove(CurrencyDpCacheKey);
        }

        // ── 匯率取用 ─────────────────────────────────────────────────────────

        public async Task<decimal?> GetRateAsync(
            string currencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy)
        {
            var baseCurrency = await GetBaseCurrencyAsync();
            if (!string.IsNullOrEmpty(baseCurrency) &&
                string.Equals(currencyCode, baseCurrency, StringComparison.OrdinalIgnoreCase))
                return 1m;

            var rateRecord = await FindRateRecordAsync(currencyCode, documentDate);
            if (rateRecord == null) return null;

            decimal? rate = rateType switch
            {
                RateType.Sell => rateRecord.SellRate,
                // 中間價必須兩邊都有值。只維護買匯時 (Buy + 0) / 2 會得到「買匯的一半」，
                // 而那個值 > 0，會通過下面那道守門被當成合法匯率一路用下去——
                // 成本與存貨評價（規格指定用 Mid）直接少一半，且沒有任何地方會報錯。
                // 缺一邊時回 null，與「查無匯率」同一條路徑，由呼叫端擋下單據。
                RateType.Mid  => rateRecord.BuyRate > 0 && rateRecord.SellRate > 0
                    ? (rateRecord.BuyRate + rateRecord.SellRate) / 2m
                    : null,
                _             => rateRecord.BuyRate
            };

            return rate > 0 ? rate : null;
        }

        public async Task<DateOnly?> GetRateDateAsync(string currencyCode, DateOnly documentDate)
        {
            var baseCurrency = await GetBaseCurrencyAsync();
            if (!string.IsNullOrEmpty(baseCurrency) &&
                string.Equals(currencyCode, baseCurrency, StringComparison.OrdinalIgnoreCase))
                return documentDate;

            var record = await FindRateRecordAsync(currencyCode, documentDate);
            return record?.RateDate;
        }

        public async Task<CurrencyRateSnapshot?> GetSnapshotAsync(
            string currencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy)
        {
            var rate = await GetRateAsync(currencyCode, documentDate, rateType);
            if (rate == null) return null;

            var rateDate = await GetRateDateAsync(currencyCode, documentDate) ?? documentDate;
            return new CurrencyRateSnapshot(currencyCode, rateDate, rate.Value, rateType);
        }

        // ── 金額換算 ─────────────────────────────────────────────────────────

        public async Task<decimal?> ToForeignAsync(
            decimal baseAmount,
            string foreignCurrencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy)
        {
            var rate = await GetRateAsync(foreignCurrencyCode, documentDate, rateType);
            if (rate == null || rate.Value == 0) return null;

            // 匯率方向：1 外幣 = Rate 本幣（如 USD=32）；外幣 = 本幣 ÷ Rate
            var result = baseAmount / rate.Value;
            return await RoundAsync(result, foreignCurrencyCode);
        }

        public async Task<decimal?> ToBaseAsync(
            decimal foreignAmount,
            string foreignCurrencyCode,
            DateOnly documentDate,
            RateType rateType = RateType.Buy)
        {
            var rate = await GetRateAsync(foreignCurrencyCode, documentDate, rateType);
            if (rate == null) return null;

            // 匯率方向：1 外幣 = Rate 本幣（如 USD=32）；本幣 = 外幣 × Rate
            var result = foreignAmount * rate.Value;
            var baseCurrency = await GetBaseCurrencyAsync();
            if (string.IsNullOrEmpty(baseCurrency)) return Math.Round(result, 2, MidpointRounding.AwayFromZero);

            return await RoundAsync(result, baseCurrency);
        }

        // ── 顯示格式 ─────────────────────────────────────────────────────────

        public async Task<decimal> RoundAsync(decimal amount, string currencyCode)
        {
            var dp = await ResolveDpAsync(currencyCode);
            return Math.Round(amount, dp, MidpointRounding.AwayFromZero);
        }

        // Clamp 是給既有非法資料的防呆：[Range] 上線前存進去的負數會讓 Math.Round 直接丟
        // ArgumentOutOfRangeException，而它是在存檔路徑深處被呼叫的，等於整張單據 500。
        private async Task<int> ResolveDpAsync(string currencyCode)
        {
            var currency = await GetCurrencyAsync(currencyCode);
            return currency == null ? 2 : DecimalPrecision.Clamp(currency.DecimalPlaces);
        }

        public async Task<string> FormatAsync(decimal amount, string currencyCode)
        {
            var currency = await GetCurrencyAsync(currencyCode);
            var dp     = await ResolveDpAsync(currencyCode);
            var symbol = currency?.Symbol ?? currencyCode;
            var rounded = Math.Round(amount, dp, MidpointRounding.AwayFromZero);
            var fmt    = dp == 0 ? "N0" : $"N{dp}";
            return $"{symbol} {rounded.ToString(fmt)}";
        }

        // ── 私有輔助 ─────────────────────────────────────────────────────────

        private Task<CurrencyRate?> FindRateRecordAsync(string currencyCode, DateOnly documentDate) =>
            _db.CurrencyRates
               .AsNoTracking()
               .Where(r => r.CurrencyCode == currencyCode && r.RateDate <= documentDate)
               .OrderByDescending(r => r.RateDate)
               .FirstOrDefaultAsync();
    }
}
