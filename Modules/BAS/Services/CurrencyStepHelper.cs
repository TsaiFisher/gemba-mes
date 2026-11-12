namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 依幣別小數位數產生 number input 的 step / 顯示格式字串。
    /// 與 UomStepHelper 對稱：step 一律回 "any"（spinner ±1 且允許小數，精度由
    /// 值格式化 GetFormat/GetDp 與伺服端 ICurrencyService.RoundAsync 把關），未指定
    /// 幣別時 GetDp 預設 2 位（最常見）。詳見 consistency-framework.md R9。
    /// dp=2 → format=N2；dp=8 → format=N8。
    /// </summary>
    public static class CurrencyStepHelper
    {
        private const int DefaultDp = 2;

        /// <summary>spinner step 字串：一律 "any"（±1 且允許小數，精度由 GetFormat + 伺服端 round 把關）。</summary>
        public static string GetStep(int decimalPlaces) => "any";

        public static string GetFormat(int decimalPlaces) =>
            decimalPlaces <= 0 ? "N0" : $"N{decimalPlaces}";

        public static int GetDp(IReadOnlyDictionary<string, int>? map, string? currencyCode)
        {
            if (map != null && !string.IsNullOrEmpty(currencyCode) &&
                map.TryGetValue(currencyCode, out var dp))
                return dp;
            return DefaultDp;
        }

        /// <summary>查字典版本 step（一律 "any"）。</summary>
        public static string GetStep(IReadOnlyDictionary<string, int>? map, string? currencyCode) => "any";

        public static string GetFormat(IReadOnlyDictionary<string, int>? map, string? currencyCode) =>
            GetFormat(GetDp(map, currencyCode));
    }
}
