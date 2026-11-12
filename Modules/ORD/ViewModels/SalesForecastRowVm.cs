namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class SalesForecastRowVm
{
    public string ProductCode { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public string YearMonth => $"{Year}-{Month:D2}";
    public decimal ActualAmount { get; set; }
    public decimal? MovingAvg { get; set; }
    /// <summary>季節係數；銷售史未滿一年為 null（不套係數，預測值＝均值）</summary>
    public decimal? SeasonalIndex { get; set; }
    /// <summary>預測值（只有預測列有值）＝12 月均值 × 季節係數（無係數時 × 1）</summary>
    public decimal? ForecastAmount { get; set; }
    public bool IsForecast { get; set; }
}
