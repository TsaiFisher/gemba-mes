using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services
{
    /// <summary>
    /// 手動 AP／AR 發票的簽核回寫（manual-test-plan.md §11 #9）。
    ///
    /// ⚠️ **沒有 contributor 的後果是靜默停在待審**：`SubmitForApproval` 之後簽核流程照跑、
    /// 簽核者也按得下核准，但業務單據的 `ApprovalStatus` 永遠停在原地——畫面上看不出異常，
    /// 只有「這張單怎麼一直沒生效」。所以簽核與 contributor 必須同一批進來，不能分兩次做。
    ///
    /// 這兩支比別的模組多做一件事：**核准當下才補開認列分錄**。
    /// 手動發票是全系統唯一能憑空產生應付／應收的路徑，未核准就掛帳等於簽核只是表面流程，
    /// 所以 `CreateManualAsync` 在未核准時刻意不寫總帳，由這裡補。
    /// </summary>
    internal static class InvoiceApprovalSyncHelper
    {
        public static DocumentApprovalStatus? Map(ApprovalStatus s) => s switch
        {
            ApprovalStatus.Approved            => DocumentApprovalStatus.Approved,
            ApprovalStatus.Rejected            => DocumentApprovalStatus.Rejected,
            ApprovalStatus.ReturnedForRevision => DocumentApprovalStatus.Draft,
            ApprovalStatus.Withdrawn           => DocumentApprovalStatus.Draft,
            _ => null
        };
    }

    public class APInvoiceApprovalSync : IApprovalSyncContributor
    {
        private readonly IAPInvoiceService _svc;
        public APInvoiceApprovalSync(IAPInvoiceService svc) => _svc = svc;

        public string DocumentType => "APInvoice";

        public async Task SyncAsync(ApplicationDbContext db, string documentId,
            ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.APInvoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.APInvoiceId == documentId, ct);
            if (doc == null) return;

            var newStatus = InvoiceApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;

            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;

            // 分錄只在核准時補，且 PostManualJournalAsync 自己是冪等的——
            // 退回再重送會再次走到這裡，不冪等的話同一張發票會掛兩次應付，
            // 而兩張傳票各自都平衡，總帳不會有任何訊號。
            if (newStatus == DocumentApprovalStatus.Approved)
                await _svc.PostManualJournalAsync(documentId, ct);
        }
    }

    public class ARInvoiceApprovalSync : IApprovalSyncContributor
    {
        private readonly IARInvoiceService _svc;
        public ARInvoiceApprovalSync(IARInvoiceService svc) => _svc = svc;

        public string DocumentType => "ARInvoice";

        public async Task SyncAsync(ApplicationDbContext db, string documentId,
            ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.ARInvoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.ARInvoiceId == documentId, ct);
            if (doc == null) return;

            var newStatus = InvoiceApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;

            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;

            if (newStatus == DocumentApprovalStatus.Approved)
                await _svc.PostManualJournalAsync(documentId, ct);
        }
    }
}
