using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services;

public static class DocStatusLabels
{
    public static string Label(DocStatus s) => s switch
    {
        DocStatus.Draft     => PzText.Get("Inv_DocStatus_Draft"),
        DocStatus.Confirmed => PzText.Get("Inv_DocStatus_Confirmed"),
        DocStatus.InTransit => PzText.Get("Inv_DocStatus_InTransit"),
        DocStatus.Posted    => PzText.Get("Inv_DocStatus_Posted"),
        DocStatus.Void      => PzText.Get("Inv_DocStatus_Void"),
        _                   => s.ToString(),
    };

    public static string Label(DocumentApprovalStatus s) => s switch
    {
        DocumentApprovalStatus.Draft           => PzText.Get("Inv_ApprovalStatus_Draft"),
        DocumentApprovalStatus.PendingApproval => PzText.Get("Inv_ApprovalStatus_Pending"),
        DocumentApprovalStatus.Approved        => PzText.Get("Inv_ApprovalStatus_Approved"),
        DocumentApprovalStatus.Rejected        => PzText.Get("Inv_ApprovalStatus_Rejected"),
        _                                      => s.ToString(),
    };
}
