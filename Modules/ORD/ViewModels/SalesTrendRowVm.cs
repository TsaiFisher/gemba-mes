namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class SalesTrendRowVm
{
    public int     Year                    { get; set; }
    public int     Month                   { get; set; }
    public string  YearMonth               => $"{Year}-{Month:D2}";
    public decimal Amount                  { get; set; }
    public decimal? PrevMonthAmount        { get; set; }
    public decimal? MomPct                 { get; set; }
    public decimal? SameMonthLastYearAmount { get; set; }
    public decimal? YoyPct                 { get; set; }
}
