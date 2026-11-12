using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc />
    public class StagingSourceService : IStagingSourceService
    {
        private readonly ApplicationDbContext _db;
        private readonly IPlantParamService _plantParams;
        private readonly IDocNumberService _docNumber;
        private readonly IApprovalService _approval;
        private readonly IPlantDefaultLocationResolver _plantDefaults;

        public StagingSourceService(ApplicationDbContext db, IPlantParamService plantParams,
            IDocNumberService docNumber, IApprovalService approval,
            IPlantDefaultLocationResolver plantDefaults)
        {
            _db = db;
            _plantParams = plantParams;
            _docNumber = docNumber;
            _approval = approval;
            _plantDefaults = plantDefaults;
        }

        public async Task<IReadOnlyList<StagingSourceLine>> GetLinesAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sourceId)) return Array.Empty<StagingSourceLine>();

            var lines = sourceType switch
            {
                "Dispatch"        => await FromDispatchAsync(sourceId, ct),
                "WorkOrder"       => await FromWorkOrderAsync(sourceId, ct),
                "ProductionBatch" => await FromBatchAsync(sourceId, plantId, ct),
                _                 => new List<StagingSourceLine>(),
            };

            if (lines.Count == 0) return lines;

            await DeductAlreadyStagedAsync(lines, ct);
            await FillProductNamesAsync(lines, ct);

            // 已備滿的料件不再列出，避免使用者對著一排 0 逐行刪除
            return lines.Where(l => l.RequiredQty > 0).ToList();
        }

        // ── 來源展開 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 派工層級：DispatchMaterial 的需求量，扣掉對應工單用料（同工序、同 BOM 序）的已發量。
        /// 已發量要看 WorkOrderMaterial——發料過帳只累加它，DispatchMaterial.IssuedQty 沒有人回寫，
        /// 只讀派工那份會把已經發完的料再備一次、再鎖一次庫存。Backflush 料比照工單層級不列入。
        /// </summary>
        private async Task<List<StagingSourceLine>> FromDispatchAsync(string dispatchId, CancellationToken ct)
        {
            var dispatch = await _db.Dispatches.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DispatchId == dispatchId, ct);
            if (dispatch == null) return new List<StagingSourceLine>();

            var dispatchMats = await _db.Set<DispatchMaterial>().AsNoTracking()
                .Where(m => m.DispatchId == dispatchId && m.RequiredQty > m.IssuedQty)
                .OrderBy(m => m.BomSeq)
                .ToListAsync(ct);
            if (dispatchMats.Count == 0) return new List<StagingSourceLine>();

            var woMats = await _db.WorkOrderMaterials.AsNoTracking()
                .Where(m => m.WorkOrderId == dispatch.WorkOrderId && m.OperationSeq == dispatch.OperationSeq)
                .ToDictionaryAsync(m => m.BomSeq, ct);

            var result = new List<StagingSourceLine>();
            foreach (var m in dispatchMats)
            {
                var remaining = m.RequiredQty - m.IssuedQty;
                if (woMats.TryGetValue(m.BomSeq, out var wom))
                {
                    if (wom.IssueMethod == MaterialIssueMethod.Backflush) continue;
                    remaining = Math.Min(remaining, wom.RequiredQty - wom.IssuedQty);
                }
                if (remaining <= 0) continue;
                result.Add(new StagingSourceLine
                {
                    ProductCode       = m.ProductCode,
                    UomCode           = m.UomCode,
                    RequiredQty       = remaining,
                    SourceWorkOrderId = dispatch.WorkOrderId,
                    SourceDispatchId  = dispatchId,
                });
            }
            return result;
        }

        /// <summary>
        /// 工單層級：WorkOrderMaterial 的未發料量。Backflush 料不列入——
        /// 它在完工時才由系統直接沖銷，先搬進備料倉只會讓帳面和實際各走各的。
        /// </summary>
        private async Task<List<StagingSourceLine>> FromWorkOrderAsync(string workOrderId, CancellationToken ct) =>
            await _db.WorkOrderMaterials.AsNoTracking()
                .Where(m => m.WorkOrderId == workOrderId
                         && m.IssueMethod != MaterialIssueMethod.Backflush
                         && m.RequiredQty > m.IssuedQty)
                .OrderBy(m => m.OperationSeq).ThenBy(m => m.BomSeq)
                .Select(m => new StagingSourceLine
                {
                    ProductCode       = m.ProductCode,
                    UomCode           = m.UomCode,
                    RequiredQty       = m.RequiredQty - m.IssuedQty,
                    SourceWorkOrderId = workOrderId,
                })
                .ToListAsync(ct);

        /// <summary>生產批層級：該批底下所有未結案工單彙總（逐行仍記工單，供預留轉移認人）。</summary>
        private async Task<List<StagingSourceLine>> FromBatchAsync(string batchId, string plantId, CancellationToken ct)
        {
            var workOrderIds = await _db.WorkOrders.AsNoTracking()
                .Where(w => w.ProductionBatchId == batchId
                         && w.PlantId == plantId
                         && w.Status != WorkOrderStatus.Closed
                         && w.Status != WorkOrderStatus.Void)
                .Select(w => w.WorkOrderId)
                .ToListAsync(ct);

            var result = new List<StagingSourceLine>();
            foreach (var woId in workOrderIds)
                result.AddRange(await FromWorkOrderAsync(woId, ct));
            return result;
        }

        // ── 共用調整 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 扣掉既有備料單（未作廢）已經鎖住的量。少了這一步，同一張派工重複開備料單會把
        /// 同一批料鎖兩次，第二張確認時不是佔用別張工單的可用量、就是直接因可用量不足而擋單。
        /// </summary>
        private async Task DeductAlreadyStagedAsync(List<StagingSourceLine> lines, CancellationToken ct)
        {
            var workOrderIds = lines.Select(l => l.SourceWorkOrderId)
                                    .Where(w => !string.IsNullOrEmpty(w))
                                    .Distinct()
                                    .ToList();
            if (workOrderIds.Count == 0) return;

            var staged = await _db.Set<MaterialStagingLine>().AsNoTracking()
                .Where(sl => sl.SourceWorkOrderId != null
                          && workOrderIds.Contains(sl.SourceWorkOrderId)
                          && sl.StagingOrder!.Status != "Void")
                .GroupBy(sl => new { sl.SourceWorkOrderId, sl.ProductCode })
                .Select(g => new
                {
                    g.Key.SourceWorkOrderId,
                    g.Key.ProductCode,
                    Qty = g.Sum(x => x.RequiredQty),
                })
                .ToListAsync(ct);

            if (staged.Count == 0) return;

            foreach (var line in lines)
            {
                var hit = staged.FirstOrDefault(s => s.SourceWorkOrderId == line.SourceWorkOrderId
                                                  && s.ProductCode == line.ProductCode);
                if (hit != null)
                    line.RequiredQty -= hit.Qty;
            }
        }

        private async Task FillProductNamesAsync(List<StagingSourceLine> lines, CancellationToken ct)
        {
            var codes = lines.Select(l => l.ProductCode).Distinct().ToList();
            var products = await _db.Products.AsNoTracking()
                .Where(p => codes.Contains(p.ProductCode))
                .Select(p => new { p.ProductCode, p.ProductName, p.StockUom })
                .ToListAsync(ct);

            foreach (var line in lines)
            {
                var p = products.FirstOrDefault(x => x.ProductCode == line.ProductCode);
                if (p == null) continue;
                line.ProductName  = p.ProductName;
                line.StockUomCode = p.StockUom;
                line.UomCode    ??= p.StockUom;
            }
        }

        // ── 一鍵開單 ──────────────────────────────────────────────────────────

        public async Task<string> CreateDraftAsync(
            string sourceType, string sourceId, string plantId, string userId, CancellationToken ct = default)
        {
            var lines = await GetLinesAsync(sourceType, sourceId, plantId, ct);
            if (lines.Count == 0)
                throw new InvalidOperationException(PzText.Get("Mfg_NothingToStage"));

            var (whCode, locationCode) = await ResolveStagingTargetAsync(sourceType, sourceId, plantId, ct);

            // 有簽核範本才進 Draft 等送審；沒設定範本的廠別直接視為已核准，
            // 與備料單畫面 Save 的判斷一致，否則現場開完單卻卡在無人可簽的待審狀態。
            var config        = await _approval.GetApprovalConfig("MaterialStagingOrder", plantId);
            var needsApproval = config?.ApprovalTemplateId != null;

            var order = new MaterialStagingOrder
            {
                StagingId           = await _docNumber.NextAsync("MaterialStagingOrder", plantId, ct: ct),
                PlantId             = plantId,
                SourceType          = sourceType,
                SourceId            = sourceId,
                StagingWhCode       = whCode,
                StagingLocationCode = locationCode,
                Status              = "Draft",
                ApprovalStatus      = needsApproval
                    ? DocumentApprovalStatus.Draft
                    : DocumentApprovalStatus.Approved,
                CreatedBy           = userId,
                CreatedAt           = DateTime.UtcNow,
                UpdatedAt           = DateTime.UtcNow,
            };

            var lineNo = 1;
            foreach (var l in lines)
            {
                order.Lines.Add(new MaterialStagingLine
                {
                    StagingId         = order.StagingId,
                    LineNo            = lineNo++,
                    ProductCode       = l.ProductCode,
                    UomCode           = l.UomCode,
                    StockUomCode      = l.StockUomCode ?? l.UomCode,
                    RequiredQty       = l.RequiredQty,
                    SourceWorkOrderId = l.SourceWorkOrderId,
                    SourceDispatchId  = l.SourceDispatchId,
                });
            }

            _db.Set<MaterialStagingOrder>().Add(order);
            await _db.SaveChangesAsync(ct);
            return order.StagingId;
        }

        public async Task<IReadOnlyList<StagingOrderBrief>> GetExistingOrdersAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default)
        {
            // 派工來源：除了表頭掛本派工的單，也要找「明細行掛本派工」的單——
            // 工單／生產批層級開的備料單同樣備到了這張派工的料，現場不該再開一張。
            var query = _db.Set<MaterialStagingOrder>().AsNoTracking()
                .Where(o => o.PlantId == plantId && o.Status != "Void");

            query = sourceType switch
            {
                "Dispatch" => query.Where(o => (o.SourceType == "Dispatch" && o.SourceId == sourceId)
                                            || o.Lines.Any(l => l.SourceDispatchId == sourceId)),
                _          => query.Where(o => o.SourceType == sourceType && o.SourceId == sourceId),
            };

            return await query
                .OrderBy(o => o.StagingId)
                .Select(o => new StagingOrderBrief
                {
                    StagingId           = o.StagingId,
                    Status              = o.Status,
                    StagingWhCode       = o.StagingWhCode,
                    StagingLocationCode = o.StagingLocationCode,
                    LineCount           = o.Lines.Count,
                })
                .ToListAsync(ct);
        }

        // ── 備料倉解析 ────────────────────────────────────────────────────────

        /// <summary>
        /// 備料倉三層解析：開單指定 &gt; 工作中心設定 &gt; 系統參數。
        /// 每一層取到值後都再過一次 <see cref="IPlantDefaultLocationResolver"/>——
        /// 系統參數是全域單值，倉庫代碼卻只屬於某一個廠（<c>Warehouse</c> 主鍵為 <c>WhCode</c> 單欄），
        /// 不驗廠別就會把**別廠的倉庫**帶進備料單。工作中心那一層同樣要驗：它指向的倉庫
        /// 可能早已停用或被改到別廠。驗不過即視同該層未設定，往下一層找。
        /// </summary>
        public async Task<(string? WhCode, string? LocationCode)> ResolveStagingTargetAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct = default)
        {
            var workCenterId = await ResolveWorkCenterAsync(sourceType, sourceId, plantId, ct);

            if (!string.IsNullOrEmpty(workCenterId))
            {
                var wc = await _db.Set<WorkCenter>().AsNoTracking()
                    .Where(w => w.WorkCenterCode == workCenterId && w.PlantId == plantId)
                    .Select(w => new { w.StagingWhCode, w.StagingLocationCode })
                    .FirstOrDefaultAsync(ct);

                if (wc != null && !string.IsNullOrEmpty(wc.StagingWhCode))
                {
                    var fromWc = await _plantDefaults.ResolveAsync(
                        wc.StagingWhCode, wc.StagingLocationCode, plantId, ct);
                    if (fromWc.WhCode != null) return fromWc;
                }
            }

            var paramWh  = await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingWh, plantId);
            var paramLoc = await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgDefaultStagingLocation, plantId);
            return await _plantDefaults.ResolveAsync(paramWh, paramLoc, plantId, ct);
        }

        /// <summary>來源對應的工作中心：派工取本工序，工單取第一道工序，生產批無對應。</summary>
        private async Task<string?> ResolveWorkCenterAsync(
            string sourceType, string sourceId, string plantId, CancellationToken ct)
        {
            if (sourceType == "Dispatch")
            {
                return await _db.Dispatches.AsNoTracking()
                    .Where(d => d.DispatchId == sourceId)
                    .Select(d => d.Operation!.WorkCenterId)
                    .FirstOrDefaultAsync(ct);
            }

            if (sourceType == "WorkOrder")
            {
                return await _db.WorkOrderOperations.AsNoTracking()
                    .Where(o => o.WorkOrderId == sourceId)
                    .OrderBy(o => o.OperationSeq)
                    .Select(o => o.WorkCenterId)
                    .FirstOrDefaultAsync(ct);
            }

            return null;
        }
    }
}
