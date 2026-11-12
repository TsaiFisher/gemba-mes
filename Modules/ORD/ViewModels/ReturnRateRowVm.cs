namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class ReturnRateRowVm
{
    public string  ProductCode  { get; set; } = "";
    public string  CompanyCode  { get; set; } = "";
    public decimal ShippedQty   { get; set; }
    public decimal ReturnedQty  { get; set; }
    public decimal ReturnRate   => ShippedQty > 0 ? Math.Round(ReturnedQty / ShippedQty * 100, 2) : 0;
}
