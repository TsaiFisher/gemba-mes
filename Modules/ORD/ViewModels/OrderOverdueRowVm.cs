namespace SmartFactory.MES.Modules.ORD.ViewModels;

public class OrderOverdueRowVm
{
    public string  SalesOrderId { get; init; } = "";
    public string  CompanyCode  { get; init; } = "";
    public DateOnly OrderDate   { get; init; }
    public string  ProductCode  { get; init; } = "";
    public int     LineNo       { get; init; }
    public DateOnly DeliveryDate { get; init; }
    public decimal OrderQty    { get; init; }
    public decimal ShippedQty  { get; init; }
    public decimal PendingQty  => OrderQty - ShippedQty;
    public int     OverdueDays { get; init; }
}
