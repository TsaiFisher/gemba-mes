using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class MaterialSlipProgressRowVm
{
    public string SlipId { get; set; } = string.Empty;
    public string SlipNo { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public MaterialSlipStatus Status { get; set; }
    public DateOnly SlipDate { get; set; }
    public string SourceDocId { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public decimal TotalRequestedQty { get; set; }
    public decimal TotalConfirmedQty { get; set; }
    public decimal FulfillRate => TotalRequestedQty > 0
        ? Math.Round(TotalConfirmedQty / TotalRequestedQty * 100, 1) : 0m;
}
