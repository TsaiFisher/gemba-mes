namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// OEE（設備綜合效率）計算（純函式，便於單元測試）。
    /// OEE = 可用率(Availability) × 表現性(Performance) × 良率(Quality)。
    ///   可用率 = 實際稼動時間 / 計畫生產時間（WorkCalendar 正班+加班）
    ///   表現性 = 理想加工時間(產出×標準單位工時) / 實際稼動時間
    ///   良率   = 良品數 / 總產出數
    /// 各比率回傳 0..1，並上限截頂為 1（資料雜訊致 &gt;100% 時避免失真）。
    /// </summary>
    public static class OeeCalculator
    {
        public readonly record struct OeeResult(
            decimal Availability, decimal Performance, decimal Quality, decimal Oee);

        public static decimal Availability(decimal plannedMinutes, decimal runMinutes)
            => plannedMinutes <= 0m ? 0m : Round4(Clamp(runMinutes / plannedMinutes));

        public static decimal Performance(decimal runMinutes, decimal idealMinutes)
            => runMinutes <= 0m ? 0m : Round4(Clamp(idealMinutes / runMinutes));

        public static decimal Quality(decimal totalQty, decimal goodQty)
            => totalQty <= 0m ? 0m : Round4(Clamp(goodQty / totalQty));

        public static OeeResult Compute(
            decimal plannedMinutes, decimal runMinutes, decimal idealMinutes,
            decimal totalQty, decimal goodQty)
        {
            var a = Availability(plannedMinutes, runMinutes);
            var p = Performance(runMinutes, idealMinutes);
            var q = Quality(totalQty, goodQty);
            return new OeeResult(a, p, q, Round4(a * p * q));
        }

        private static decimal Clamp(decimal v) => v < 0m ? 0m : v > 1m ? 1m : v;
        private static decimal Round4(decimal v) => System.Math.Round(v, 4, System.MidpointRounding.AwayFromZero);
    }
}
