namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class MrpAchievementRowVm
{
    public string MrpRunId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? UomCode { get; set; }
    public DateTime RequiredDate { get; set; }
    public decimal RequiredQty { get; set; }
    public decimal ActualQty { get; set; }
    public decimal AchievementRate => RequiredQty > 0
        ? Math.Round(ActualQty / RequiredQty * 100, 1) : 0m;
    public decimal ShortfallQty => RequiredQty - ActualQty;
    public string? ValidationError { get; set; }
}
