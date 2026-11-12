namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class DeliveryRateRowVm
{
    public string  CompanyCode  { get; set; } = "";
    public int     TotalLines   { get; set; }
    public int     OnTimeLines  { get; set; }
    public int     LateLines    { get; set; }
    public decimal OnTimeRate   => TotalLines > 0 ? Math.Round((decimal)OnTimeLines / TotalLines * 100, 2) : 0;
}
