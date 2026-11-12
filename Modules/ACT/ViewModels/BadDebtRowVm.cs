namespace SmartFactory.MES.Modules.ACT.ViewModels;

public class BadDebtRowVm
{
    public string CompanyCode { get; set; } = string.Empty;
    public string AgingBucket { get; set; } = string.Empty;
    public int AgingDays { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal LossRate { get; set; }
    public decimal Provision { get; set; }
}
