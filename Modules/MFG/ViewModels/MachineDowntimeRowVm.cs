namespace SmartFactory.MES.Modules.MFG.ViewModels;

public class MachineDowntimeRowVm
{
    public string? WorkCenterId { get; set; }
    public string? MachineCode { get; set; }
    public string? DowntimeType { get; set; }
    public int EventCount { get; set; }
    public decimal TotalHours { get; set; }
    public string? Reason { get; set; }
}
