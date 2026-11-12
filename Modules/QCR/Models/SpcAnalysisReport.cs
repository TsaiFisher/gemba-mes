using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.QCR.Models
{
    /// <summary>
    /// SPC 製程能力分析報表：依（品號 × 檢驗特性）彙總計量值量測結果，
    /// 提供 Cp / Cpk / 超規率等製程能力指標；選定單一品號時附管制圖趨勢資料。
    /// </summary>
    public class SpcAnalysisReport
    {
        public List<SpcCharacteristicRow> Rows { get; set; } = new();

        /// <summary>管制圖序列（僅在查詢條件鎖定單一品號時填入，供 Chart.js 繪製）。</summary>
        public List<SpcChartSeries> Charts { get; set; } = new();

        [NotMapped]
        public bool HasChart => Charts.Count > 0;
    }

    /// <summary>單一（品號 × 特性）的製程能力摘要列。</summary>
    public class SpcCharacteristicRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ItemName    { get; set; } = string.Empty;
        public string? UomCode    { get; set; }

        public int     SampleCount { get; set; }
        public decimal Mean        { get; set; }
        public decimal StdDev      { get; set; }
        public decimal MinValue    { get; set; }
        public decimal MaxValue    { get; set; }

        public decimal? LowerSpec  { get; set; }
        public decimal? TargetValue { get; set; }
        public decimal? UpperSpec  { get; set; }

        public decimal? Cp  { get; set; }
        public decimal? Cpk { get; set; }

        public int OutOfSpecCount { get; set; }

        [NotMapped]
        public decimal OutOfSpecRate =>
            SampleCount == 0 ? 0m : Math.Round(OutOfSpecCount * 100m / SampleCount, 2, MidpointRounding.AwayFromZero);

        /// <summary>製程能力評級（依 Cpk）：不足 / 尚可 / 良好 / 優異 / N/A。純顯示字串。</summary>
        [NotMapped]
        public string CapabilityLevel =>
            Cpk is null      ? "NA"
            : Cpk >= 1.67m   ? "Excellent"
            : Cpk >= 1.33m   ? "Good"
            : Cpk >= 1.00m   ? "Adequate"
                             : "Insufficient";
    }

    /// <summary>單一特性的管制圖序列（時間序 X̄-individual 圖）。</summary>
    public class SpcChartSeries
    {
        public string ItemName  { get; set; } = string.Empty;
        public string? UomCode  { get; set; }

        /// <summary>X 軸標籤（量測時間，時間序）。</summary>
        public List<string> Labels { get; set; } = new();

        /// <summary>量測值序列。</summary>
        public List<decimal> Values { get; set; } = new();

        public decimal Mean { get; set; }
        public decimal Ucl  { get; set; }
        public decimal Lcl  { get; set; }
        public decimal? LowerSpec  { get; set; }
        public decimal? UpperSpec  { get; set; }
        public decimal? TargetValue { get; set; }
    }
}
