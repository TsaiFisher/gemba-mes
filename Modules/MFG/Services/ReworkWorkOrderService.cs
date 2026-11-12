using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 重工工單的建立（mfg-domain.md §14.2）。工單列表的「建立重工」與 NCR 處置「重工」共用這一份。
    /// </summary>
    public interface IReworkWorkOrderService
    {
        /// <summary>
        /// 加入（不存檔）一張連到 <paramref name="original"/> 的重工工單，複製原工單的工序與用料，
        /// 用料需求量依「重工數量 ÷ 原工單數量」等比換算，已發／已退量從 0 起算。
        /// 品號與原工單不同時（NCR 開在子件上）不複製——原工單的途程與用料不是那個品號的。
        /// <paramref name="operationSeqs"/> 有給時只複製這些工序與掛在它們底下的用料（§14.2「只需建立需要重做的製程」）；
        /// null＝全部複製。
        /// <paramref name="includeDefective"/>＝不良品已入庫（例如 FQC 判退後在待驗倉、已繳庫的成品）：
        /// 另加一行「不良品本身」的 Push 用料（數量＝重工數量），投產自動開的重工發料會把它一起發出去，
        /// 完工時它的帳面值就是良品回存貨的原值（mfg-domain.md §16.4b）。
        /// 不加的話完工會多出一份良品數量、不良品仍留在庫存。
        /// </summary>
        Task<WorkOrder> AddAsync(WorkOrder original, string reworkWorkOrderId, decimal quantity,
            string productCode, DateTime plannedEnd, IReadOnlyCollection<int>? operationSeqs = null,
            bool includeDefective = false, CancellationToken ct = default);
    }

    public class ReworkWorkOrderService : IReworkWorkOrderService
    {
        private readonly ApplicationDbContext _db;
        private readonly IUomService _uom;

        public ReworkWorkOrderService(ApplicationDbContext db, IUomService uom)
        {
            _db = db;
            _uom = uom;
        }

        public async Task<WorkOrder> AddAsync(WorkOrder original, string reworkWorkOrderId, decimal quantity,
            string productCode, DateTime plannedEnd, IReadOnlyCollection<int>? operationSeqs = null,
            bool includeDefective = false, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var rework = new WorkOrder
            {
                WorkOrderId           = reworkWorkOrderId,
                ProductionBatchId     = original.ProductionBatchId,   // 沿用原批號
                ProductCode           = productCode,
                RoutingId             = original.RoutingId,
                Quantity              = quantity,
                Status                = WorkOrderStatus.Planned,
                PlannedStartDate      = now,
                PlannedEndDate        = plannedEnd,                   // 暫估，釋出後排程決定
                ReworkFromWorkOrderId = original.WorkOrderId,
                Type                  = WorkOrderType.Rework,
                ApprovalStatus        = DocumentApprovalStatus.Draft,
                PlantId               = original.PlantId,
                CreatedAt             = now,
                UpdatedAt             = now,
            };
            _db.WorkOrders.Add(rework);

            // 沒有工序就排不出派工、沒有用料就發不了料——投產後整張卡死（2026-10-15 前 NCR 路徑就是這樣）。
            if (productCode != original.ProductCode) return rework;

            var ops = await _db.WorkOrderOperations
                .Include(o => o.Materials)
                .Where(o => o.WorkOrderId == original.WorkOrderId
                         && (operationSeqs == null || operationSeqs.Contains(o.OperationSeq)))
                .AsNoTracking()
                .ToListAsync(ct);

            // 原工單用料是依原工單數量展開的總量；重工只做 quantity 個，等比縮。
            // 原本照抄總量：重工 3 個仍要發 10 個份的料，投產自動開的 Push 發料單也是全量。
            var ratio = original.Quantity > 0 ? quantity / original.Quantity : 1m;

            foreach (var op in ops)
            {
                _db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId        = reworkWorkOrderId,
                    OperationSeq       = op.OperationSeq,
                    OperationName      = op.OperationName,
                    WorkCenterId       = op.WorkCenterId,
                    SetupTime          = op.SetupTime,
                    UnitTime           = op.UnitTime,
                    TimeUnit           = op.TimeUnit,
                    SetupTimeUnit      = op.SetupTimeUnit,
                    MoveTime           = op.MoveTime,
                    QueueTime          = op.QueueTime,
                    BatchMode          = op.BatchMode,
                    BatchQty           = op.BatchQty,
                    IsOutsource        = op.IsOutsource,
                    RequiresInspection = op.RequiresInspection,
                    RequiresFpi        = op.RequiresFpi,
                });

                foreach (var m in op.Materials)
                {
                    _db.WorkOrderMaterials.Add(new WorkOrderMaterial
                    {
                        WorkOrderId     = reworkWorkOrderId,
                        OperationSeq    = op.OperationSeq,
                        BomSeq          = m.BomSeq,
                        ProductCode     = m.ProductCode,
                        RequiredQty     = await _uom.RoundQtyAsync(m.UomCode, m.RequiredQty * ratio, ct),
                        UomCode         = m.UomCode,
                        SourceMode      = m.SourceMode,
                        UnitCost        = m.UnitCost,
                        IssueMethod     = m.IssueMethod,
                        BackflushTiming = m.BackflushTiming,
                        IssuedQty       = 0m,   // 重工單獨立發料追蹤
                        ReturnedQty     = 0m,
                    });
                }
            }

            // 不良品本身掛在第一道重做工序上（工單用料一定要掛工序）；BomSeq 取 0，原工單的 BOM 序從 1 起。
            if (includeDefective && ops.Count > 0)
            {
                var firstSeq = ops.Min(o => o.OperationSeq);
                var bomSeq   = ops.Single(o => o.OperationSeq == firstSeq).Materials.Any(m => m.BomSeq == 0)
                    ? ops.Single(o => o.OperationSeq == firstSeq).Materials.Max(m => m.BomSeq) + 1
                    : 0;
                var product = await _db.Products.AsNoTracking()
                    .Where(p => p.ProductCode == productCode)
                    .Select(p => new { p.StockUom, p.StandardCost })
                    .FirstOrDefaultAsync(ct);
                // 標準材料成本用：本廠現行庫存單位成本（移動平均全廠同步），沒有庫存紀錄才退回標準單價
                var bookUnit = await _db.Inventories.AsNoTracking()
                    .Where(i => i.ProductCode == productCode && i.PlantId == original.PlantId && i.UnitCost > 0)
                    .Select(i => (decimal?)i.UnitCost)
                    .FirstOrDefaultAsync(ct);
                _db.WorkOrderMaterials.Add(new WorkOrderMaterial
                {
                    WorkOrderId  = reworkWorkOrderId,
                    OperationSeq = firstSeq,
                    BomSeq       = bomSeq,
                    ProductCode  = productCode,
                    RequiredQty  = quantity,
                    UomCode      = product?.StockUom ?? string.Empty,
                    SourceMode   = MrpMode.StockOnly,      // 料就是庫存裡的不良品，不另外生產或採購
                    UnitCost     = bookUnit ?? product?.StandardCost ?? 0m,
                    IssueMethod  = MaterialIssueMethod.Push,
                    IssuedQty    = 0m,
                    ReturnedQty  = 0m,
                });
            }
            return rework;
        }
    }
}
