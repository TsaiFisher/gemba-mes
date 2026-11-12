namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// 依 UOM 小數位數產生 number input 的 step / min 值。
    ///
    /// step 一律回 "any"：HTML number input 的 step 同時決定「上下三角形(spinner)增減量」
    /// 與「允許輸入精度」。若回精確小數（如 "0.000001"），spinner 每按一次只 ±0.000001。
    /// 改回 "any" 後 spinner 一律 ±1，且仍接受任意小數輸入；實際精度由 min（最小正值）
    /// 與伺服端 IUomService.RoundQtyAsync 雙重把關。詳見 consistency-framework.md R9。
    /// dp=0 → min=1；dp=4 → min=0.0001。
    /// </summary>
    public static class UomStepHelper
    {
        /// <summary>spinner step 字串：一律 "any"（±1 且允許小數，精度由 min + 伺服端 round 把關）。</summary>
        public static string GetStep(int decimalPlaces) => "any";

        /// <summary>精確最小正值（給 min 用）。dp=0 → "1"；dp=4 → "0.0001"。</summary>
        private static string PrecisionStep(int decimalPlaces)
        {
            if (decimalPlaces <= 0) return "1";
            return "0." + new string('0', decimalPlaces - 1) + "1";
        }

        /// <summary>限制必輸入正值時的 min（= 該 UOM 的最小正值；dp=0 時為 "1"）。</summary>
        public static string GetMinPositive(int decimalPlaces) => PrecisionStep(decimalPlaces);

        /// <summary>查字典版本 step（一律 "any"）。</summary>
        public static string GetStep(IReadOnlyDictionary<string, int>? map, string? uomCode) => "any";


        /// <summary>顯示格式字串。dp=0 → "N0"；dp=3 → "N3"。與 <see cref="CurrencyStepHelper.GetFormat(int)"/> 同一套語意。</summary>
        public static string GetFormat(int decimalPlaces) =>
            decimalPlaces <= 0 ? "N0" : $"N{decimalPlaces}";

        /// <summary>查字典版本的顯示格式。找不到該單位時退回 dp=4（存量欄位是 decimal(18,4)）。</summary>
        /// <remarks>
        /// 這個方法是為了取代散在各 View 裡的 `string QtyFmt(string? uom) => ...` 區域函式——
        /// 同一段邏輯至少被複製了 8 份，而每一份的「找不到時退回幾位」都可能不一樣。
        /// 沒有它的地方就會出現寫死的 ToString("N4")：數量不跟著單位的小數位數走，
        /// 一個以「箱」為單位、小數位數 0 的品項，畫面上會顯示 12.0000 箱。
        /// </remarks>
        public static string GetFormat(IReadOnlyDictionary<string, int>? map, string? uomCode) =>
            GetFormat(GetDp(map, uomCode));

        /// <summary>查字典版本的小數位數；找不到回退 4。</summary>
        public static int GetDp(IReadOnlyDictionary<string, int>? map, string? uomCode)
        {
            if (map != null && !string.IsNullOrEmpty(uomCode) && map.TryGetValue(uomCode, out var dp))
                return dp;
            return 4;
        }
        /// <summary>查字典版本 min（找不到時回退 dp=0）。</summary>
        public static string GetMinPositive(IReadOnlyDictionary<string, int>? map, string? uomCode)
        {
            if (map != null && !string.IsNullOrEmpty(uomCode) && map.TryGetValue(uomCode, out var dp))
                return PrecisionStep(dp);
            return PrecisionStep(0);
        }
    }
}
