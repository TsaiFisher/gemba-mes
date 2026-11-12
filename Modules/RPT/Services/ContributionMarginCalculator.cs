namespace SmartFactory.MES.Modules.RPT.Services
{
    /// <summary>
    /// 產品邊際貢獻計算（純函式，便於單元測試）。
    /// 邊際貢獻 = 營收 − 變動成本；變動成本 = 銷貨成本(COGS) × 變動成本占比。
    /// 變動占比由工單實際成本要素（材料／人工／製費）依「變動/固定分類」推得：
    ///   占比 = Σ(標記為變動的要素) / Σ(全部要素)
    /// 現行 AVCO 僅提供混合後的 COGS，故以「產品期間平均變動占比」還原變動成本（路線 A）。
    /// </summary>
    public static class ContributionMarginCalculator
    {
        /// <summary>變動成本分類旗標：哪些要素計入變動成本。</summary>
        public readonly record struct VariablePolicy(bool Material, bool Labor, bool Overhead);

        /// <summary>
        /// 由要素金額與分類旗標算變動占比（0..1）。全要素合計 ≤ 0 → 回傳 null（占比未知）。
        /// </summary>
        public static decimal? VariableRatio(
            decimal material, decimal labor, decimal overhead, VariablePolicy policy)
        {
            var total = material + labor + overhead;
            if (total <= 0m) return null;

            var variable = (policy.Material ? material : 0m)
                         + (policy.Labor    ? labor    : 0m)
                         + (policy.Overhead ? overhead : 0m);
            return Round4(variable / total);
        }

        /// <summary>邊際貢獻 = 營收 − COGS × 變動占比。</summary>
        public static decimal Contribution(decimal revenue, decimal cogs, decimal variableRatio)
            => Round4(revenue - cogs * variableRatio);

        /// <summary>邊際貢獻率 = 邊際貢獻 / 營收（%）。營收 ≤ 0 → 0。</summary>
        public static decimal ContributionRate(decimal revenue, decimal contribution)
            => revenue <= 0m ? 0m
             : System.Math.Round(contribution / revenue * 100m, 2, System.MidpointRounding.AwayFromZero);

        private static decimal Round4(decimal v) => System.Math.Round(v, 4, System.MidpointRounding.AwayFromZero);
    }
}
