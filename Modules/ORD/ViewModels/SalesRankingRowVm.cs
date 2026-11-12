namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class SalesRankingRowVm
{
    public int    Rank         { get; set; }
    public string GroupKey     { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public int    OrderCount   { get; set; }
    public int    LineCount    { get; set; }
}
