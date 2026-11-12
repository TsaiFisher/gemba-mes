using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Services
{
    /// <summary>
    /// PUR 模組業務文件的 IApprovalSyncContributor 實作。
    /// 規範：執行於 ApprovalService 的 SaveChanges 交易內，contributor 內**不要**呼叫 SaveChangesAsync。
    /// </summary>
    internal static class PurchaseApprovalSyncHelper
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

    public class PurchaseRequestApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "PurchaseRequest";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.PUR.Models.PurchaseRequest>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.PurchaseRequestId == documentId, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class PurchaseOrderApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "PurchaseOrder";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.PUR.Models.PurchaseOrder>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.PurchaseOrderId == documentId, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class GoodsReceiptApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "GoodsReceipt";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.PUR.Models.GoodsReceipt>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.GoodsReceiptId == documentId, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class GoodsReturnApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "GoodsReturn";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.PUR.Models.GoodsReturn>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.GoodsReturnId == documentId, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    /// <summary>
    /// 委外收貨：Confirm 要求 ApprovalStatus == Approved，但先前全模組沒有任何地方會把它寫成
    /// Approved（送審寫 Pending、撤回寫 Draft）。只要該部署替委外收貨設定簽核流程，
    /// 「收貨→庫存過帳→應付發票」整條就此死鎖。R15 揪出後補上。
    /// </summary>
    public class OutsourceReceiptApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "OutsourceReceipt";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.PUR.Models.OutsourceReceipt>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.OutsourceReceiptId == documentId, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    /// <summary>
    /// 供應商評分卡（Phase G+ SRM）：核准時同步回寫 Company.QualificationStatus（pur-domain.md §6.1）。
    /// documentId 為 VendorEvaluation.Id（int）字串化。
    /// </summary>
    public class VendorEvaluationApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "VendorEvaluation";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            if (!int.TryParse(documentId, out var id)) return;
            var doc = await db.Set<Modules.PUR.Models.VendorEvaluation>()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc == null) return;
            var newStatus = PurchaseApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;

            if (newStatus == DocumentApprovalStatus.Approved)
            {
                var company = await db.Set<Modules.BAS.Models.Company>()
                    .FirstOrDefaultAsync(c => c.CompanyCode == doc.SupplierCode, ct);
                if (company != null)
                {
                    company.QualificationStatus =
                        VendorEvaluationCalculator.StatusOf(doc.ResultStatus);
                    company.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
    }
}
