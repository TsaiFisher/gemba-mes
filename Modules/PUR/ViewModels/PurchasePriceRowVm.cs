namespace SmartFactory.MES.Modules.PUR.ViewModels;

public class PurchasePriceRowVm
{
    public string ProductCode { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public DateOnly ReceiptDate { get; set; }
    public string GoodsReceiptId { get; set; } = string.Empty;
    public decimal ActualUnitPrice { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? StandardUnitPrice { get; set; }
    public decimal? PriceDiff => StandardUnitPrice.HasValue ? ActualUnitPrice - StandardUnitPrice.Value : null;
    public decimal? PriceDiffPct => StandardUnitPrice.HasValue && StandardUnitPrice.Value != 0
        ? Math.Round((ActualUnitPrice - StandardUnitPrice.Value) / StandardUnitPrice.Value * 100, 1)
        : null;
}
