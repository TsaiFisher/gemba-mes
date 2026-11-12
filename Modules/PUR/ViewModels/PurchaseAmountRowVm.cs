namespace SmartFactory.MES.Modules.PUR.ViewModels;

public class PurchaseAmountRowVm
{
    public string GroupKey { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public string YearMonth => $"{Year}-{Month:D2}";
    public decimal TotalAmount { get; set; }
    public int ReceiptCount { get; set; }
}
