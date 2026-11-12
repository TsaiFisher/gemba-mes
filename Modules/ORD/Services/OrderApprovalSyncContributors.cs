using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Services
{
    /// <summary>
    /// ORD 模組業務文件的 IApprovalSyncContributor 實作。
    /// </summary>
    internal static class OrderApprovalSyncHelper
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

    public class SalesOrderApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "SalesOrder";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.ORD.Models.SalesOrder>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.SalesOrderId == documentId, ct);
            if (doc == null) return;
            var newStatus = OrderApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class SalesReturnApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "SalesReturn";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.ORD.Models.SalesReturn>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.SalesReturnId == documentId, ct);
            if (doc == null) return;
            var newStatus = OrderApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class GoodsShipmentApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "GoodsShipment";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.ORD.Models.GoodsShipment>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.GoodsShipmentId == documentId, ct);
            if (doc == null) return;
            var newStatus = OrderApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class InternalOrderApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "InternalOrder";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.ORD.Models.InternalOrder>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.InternalOrderId == documentId, ct);
            if (doc == null) return;
            var newStatus = OrderApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }
}
