using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Services
{
    /// <summary>
    /// QCR 模組的 IApprovalSyncContributor 實作。
    /// 規範：執行於 ApprovalService 的 SaveChanges 交易內，contributor 內**不要**呼叫 SaveChangesAsync。
    /// </summary>
    public class NcrApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "NonConformanceReport";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var ncr = await db.Set<NonConformanceReport>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(n => n.NcrId == documentId, ct);
            if (ncr == null) return;

            // NCR 有兩套狀態：ApprovalStatus（簽核）與 NcrStatus（處置生命週期）。
            // 「無簽核範本」那條路徑（NcrController.Submit）兩者都寫，走簽核流程時也必須兩者都寫，
            // 否則 UseAsIs 的執行閘門（Execute 要求 NcrStatus == Approved）永遠不會放行。
            switch (finalStatus)
            {
                case ApprovalStatus.Approved:
                    ncr.ApprovalStatus = DocumentApprovalStatus.Approved;
                    ncr.NcrStatus      = NcrStatus.Approved;
                    ncr.ApprovedAt     = DateTime.UtcNow;
                    break;

                case ApprovalStatus.Rejected:
                    ncr.ApprovalStatus = DocumentApprovalStatus.Rejected;
                    // 駁回退回 Open：Submit 的前置條件是 NcrStatus == Open，
                    // 停在 PendingApproval 會讓這張 NCR 既不能執行也不能重送。
                    ncr.NcrStatus      = NcrStatus.Open;
                    break;

                case ApprovalStatus.ReturnedForRevision:
                case ApprovalStatus.Withdrawn:
                    ncr.ApprovalStatus    = DocumentApprovalStatus.Draft;
                    ncr.NcrStatus         = NcrStatus.Open;
                    ncr.ApprovalRequestId = null;
                    break;

                default:
                    return;
            }
        }
    }
}
