namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>
    /// SPC（統計製程管制）計量值統計計算（純函式，便於單元測試）。
    /// 由檢驗量測值（InspectionItem.MeasuredValue）與規格上下限計算製程能力指標
    /// （Cp / Cpk）、管制界限（UCL/LCL = 平均 ± 3σ）與超規次數。
    /// σ 採樣本標準差（n−1）；樣本數 &lt; 2 時 σ=0、能力指標不可計算（回傳 null）。
    /// </summary>
    public static class SpcCalculator
    {
        public readonly record struct SpcStats(
            int Count,
            decimal Mean,
            decimal StdDev,
            decimal Min,
            decimal Max,
            decimal Ucl,
            decimal Lcl,
            decimal? Cp,
            decimal? Cpk,
            int OutOfSpecCount);

        /// <param name="values">量測值序列（已濾除 null）。</param>
        /// <param name="lsl">規格下限（null = 無下限，僅單邊）。</param>
        /// <param name="usl">規格上限（null = 無上限，僅單邊）。</param>
        public static SpcStats Compute(
            IReadOnlyList<decimal> values, decimal? lsl, decimal? usl)
        {
            if (values == null || values.Count == 0)
                return new SpcStats(0, 0, 0, 0, 0, 0, 0, null, null, 0);

            int n = values.Count;
            decimal sum = 0m;
            foreach (var v in values) sum += v;
            decimal mean = sum / n;

            decimal min = values[0], max = values[0];
            foreach (var v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            // 樣本標準差（n−1）；以 double 開根號後轉回 decimal
            decimal sigma = 0m;
            if (n >= 2)
            {
                decimal ss = 0m;
                foreach (var v in values) { var d = v - mean; ss += d * d; }
                double variance = (double)ss / (n - 1);
                sigma = (decimal)Math.Sqrt(variance);
            }

            decimal ucl = mean + 3m * sigma;
            decimal lcl = mean - 3m * sigma;

            decimal? cp = null, cpk = null;
            if (sigma > 0m)
            {
                if (lsl.HasValue && usl.HasValue)
                {
                    cp = (usl.Value - lsl.Value) / (6m * sigma);
                    var cpu = (usl.Value - mean) / (3m * sigma);
                    var cpl = (mean - lsl.Value) / (3m * sigma);
                    cpk = Math.Min(cpu, cpl);
                }
                else if (usl.HasValue)
                {
                    cpk = (usl.Value - mean) / (3m * sigma);   // 僅上限：單邊 Cpu
                }
                else if (lsl.HasValue)
                {
                    cpk = (mean - lsl.Value) / (3m * sigma);   // 僅下限：單邊 Cpl
                }
            }

            int oos = 0;
            foreach (var v in values)
            {
                if ((lsl.HasValue && v < lsl.Value) || (usl.HasValue && v > usl.Value))
                    oos++;
            }

            return new SpcStats(
                n,
                Round4(mean), Round4(sigma), Round4(min), Round4(max),
                Round4(ucl), Round4(lcl),
                cp.HasValue ? Round4(cp.Value) : null,
                cpk.HasValue ? Round4(cpk.Value) : null,
                oos);
        }

        private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    }
}
