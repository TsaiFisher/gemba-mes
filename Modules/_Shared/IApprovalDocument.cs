using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules._Shared;

public interface IApprovalDocument
{
    string? ApprovalRequestId { get; set; }
    DocumentApprovalStatus ApprovalStatus { get; set; }
}
