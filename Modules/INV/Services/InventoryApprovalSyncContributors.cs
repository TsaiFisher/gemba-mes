using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// INV 模組業務文件的 IApprovalSyncContributor 實作。
    /// </summary>
    internal static class InventoryApprovalSyncHelper
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

    public class StockTransferApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "StockTransfer";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.StockTransfer>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.StockTransferId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class CrossPlantTransferApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "CrossPlantTransfer";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.CrossPlantTransfer>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.CrossPlantTransferId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class ScrapDocApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "ScrapDoc";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.ScrapDoc>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.ScrapDocId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class LoanDocApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "LoanDoc";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.LoanDoc>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.LoanDocId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class SampleDocApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "SampleDoc";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.SampleDoc>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.SampleDocId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class ReworkMaterialDocApprovalSync : IApprovalSyncContributor
    {
        // 型別字串以「送審端實際傳入值」為準（ReworkMaterialController.DocType），
        // 原本寫成 "ReworkMaterialDoc" 與送審值不符 → contributor 從未被路由到（R15 揪出）。
        public string DocumentType => "ReworkMaterial";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.ReworkMaterialDoc>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.ReworkMaterialDocId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class OutsourceMaterialDocApprovalSync : IApprovalSyncContributor
    {
        // 同 ReworkMaterial：以 OutsourceMaterialController.DocType 為準（R15 揪出的字串錯配）。
        public string DocumentType => "OutsourceMaterial";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.OutsourceMaterialDoc>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.OutsourceMaterialDocId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    /// <summary>
    /// 備料單：原本僅靠 Controller 開單時的 SyncApprovalStatusAsync 延遲拉回，
    /// 核准當下不回寫。改以 contributor 即時回寫（R15）。
    /// </summary>
    public class MaterialStagingOrderApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "MaterialStagingOrder";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.MaterialStagingOrder>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.StagingId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    /// <summary>到岸成本分攤單：同備料單，補上核准當下的即時回寫（R15）。</summary>
    public class LandedCostAllocationApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "LandedCostAllocation";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.LandedCostAllocation>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.LandedCostAllocationId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }

    public class StockAdjustmentApprovalSync : IApprovalSyncContributor
    {
        public string DocumentType => "StockAdjustment";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var doc = await db.Set<Modules.INV.Models.StockAdjustment>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.StockAdjustmentId == documentId, ct);
            if (doc == null) return;
            var newStatus = InventoryApprovalSyncHelper.Map(finalStatus);
            if (newStatus == null) return;
            doc.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) doc.ApprovalRequestId = null;
        }
    }
}
