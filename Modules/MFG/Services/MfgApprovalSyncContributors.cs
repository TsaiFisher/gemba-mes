using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// MFG 業務文件的 IApprovalSyncContributor（mfg-domain.md §18.2）。
    /// 於 ApprovalService.ProcessApproval 終局時被呼叫，回寫工單簽核狀態；
    /// 規範：執行於 ApprovalService 的交易內，contributor 內**不要**自行 SaveChangesAsync。
    /// </summary>
    public class WorkOrderApprovalSync : IApprovalSyncContributor
    {
        private readonly IDocNumberService _docNumber;

        public WorkOrderApprovalSync(IDocNumberService docNumber)
        {
            _docNumber = docNumber;
        }

        public string DocumentType => "WorkOrder";

        public async Task SyncAsync(ApplicationDbContext db, string documentId, ApprovalStatus finalStatus, CancellationToken ct = default)
        {
            var wo = await db.Set<WorkOrder>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.WorkOrderId == documentId, ct);
            if (wo == null) return;

            DocumentApprovalStatus? newStatus = finalStatus switch
            {
                ApprovalStatus.Approved            => DocumentApprovalStatus.Approved,
                ApprovalStatus.Rejected            => DocumentApprovalStatus.Rejected,
                ApprovalStatus.ReturnedForRevision => DocumentApprovalStatus.Draft,
                ApprovalStatus.Withdrawn           => DocumentApprovalStatus.Draft,
                _ => null
            };
            if (newStatus == null) return;

            wo.ApprovalStatus = newStatus.Value;
            if (newStatus == DocumentApprovalStatus.Draft) wo.ApprovalRequestId = null;

            // 駁回／退回修改／撤回：比照工單畫面的「撤回」退回已規劃，才能修正後重新投產。
            // 原本只改核准狀態、Status 留在 Released——重新投產要 Planned、撤回要 PendingApproval，
            // 兩條路都不通，只剩作廢；同時排程與開工照樣把它當可生產（§18.2，2026-10-20）。
            if (finalStatus != ApprovalStatus.Approved && wo.Status == WorkOrderStatus.Released)
            {
                wo.Status = WorkOrderStatus.Planned;
                await WorkOrderApprovalGate.UnscheduleAsync(db, wo.WorkOrderId, DateTime.UtcNow, ct);
            }

            // §17.3 Push 料件自動建立 MaterialSlip(Draft)（WO PendingApproval → Approved 路徑）
            if (finalStatus == ApprovalStatus.Approved && !wo.IsOutsource)
                await CreatePushMaterialSlipAsync(db, wo, ct);
        }

        private async Task CreatePushMaterialSlipAsync(ApplicationDbContext db, WorkOrder wo, CancellationToken ct)
        {
            var pushLines = await db.Set<WorkOrderMaterial>()
                .IgnoreQueryFilters()
                .Where(m => m.WorkOrderId == wo.WorkOrderId && m.IssueMethod == MaterialIssueMethod.Push)
                .ToListAsync(ct);
            if (pushLines.Count == 0) return;

            var plantId = wo.PlantId ?? string.Empty;
            var today   = DateOnly.FromDateTime(DateTime.UtcNow);
            var (category, docType) = MaterialSlipFlow.PushIssueFor(wo.Type);
            var slipId  = await _docNumber.NextAsync(docType, plantId, today, ct);
            var now     = DateTime.UtcNow;

            var slip = new MaterialSlip
            {
                SlipId        = slipId,
                SlipNo        = slipId,
                Category      = category,
                Status        = MaterialSlipStatus.Draft,
                SlipDate      = today,
                SourceDocType = "WorkOrder",
                SourceDocId   = wo.WorkOrderId,
                PlantId       = plantId,
                RequestedBy   = string.Empty,
                RequestedAt   = now,
                Remarks       = "Auto-created on WO Approval",
                CreatedAt     = now,
                UpdatedAt     = now,
            };

            int lineNo = 1;
            foreach (var m in pushLines)
            {
                slip.Lines.Add(new MaterialSlipLine
                {
                    SlipId       = slipId,
                    LineNo       = lineNo++,
                    SourceLineId = $"{m.OperationSeq}_{m.BomSeq}",
                    ProductCode  = m.ProductCode,
                    RequestedQty = m.RequiredQty,
                    UomCode      = m.UomCode,
                });
            }

            db.Set<MaterialSlip>().Add(slip);
        }
    }
}
