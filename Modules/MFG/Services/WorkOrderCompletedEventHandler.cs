using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// WorkOrderCompleted 事件處理器（mfg-domain.md §4.2 / §10.5）。
    /// 非同步執行成本累計與庫存異動，不阻塞即時報工交易。
    /// </summary>
    public class WorkOrderCompletedEventHandler : IDomainEventHandler<WorkOrderCompletedEvent>
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<WorkOrderCompletedEventHandler> _logger;

        public WorkOrderCompletedEventHandler(
            ApplicationDbContext db,
            ILogger<WorkOrderCompletedEventHandler> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task HandleAsync(WorkOrderCompletedEvent @event, CancellationToken ct = default)
        {
            _logger.LogInformation(
                "開始處理 WorkOrderCompleted 事件 (EventId={EventId}, WO={WorkOrderId})",
                @event.EventId, @event.WorkOrderId);

            try
            {
                // 1. 累計標準成本與實際成本差異
                await AccumulateCostAsync(@event, ct);

                // 2. 建立完工入庫庫存異動（轉往成品倉）
                await CreateProductCompletionMovementAsync(@event, ct);

                _logger.LogInformation(
                    "WorkOrderCompleted 事件處理完成 (EventId={EventId}, WO={WorkOrderId})",
                    @event.EventId, @event.WorkOrderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "WorkOrderCompleted 事件處理失敗 (EventId={EventId}, WO={WorkOrderId})",
                    @event.EventId, @event.WorkOrderId);
                throw; // 記錄後拋出，由背景服務重試機制處理
            }
        }

        /// <summary>累計成本到 WorkOrder（標準成本 vs 實際成本差異）。</summary>
        private async Task AccumulateCostAsync(WorkOrderCompletedEvent @event, CancellationToken ct)
        {
            var wo = await _db.WorkOrders.FirstOrDefaultAsync(
                w => w.WorkOrderId == @event.WorkOrderId, ct);

            if (wo == null)
            {
                _logger.LogWarning("WorkOrder {WorkOrderId} 不存在，跳過成本累計", @event.WorkOrderId);
                return;
            }

            // 人工與設備成本已於每次 Session 關閉時即時累計到 WorkOrder；此處確認時戳。
            wo.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            _logger.LogDebug(
                "成本確認完成: WO={WorkOrderId}, ActualLabor={Labor}, ActualOverhead={Overhead}",
                @event.WorkOrderId, wo.ActualLaborCost, wo.ActualOverheadCost);
        }

        /// <summary>建立完工入庫庫存異動（PROD_COMPLETE）。</summary>
        private async Task CreateProductCompletionMovementAsync(WorkOrderCompletedEvent @event, CancellationToken ct)
        {
            var wo = await _db.WorkOrders
                .Include(w => w.ProductionBatch)
                .FirstOrDefaultAsync(w => w.WorkOrderId == @event.WorkOrderId, ct);

            if (wo == null) return;

            // 確認工單有關聯的成品批次
            if (wo.ProductionBatch == null)
            {
                _logger.LogWarning(
                    "WorkOrder {WorkOrderId} 無關聯 ProductionBatch，無法建立完工異動",
                    @event.WorkOrderId);
                return;
            }

            // Phase F：完工入庫由 ProductionCompletionController（使用者手動觸發）處理，
            // 背景自動入庫需成品倉系統參數支援，留待 Phase F 完整實作。
            _logger.LogInformation(
                "WO={WorkOrderId} 完工事件已記錄；完工入庫請由完工入庫畫面手動確認。",
                @event.WorkOrderId);
        }
    }
}
