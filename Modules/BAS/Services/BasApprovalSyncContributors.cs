using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.BAS.Services
{
    /// <summary>
    /// BAS 主檔（產品／往來廠商／稅別）的 IApprovalSyncContributor 實作。
    ///
    /// 這三張主檔送審時會被寫成 IsActive=false（「待主管核准後生效」），但先前沒有任何
    /// contributor 也沒有 sync-on-read，核准後既不會轉 Approved 也不會復原 IsActive
    /// → 主檔永遠停用、下拉選單永遠選不到。R15 揪出後補上。
    ///
    /// IsActive 的取捨：送審時 Controller 已無條件覆寫為 false，使用者原本的停用意圖
    /// 在那一刻就已遺失，核准端無從還原，故核准一律恢復為啟用（與「核准後生效」語意一致）；
    /// 要停用主檔請於核准後另行編輯。
    /// 規範：執行於 ApprovalService 的 SaveChanges 交易內，contributor 內**不要**呼叫 SaveChangesAsync。
    /// </summary>
    internal static class BasApprovalSyncHelper
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

    public class ProductApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "Product";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Models.Product>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.ProductCode == documentId, ct);
            if (doc == null) return;
            var newStatus = BasApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Approved) doc.IsActive = true;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
            doc.UpdatedAt = DateTime.UtcNow;
        }
    }

    public class CompanyApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "Company";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Models.Company>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.CompanyCode == documentId, ct);
            if (doc == null) return;
            var newStatus = BasApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Approved) doc.IsActive = true;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
            doc.UpdatedAt = DateTime.UtcNow;
        }
    }

    public class TaxApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "Tax";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Models.Tax>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.TaxCode == documentId, ct);
            if (doc == null) return;
            var newStatus = BasApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Approved) doc.IsActive = true;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
            doc.UpdatedAt = DateTime.UtcNow;
        }
    }
}
