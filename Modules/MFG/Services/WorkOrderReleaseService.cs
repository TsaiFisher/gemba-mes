using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 投產定案服務（mfg-domain.md §24）。把「重算量（net-at-release）→ 硬料閘（含在途 peg）→ 鎖庫出
    /// → 標準成本快照 → 送審/核准 → Released」核心邏輯集中，供單工單與批層整批投產共用。
    /// </summary>
    public class WorkOrderReleaseService : IWorkOrderReleaseService
    {
        private readonly ApplicationDbContext _db;
        private readonly IDateTimeService _dt;
        private readonly IApprovalService _approvalService;
        private readonly IPlantParamService _plantParams;
        private readonly IDocNumberService _docNumber;
        private readonly IInventoryReservationService _reservations;
        private readonly IUserContext _userContext;
        private readonly IOutsourceQuotationResolver? _quotations;
        private readonly IWorkOrderDemandReservation _demand;

        private const string DocType = "WorkOrder";

        public WorkOrderReleaseService(
            ApplicationDbContext db,
            IApprovalService approvalService,
            IPlantParamService plantParams,
            IDocNumberService docNumber,
            IInventoryReservationService reservations,
            IUserContext userContext,
            IDateTimeService dt,
            IOutsourceQuotationResolver? quotations = null,
            IWorkOrderDemandReservation? demand = null)
        {
            _quotations = quotations;
            _demand = demand ?? new WorkOrderDemandReservation(db, reservations);
            _db = db;
            _dt = dt;
            _approvalService = approvalService;
            _plantParams = plantParams;
            _docNumber = docNumber;
            _reservations = reservations;
            _userContext = userContext;
        }

        public async Task<WorkOrderReleasePreview> PreviewAsync(string workOrderId, CancellationToken ct = default)
        {
            var wo = await LoadAsync(workOrderId, ct);
            if (wo == null)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotFound);
            if (WorkOrderStateMachine.CanRelease(wo.Status) != WorkOrderGuard.Allowed)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotPlanned, wo.Quantity);

            var calc = await RecalcAsync(wo, ct);

            if (calc.NewQuantity <= 0)
                return new WorkOrderReleasePreview(workOrderId, WorkOrderReleaseOutcome.NotNeeded,
                    wo.Quantity, 0m, false, Array.Empty<string>());

            if (calc.ShortageProducts.Count > 0)
                return new WorkOrderReleasePreview(workOrderId, WorkOrderReleaseOutcome.MaterialBlocked,
                    wo.Quantity, calc.NewQuantity, calc.MaterialRisk, calc.ShortageProducts);

            // 無缺料：重算量是否變動決定是否需人工確認；不變動則直接可投產
            var outcome = calc.NewQuantity != wo.Quantity
                ? WorkOrderReleaseOutcome.NeedsConfirm
                : WorkOrderReleaseOutcome.Released;
            return new WorkOrderReleasePreview(workOrderId, outcome,
                wo.Quantity, calc.NewQuantity, calc.MaterialRisk, Array.Empty<string>());
        }

        public async Task<WorkOrderReleasePreview> ApplyAsync(string workOrderId, bool confirmed, CancellationToken ct = default)
        {
            var wo = await LoadAsync(workOrderId, ct);
            if (wo == null)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotFound);
            if (WorkOrderStateMachine.CanRelease(wo.Status) != WorkOrderGuard.Allowed)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotPlanned, wo.Quantity);

            var calc = await RecalcAsync(wo, ct);

            // §24.2 重算為 0（投產當下可用量已滿足毛需求）：不得投產。原本只當成「數量有變動」，人工確認後
            // 照樣投產——工單量、用料、派工全縮成 0，派工報工上限＝0 任何數量都被擋，工單永遠卡在已投產（2026-10-13）。
            if (calc.NewQuantity <= 0)
            {
                // 不投產，但那批抵掉需求的庫存要鎖給原需求（§24.2a）；重複按投產時 own 已含這筆，不會重鎖
                await _demand.LockForDemandAsync(wo, calc.StockToLock, ct);
                return new WorkOrderReleasePreview(workOrderId, WorkOrderReleaseOutcome.NotNeeded,
                    wo.Quantity, 0m, false, Array.Empty<string>());
            }

            // §24.3 硬料閘：缺料且在途補不足 → 整單擋住，不投產
            if (calc.ShortageProducts.Count > 0)
                return new WorkOrderReleasePreview(workOrderId, WorkOrderReleaseOutcome.MaterialBlocked,
                    wo.Quantity, calc.NewQuantity, calc.MaterialRisk, calc.ShortageProducts);

            // §24.2 重算需人工確認：量變動且未確認 → 不套用
            if (calc.NewQuantity != wo.Quantity && !confirmed)
                return new WorkOrderReleasePreview(workOrderId, WorkOrderReleaseOutcome.NeedsConfirm,
                    wo.Quantity, calc.NewQuantity, calc.MaterialRisk, Array.Empty<string>());

            var plantId = wo.PlantId ?? _userContext.PlantId ?? string.Empty;

            // ①～⑦ 一筆交易：⑤ 送審時 ApprovalService 會在同一個 DbContext 先 SaveChanges，
            // 不包交易的話「已投產」會比簽核狀態先落地——之後任一步失敗，工單就停在已投產＋草稿／已退回
            // （開工閘不擋草稿＝沒核准也開得了工），還留一張孤兒簽核單；沒失敗也有一段讀得到半套的空窗。
            await using var ownTx = await BeginOwnTransactionAsync(ct);

            // ① 套用重算量：連動工序派工量、BOM 用料量、庫出量（§24.2）
            ApplyRescale(wo, calc.ScaleFactor, calc.NewQuantity);
            await RescaleDispatchesAsync(wo, calc.ScaleFactor, ct);
            // ①' 拿來抵需求的庫存鎖給原需求（§24.2a）
            await _demand.LockForDemandAsync(wo, calc.StockToLock, ct);

            // ② 在途鎖定（§24.4）：對缺料但在途可補的料建立 Category=Supply 的 peg；
            //    先以採購在途補，餘量再以委外在途補。
            var byDate = DateOnly.FromDateTime(wo.PlannedStartDate);
            await PegInTransitAsync(wo, plantId, byDate, calc.RiskLines, ct);

            // ③ 料況：通過硬閘即視為 Ready；MaterialRisk = 在途已鎖、貨未到（§24.7）
            wo.MaterialReadinessStatus = MaterialReadinessStatus.Ready;

            // ④ 標準成本快照（§16.2，以重算後數量計）
            var materials = wo.Operations.SelectMany(o => o.Materials).ToList();
            var rates = await GetWorkCenterRatesAsync(ct);
            var (mat, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(wo.Quantity, materials, wo.Operations, rates);
            wo.StandardMaterialCost = mat;
            wo.StandardLaborCost = lab;
            wo.StandardOverheadCost = ovh;

            wo.Status = WorkOrderStatus.Released;
            wo.UpdatedAt = DateTime.UtcNow;

            // ⑤ 釋出簽核（§18.2）：依 ApprovalConfig 或金額閥值決定送審或直接核准
            var outcome = await ApplyApprovalAsync(wo, materials, mat + lab + ovh, ct);

            // ⑥ Dispatch.MaterialRisk 於投產時設定（取代排程層；§24.7）
            await SetDispatchMaterialRiskAsync(wo, calc.MaterialRisk, ct);

            // ⑦ 委外工單 Release 自動建立採購單（§19.3 Stage 2）
            await CreateOutsourcePurchaseOrderAsync(wo, plantId, ct);

            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);

            return new WorkOrderReleasePreview(workOrderId, outcome,
                wo.Quantity, calc.NewQuantity, calc.MaterialRisk, Array.Empty<string>());
        }

        /// <summary>
        /// 簡化模式自動投產（mfg-domain.md §25.3）：MRP 展開後直接把工單推到 Released，
        /// 略過投產閘門的人工關卡，但**保留三件下游必需品**——不保留會讓成本、料帳、委外流程斷掉。
        ///
        /// 保留：③在途 peg（鎖但不擋）／④標準成本快照／⑥Push 料領料單／⑧委外自動建 PO。
        /// 跳過：①淨量重算（MRP 展開時已淨算過）／②硬料閘（改由開工時的 mfg_material_gate 軟閘接手）
        ///       ／⑤簽核判定（直接視同已核准）。
        ///
        /// 呼叫端（MRP 執行）已開交易就併入；沒有則自開一筆，理由同 <see cref="ApplyAsync"/>。
        /// </summary>
        public async Task<WorkOrderReleasePreview> AutoReleaseAsync(string workOrderId, CancellationToken ct = default)
        {
            var wo = await LoadAsync(workOrderId, ct);
            if (wo == null)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotFound);
            if (WorkOrderStateMachine.CanRelease(wo.Status) != WorkOrderGuard.Allowed)
                return Fail(workOrderId, WorkOrderReleaseOutcome.NotPlanned, wo.Quantity);

            await using var ownTx = await BeginOwnTransactionAsync(ct);

            // 料況試算只取「風險/缺料」資訊，**不套用重算量、不據以擋單**
            var calc = await RecalcAsync(wo, ct);
            var plantId = wo.PlantId ?? _userContext.PlantId ?? string.Empty;
            var byDate = DateOnly.FromDateTime(wo.PlannedStartDate);

            // ③ 在途 peg：能鎖多少鎖多少。完整模式是「補不足就擋單」，簡化模式是「鎖了但不擋」——
            //    比完全不鎖正確，可避免多張工單各自把同一批在途算成自己的（§24.4 缺口）。
            await PegInTransitAsync(wo, plantId, byDate, calc.RiskLines, ct);

            // 料況據實回填（無硬閘故不能一律標 Ready，否則畫面顯示與實際不符）
            wo.MaterialReadinessStatus = calc.ShortageProducts.Count > 0
                ? MaterialReadinessStatus.Shortage
                : MaterialReadinessStatus.Ready;

            // ④ 標準成本快照（§16.2）：跳過則成本差異分析與 CST 成本鏈全空
            var materials = wo.Operations.SelectMany(o => o.Materials).ToList();
            var rates = await GetWorkCenterRatesAsync(ct);
            var (mat, lab, ovh) = WorkOrderCostCalculator.ComputeStandard(wo.Quantity, materials, wo.Operations, rates);
            wo.StandardMaterialCost = mat;
            wo.StandardLaborCost = lab;
            wo.StandardOverheadCost = ovh;

            wo.Status = WorkOrderStatus.Released;
            wo.UpdatedAt = DateTime.UtcNow;

            // ⑤ 簽核判定：簡化模式預設直接核准（§25），但明確設了門檻或強制送審條件者仍送審（A4）；
            //    ⑥ Push 料領料單在直接核准時產生、送審者於核准回寫時產生（同完整模式）
            var outcome = await ApplyApprovalAsync(wo, materials, mat + lab + ovh, ct, simplified: true);

            // ⑦ MaterialRisk：因保留 peg，「已鎖在途、貨未到」的語意仍然成立
            await SetDispatchMaterialRiskAsync(wo, calc.MaterialRisk, ct);

            // ⑧ 委外工單自動建立採購單（§19.3 Stage 2）：跳過則委外工單沒有對應 OPO
            await CreateOutsourcePurchaseOrderAsync(wo, plantId, ct);

            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);

            return new WorkOrderReleasePreview(workOrderId, outcome,
                wo.Quantity, wo.Quantity, calc.MaterialRisk, calc.ShortageProducts);
        }

        /// <summary>呼叫端沒開交易才自開（回傳 null＝併入呼叫端的交易，由它決定提交或回滾）。</summary>
        private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginOwnTransactionAsync(CancellationToken ct) =>
            _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;

        public async Task<BatchReleaseResult> ReleaseBatchAsync(string batchId, bool confirmed, CancellationToken ct = default)
        {
            var batch = await _db.ProductionBatches.FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
            if (batch == null)
                return new BatchReleaseResult(BatchReleaseOutcome.NotFound, 0, 0,
                    Array.Empty<BatchReleaseBlockedLine>(), Array.Empty<BatchReleaseConfirmLine>());

            // 批內 Planned 工單：半成品（有父工單）先於成品定案
            var plannedRows = await _db.WorkOrders.AsNoTracking()
                .Where(w => w.ProductionBatchId == batchId && w.Status == WorkOrderStatus.Planned)
                .OrderBy(w => w.ParentWorkOrderId == null)   // false(半成品) 先、true(成品) 後
                .ThenBy(w => w.WorkOrderId)
                .Select(w => new { w.WorkOrderId, w.ParentWorkOrderId })
                .ToListAsync(ct);
            var plannedWos = plannedRows.Select(w => w.WorkOrderId).ToList();

            if (plannedWos.Count == 0)
                return new BatchReleaseResult(BatchReleaseOutcome.NothingToRelease, 0, 0,
                    Array.Empty<BatchReleaseBlockedLine>(), Array.Empty<BatchReleaseConfirmLine>());

            // ① 逐張試算（不異動）
            var blocked = new List<BatchReleaseBlockedLine>();
            var needsConfirm = new List<BatchReleaseConfirmLine>();
            var previews = new Dictionary<string, WorkOrderReleasePreview>();
            foreach (var woId in plannedWos)
                previews[woId] = await PreviewAsync(woId, ct);

            // 重算為 0 的工單不投產；它底下的半成品子工單也一併略過——父工單不生產，子工單做出來的半成品沒有人用
            var notNeeded = previews.Where(p => p.Value.Outcome == WorkOrderReleaseOutcome.NotNeeded)
                                    .Select(p => p.Key).ToHashSet();
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var w in plannedRows)
                    if (w.ParentWorkOrderId != null && notNeeded.Contains(w.ParentWorkOrderId) && notNeeded.Add(w.WorkOrderId))
                        grew = true;
            }
            plannedWos = plannedWos.Where(id => !notNeeded.Contains(id)).ToList();
            var skipped = plannedRows.Select(w => w.WorkOrderId).Where(notNeeded.Contains).ToList();

            foreach (var woId in plannedWos)
            {
                var p = previews[woId];
                if (p.Outcome == WorkOrderReleaseOutcome.MaterialBlocked)
                    blocked.Add(new BatchReleaseBlockedLine(woId, p.ShortageProducts));
                else if (p.Outcome == WorkOrderReleaseOutcome.NeedsConfirm)
                    needsConfirm.Add(new BatchReleaseConfirmLine(woId, p.PlannedQty, p.RecalculatedQty));
            }

            // ② all-or-nothing：任一缺料 → 整批不投
            if (blocked.Count > 0)
                return new BatchReleaseResult(BatchReleaseOutcome.MaterialBlocked, 0, 0, blocked, needsConfirm, skipped);

            // ③ 有重算量變動且未確認 → 回傳需確認清單
            if (needsConfirm.Count > 0 && !confirmed)
                return new BatchReleaseResult(BatchReleaseOutcome.NeedsConfirm, 0, 0, blocked, needsConfirm, skipped);

            // 整批一筆交易（逐張 ApplyAsync 併入）：與上面缺料的 all-or-nothing 同一規則——
            // 原本逐張各自提交，第 N 張投產途中出錯時前 N−1 張已投產並送審，畫面卻只顯示失敗。
            await using var ownTx = await BeginOwnTransactionAsync(ct);

            // 重算為 0 而略過的（不含跟著父工單略過的子工單）：同樣把抵掉需求的庫存鎖給原需求
            foreach (var woId in previews.Where(p => p.Value.Outcome == WorkOrderReleaseOutcome.NotNeeded).Select(p => p.Key))
                await ApplyAsync(woId, true, ct);

            // ④ 全數通過 → 逐張定案（半成品先）
            int released = 0, submitted = 0;
            var noFlow = new List<string>();
            foreach (var woId in plannedWos)
            {
                var r = await ApplyAsync(woId, true, ct);
                if (r.Outcome.IsReleased()) released++;
                else if (r.Outcome == WorkOrderReleaseOutcome.Submitted) submitted++;
                if (r.Outcome == WorkOrderReleaseOutcome.ReleasedNoApprovalFlow) noFlow.Add(woId);
            }
            if (ownTx != null) await ownTx.CommitAsync(ct);

            return new BatchReleaseResult(BatchReleaseOutcome.Released, released, submitted,
                blocked, needsConfirm, skipped, noFlow);
        }

        // ── 重算（net-at-release）+ 硬料閘試算（不異動資料）─────────────────────────

        private sealed record RecalcResult(
            decimal NewQuantity,
            decimal ScaleFactor,
            bool MaterialRisk,
            IReadOnlyList<string> ShortageProducts,
            IReadOnlyList<(string ProductCode, decimal Shortfall)> RiskLines,
            decimal StockToLock = 0m);   // 重算時拿來抵需求、但還沒鎖給原需求的自由量（§24.2a）

        private async Task<RecalcResult> RecalcAsync(WorkOrder wo, CancellationToken ct)
        {
            var plantId = wo.PlantId ?? _userContext.PlantId ?? string.Empty;

            // 重算量 = 毛需求 − 可用量（本單預留 + 真正自由量）；GrossDemandQty<=0（升級前舊單）則不重算。
            decimal newQuantity = wo.Quantity;
            decimal stockToLock = 0m;
            if (wo.GrossDemandQty > 0)
            {
                var free = await _reservations.GetPlantAvailableQtyAsync(plantId, wo.ProductCode, ct);
                var own = await _demand.ReservedForDemandAsync(wo, ct);
                // 抵掉需求的自由量要鎖給原需求，否則少做了、那批庫存卻可能被別人用掉（§24.2a）
                stockToLock = Math.Max(0m, Math.Min(free, wo.GrossDemandQty - own));
                var availableForThisWo = free + own;
                var newMake = wo.GrossDemandQty - availableForThisWo;
                if (newMake < 0) newMake = 0;
                var cumYield0 = await CumulativeYield0Async(wo, ct);
                newQuantity = Math.Round(cumYield0 > 0 ? newMake / cumYield0 : newMake, 6, MidpointRounding.AwayFromZero);
            }

            var scaleFactor = wo.Quantity > 0 ? newQuantity / wo.Quantity : 1m;

            // 硬料閘：逐料（採購/庫存葉料）查可用，缺則查在途
            var shortages = new List<string>();
            var riskLines = new List<(string, decimal)>();
            bool materialRisk = false;
            var byDate = DateOnly.FromDateTime(wo.PlannedStartDate);

            foreach (var m in wo.Operations.SelectMany(o => o.Materials))
            {
                if (m.IssueMethod == MaterialIssueMethod.Backflush) continue;     // 完工才沖，不納硬閘
                if (!string.IsNullOrEmpty(m.ChildWorkOrderId)) continue;          // 自製半成品不納硬閘

                var required = Math.Round(m.RequiredQty * scaleFactor, 6, MidpointRounding.AwayFromZero);
                // 可用＝本單預留＋自由量（§24.2 同口徑）：MRP 展開時已為本工單鎖住的在手預留
                // 也被 GetPlantAvailableQtyAsync 扣掉，不加回會讓本工單被自己的預留擋在料閘外。
                var free = await _reservations.GetPlantAvailableQtyAsync(plantId, m.ProductCode, ct);
                var ownMat = await _reservations.GetReservedForWorkOrderAsync(
                    wo.WorkOrderId, m.ProductCode, ct);
                var avail = free + ownMat;
                if (avail >= required) continue;

                var shortfall = required - avail;
                var poInTransit  = await _reservations.GetAvailableInTransitAsync(plantId, m.ProductCode, byDate, ct);
                var opoInTransit = await _reservations.GetAvailableOutsourceInTransitAsync(plantId, m.ProductCode, byDate, ct);
                if (avail + poInTransit + opoInTransit >= required)
                {
                    riskLines.Add((m.ProductCode, shortfall));
                    materialRisk = true;
                }
                else
                {
                    shortages.Add(m.ProductCode);
                }
            }

            return new RecalcResult(newQuantity, scaleFactor, materialRisk,
                shortages.Distinct().ToList(), riskLines, stockToLock);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// §24.4 在途鎖定：對缺料但在途可補的料建立 Category=Supply 的 peg；
        /// 先以採購在途補，餘量再以委外在途補。
        /// 完整投產（<see cref="ApplyAsync"/>）與簡化模式自動投產（<see cref="AutoReleaseAsync"/>）共用；
        /// 差別只在呼叫端是否用「補不足」的結果去擋單，本方法本身一律「能鎖多少鎖多少」。
        /// </summary>
        private async Task PegInTransitAsync(
            WorkOrder wo, string plantId, DateOnly byDate,
            IReadOnlyList<(string ProductCode, decimal Shortfall)> riskLines, CancellationToken ct)
        {
            foreach (var risk in riskLines)
            {
                var remaining = risk.Shortfall;

                var poAvail = await _reservations.GetAvailableInTransitAsync(plantId, risk.ProductCode, byDate, ct);
                var usePo = Math.Min(remaining, poAvail);
                if (usePo > 0)
                {
                    var cover = await FindCoveringPoAsync(plantId, risk.ProductCode, byDate, usePo, ct);
                    await _reservations.PegSupplyAsync(plantId, risk.ProductCode, usePo,
                        ReservationSourceType.WorkOrderMaterial, wo.WorkOrderId, null, wo.PlannedStartDate,
                        "PurchaseOrder", cover?.PurchaseOrderId ?? string.Empty, cover?.LineNo,
                        cover?.DeliveryDate, ct);
                    remaining -= usePo;
                }

                if (remaining > 0)
                {
                    var cover = await FindCoveringOpoAsync(plantId, risk.ProductCode, byDate, remaining, ct);
                    await _reservations.PegSupplyAsync(plantId, risk.ProductCode, remaining,
                        ReservationSourceType.WorkOrderMaterial, wo.WorkOrderId, null, wo.PlannedStartDate,
                        "OutsourcePurchaseOrder", cover?.OpoId ?? string.Empty, cover?.LineNo,
                        cover?.ExpectedDeliveryDate, ct);
                }
            }
        }

        private Task<WorkOrder?> LoadAsync(string workOrderId, CancellationToken ct) =>
            _db.WorkOrders
                .Include(w => w.Operations).ThenInclude(o => o.Materials)
                .FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct);

        private static WorkOrderReleasePreview Fail(string id, WorkOrderReleaseOutcome outcome, decimal qty = 0) =>
            new(id, outcome, qty, qty, false, Array.Empty<string>());

        /// <summary>全線累積良率 = ∏(YieldRate/100)（= ComputeCumulativeYield[0]）。查無途程時為 1。</summary>
        private async Task<decimal> CumulativeYield0Async(WorkOrder wo, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(wo.RoutingId)) return 1m;
            var yields = await _db.Routings
                .Where(r => r.RoutingId == wo.RoutingId)
                .SelectMany(r => r.Operations)
                .Select(o => o.YieldRate)
                .ToListAsync(ct);
            if (yields.Count == 0) return 1m;
            decimal cum = 1m;
            foreach (var y in yields) cum *= Math.Max(y, 0.01m) / 100m;
            return cum;
        }

        private static void ApplyRescale(WorkOrder wo, decimal scaleFactor, decimal newQuantity)
        {
            wo.Quantity = newQuantity;
            if (scaleFactor == 1m) return;
            foreach (var m in wo.Operations.SelectMany(o => o.Materials))
                m.RequiredQty = Math.Round(m.RequiredQty * scaleFactor, 6, MidpointRounding.AwayFromZero);
        }

        private async Task RescaleDispatchesAsync(WorkOrder wo, decimal scaleFactor, CancellationToken ct)
        {
            if (scaleFactor == 1m) return;
            var dispatches = await _db.Dispatches
                .Include(d => d.Materials)
                .Where(d => d.WorkOrderId == wo.WorkOrderId)
                .ToListAsync(ct);
            foreach (var d in dispatches)
            {
                d.Quantity = Math.Round(d.Quantity * scaleFactor, 6, MidpointRounding.AwayFromZero);
                d.UpdatedAt = DateTime.UtcNow;
                foreach (var dm in d.Materials)
                    dm.RequiredQty = Math.Round(dm.RequiredQty * scaleFactor, 6, MidpointRounding.AwayFromZero);
            }
        }

        private async Task<Dictionary<string, (decimal Labor, decimal Machine)>> GetWorkCenterRatesAsync(CancellationToken ct)
        {
            var wcs = await _db.Set<WorkCenter>().AsNoTracking()
                .Select(wc => new { wc.WorkCenterCode, wc.StandardLaborCost, wc.MachineHourlyRate })
                .ToListAsync(ct);
            return wcs.ToDictionary(x => x.WorkCenterCode, x => (x.StandardLaborCost, x.MachineHourlyRate));
        }

        private async Task<WorkOrderReleaseOutcome> ApplyApprovalAsync(
            WorkOrder wo, List<WorkOrderMaterial> materials, decimal totalStdCost, CancellationToken ct,
            bool simplified = false)
        {
            var config = await _approvalService.GetApprovalConfig(DocType, _userContext.PlantId);

            // 與 ApprovalService.ResolveSaveStatusAsync 及其餘七個呼叫端同一組守衛：
            // IsApprovalRequired=false 或沒有範本都視為「未設線上簽核」。本處原本只看範本，
            // 是全站唯一漏看 IsApprovalRequired 的地方。
            bool configured = config?.IsApprovalRequired == true
                           && !string.IsNullOrEmpty(config.ApprovalTemplateId);

            // mfg-domain §18.2 的「快速通道」：設了門檻，就只有標準成本超過門檻的工單要簽，
            // 其餘自動核准；沒設門檻（未設定／0／負數／非數字）則維持「設了組態就每張都要簽」。
            // 強制送審條件（委外／重工／延期，A9）成立時略過快速通道。
            // 簡化模式（A4）：沒設門檻也沒有強制條件就照 §25 直接核准；明確設了才送審。
            var threshold = ParseThreshold(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgWoApprovalThreshold, _userContext.PlantId));
            bool exceeds = threshold is { } t && totalStdCost > t;
            bool forced  = await ForcedApprovalAsync(wo, ct);

            if (!configured)
            {
                // 門檻本身不能作為送審的觸發條件——沒有範本就無處可送；照舊直接核准，
                // 但超過門檻或符合強制條件時要讓投產的人知道「本該送審」（A3，原本完全無聲）。
                var direct = ApproveDirectly(wo, materials);
                return exceeds || forced ? WorkOrderReleaseOutcome.ReleasedNoApprovalFlow : direct;
            }

            bool submit = forced || exceeds || (!simplified && threshold == null);
            if (!submit)
                return ApproveDirectly(wo, materials);

            var req = await _approvalService.SubmitForApproval(
                wo.WorkOrderId, DocType, config.ApprovalTemplateId, _userContext.UserId ?? string.Empty);
            wo.ApprovalRequestId = req.Id;
            wo.ApprovalStatus = DocumentApprovalStatus.PendingApproval;
            return WorkOrderReleaseOutcome.Submitted;
        }

        /// <summary>門檻：合法正數才算設定（未設定／0／負數／非數字＝沒設）。前後空白與千分位都接受。</summary>
        internal static decimal? ParseThreshold(string? raw) =>
            decimal.TryParse(raw?.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

        /// <summary>§18.2 其餘三條觸發（A9）：委外、重工、預計完工晚於生產批需求日（訂單交期）。</summary>
        private async Task<bool> ForcedApprovalAsync(WorkOrder wo, CancellationToken ct)
        {
            var plant = wo.PlantId ?? _userContext.PlantId;
            async Task<bool> On(string key) => string.Equals(
                (await _plantParams.GetAsync(key, plant, ct))?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

            if (wo.IsOutsource && await On(ProjectZSystemParamKeys.MfgWoApprovalOutsource)) return true;
            if (wo.Type == WorkOrderType.Rework && await On(ProjectZSystemParamKeys.MfgWoApprovalRework)) return true;
            if (await On(ProjectZSystemParamKeys.MfgWoApprovalOnDelay))
            {
                var due = await _db.ProductionBatches.AsNoTracking()
                    .Where(b => b.BatchId == wo.ProductionBatchId)
                    .Select(b => (DateTime?)b.PlannedEndDate).FirstOrDefaultAsync(ct);
                if (due != null && wo.PlannedEndDate.Date > due.Value.Date) return true;
            }
            return false;
        }

        private async Task SetDispatchMaterialRiskAsync(WorkOrder wo, bool risk, CancellationToken ct)
        {
            if (!risk) return;
            var dispatches = await _db.Dispatches
                .Where(d => d.WorkOrderId == wo.WorkOrderId)
                .ToListAsync(ct);
            foreach (var d in dispatches)
            {
                d.MaterialRisk = true;
                d.UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>找出覆蓋缺口的最後一張在途 PO 行（依到貨日累計），供 peg 記錄供給來源與預計到貨日。</summary>
        private async Task<CoveringPo?> FindCoveringPoAsync(
            string plantId, string productCode, DateOnly byDate, decimal shortfall, CancellationToken ct)
        {
            var lines = await _db.Set<PurchaseOrderLine>()
                .Where(l => l.ProductCode == productCode
                         && !l.IsClosed
                         && l.ReceivedStockQty < l.StockQty   // 庫存單位，與缺口（shortfall）同單位（2026-10-27）
                         && l.DeliveryDate.HasValue
                         && l.DeliveryDate.Value <= byDate)
                .Join(_db.Set<PurchaseOrder>()
                        .Where(po => po.PlantId == plantId
                                  && po.ApprovalStatus == DocumentApprovalStatus.Approved),
                    l => l.PurchaseOrderId,
                    po => po.PurchaseOrderId,
                    (l, po) => new { l.PurchaseOrderId, l.LineNo, l.DeliveryDate, Remaining = l.StockQty - l.ReceivedStockQty })
                .OrderBy(x => x.DeliveryDate)
                .ToListAsync(ct);

            decimal acc = 0;
            foreach (var x in lines)
            {
                acc += x.Remaining;
                if (acc >= shortfall)
                    return new CoveringPo(x.PurchaseOrderId, x.LineNo, x.DeliveryDate);
            }
            return lines.Count > 0
                ? new CoveringPo(lines[^1].PurchaseOrderId, lines[^1].LineNo, lines[^1].DeliveryDate)
                : null;
        }

        private sealed record CoveringPo(string PurchaseOrderId, int LineNo, DateOnly? DeliveryDate);

        /// <summary>找出覆蓋缺口的最後一張委外在途 OPO 行（依委外應交日累計，扣已收量），供 peg 記錄供給來源。</summary>
        private async Task<CoveringOpo?> FindCoveringOpoAsync(
            string plantId, string productCode, DateOnly byDate, decimal shortfall, CancellationToken ct)
        {
            var lines = await _db.Set<OutsourcePurchaseOrderLine>()
                .Where(l => l.ProductCode == productCode)
                .Join(_db.Set<OutsourcePurchaseOrder>()
                        .Where(o => o.PlantId == plantId
                                 && o.Status == OutsourcePoStatus.Confirmed
                                 && o.ExpectedDeliveryDate.HasValue
                                 && o.ExpectedDeliveryDate.Value <= byDate),
                    l => l.OutsourcePurchaseOrderId,
                    o => o.OutsourcePurchaseOrderId,
                    (l, o) => new { l.OutsourcePurchaseOrderId, l.LineNo, l.Qty, o.ExpectedDeliveryDate })
                .OrderBy(x => x.ExpectedDeliveryDate)
                .ToListAsync(ct);

            if (lines.Count == 0) return null;

            // 各行已收量（Posted 委外進貨）
            var opoIds = lines.Select(x => x.OutsourcePurchaseOrderId).Distinct().ToList();
            var recv = await _db.Set<OutsourceReceiptLine>()
                .Join(_db.Set<OutsourceReceipt>().Where(r => r.Status == DocStatus.Posted),
                    orl => orl.OutsourceReceiptId, or => or.OutsourceReceiptId,
                    (orl, or) => new { or.OutsourcePurchaseOrderId, orl.OpoLineNo, orl.ReceiptQty })
                .Where(x => opoIds.Contains(x.OutsourcePurchaseOrderId) && x.OpoLineNo != null)
                .GroupBy(x => new { x.OutsourcePurchaseOrderId, x.OpoLineNo })
                .Select(g => new { g.Key.OutsourcePurchaseOrderId, g.Key.OpoLineNo, Recv = g.Sum(x => x.ReceiptQty) })
                .ToListAsync(ct);
            var recvMap = recv.ToDictionary(x => (x.OutsourcePurchaseOrderId, x.OpoLineNo!.Value), x => x.Recv);

            decimal acc = 0;
            (string OpoId, int LineNo, DateOnly? Exp)? last = null;
            foreach (var x in lines)
            {
                recvMap.TryGetValue((x.OutsourcePurchaseOrderId, x.LineNo), out var received);
                var outstanding = x.Qty - received;
                if (outstanding <= 0) continue;
                last = (x.OutsourcePurchaseOrderId, x.LineNo, x.ExpectedDeliveryDate);
                acc += outstanding;
                if (acc >= shortfall)
                    return new CoveringOpo(x.OutsourcePurchaseOrderId, x.LineNo, x.ExpectedDeliveryDate);
            }
            return last.HasValue ? new CoveringOpo(last.Value.OpoId, last.Value.LineNo, last.Value.Exp) : null;
        }

        private sealed record CoveringOpo(string OpoId, int LineNo, DateOnly? ExpectedDeliveryDate);

        private async Task CreateOutsourcePurchaseOrderAsync(WorkOrder wo, string plantId, CancellationToken ct)
        {
            if (!wo.IsOutsource || !string.IsNullOrEmpty(wo.OutsourcePurchaseOrderId))
                return;

            // 委外簡化 B3（mfg-domain.md §19.7）：參數開啟時改建真正的委外採購單（OPO），不再建舊的 PUR 採購單。
            // 挑不出唯一廠商就什麼都不建，留給「轉委外採購單」人工處理——不退回舊路徑，免得同一張工單兩種單據並存。
            if (string.Equals((await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgOutsourceAutoPo, plantId, ct))?.Trim(),
                    "true", StringComparison.OrdinalIgnoreCase))
            {
                if (_quotations == null)
                    throw new InvalidOperationException("mfg_outsource_auto_po is on but IOutsourceQuotationResolver is not registered.");
                await CreateAutoOutsourcePoAsync(wo, plantId, ct);
                return;
            }

            if (string.IsNullOrEmpty(wo.OutsourceSupplierCode))
                return;

            var poId = await _docNumber.NextAsync("PurchaseOrder", plantId, null, ct);
            var product = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProductCode == wo.ProductCode, ct);
            var now = DateTime.UtcNow;
            var po = new PurchaseOrder
            {
                PurchaseOrderId = poId,
                OrderDate       = DateOnly.FromDateTime(now),
                SupplierCode    = wo.OutsourceSupplierCode,
                Remarks         = $"委外採購單（自動建立，來源工單 {wo.WorkOrderId}）",
                PlantId         = wo.PlantId,
                CreatedAt       = now,
                UpdatedAt       = now,
                Lines           =
                [
                    new PurchaseOrderLine
                    {
                        PurchaseOrderId = poId,
                        LineNo          = 1,
                        ProductCode     = wo.ProductCode,
                        DeliveryDate    = wo.OutsourceExpectedDeliveryDate,
                        OrderQty        = wo.Quantity,
                        UomCode         = product?.StockUom,
                        // 採購單位就是庫存單位，庫存數量即訂購量。原本沒填（恆 0）：
                        // 在途改以庫存單位計算後，這張單的在途會變成 0，投產料閘與缺貨分析都看不到它（2026-10-27）
                        StockQty        = wo.Quantity,
                        StockUomCode    = product?.StockUom,
                    }
                ]
            };
            _db.PurchaseOrders.Add(po);
            wo.OutsourcePurchaseOrderId = poId;
        }

        /// <summary>
        /// 委外簡化 B3：投產時自動建立「已確認」的委外採購單，手機即可直接收料。
        ///
        /// <para>廠商：工單已指定就用它；沒指定時，找當日有效、且明確報到這個品號的報價，
        /// 只有一家廠商才採用。單價一律經 <see cref="IOutsourceQuotationResolver.Resolve"/> 取得——
        /// 指定廠商但沒有適用報價也不建（避免帶出 0 元的加工費一路快照到 AP）。</para>
        ///
        /// <para>委外建議（OutsourceDemand）與 Convert 相同：有就沿用並累加已轉量，沒有就建一筆。
        /// 不在此 SaveChanges——AutoRelease 由呼叫端以同一交易包覆。</para>
        /// </summary>
        private async Task CreateAutoOutsourcePoAsync(WorkOrder wo, string plantId, CancellationToken ct)
        {
            var today = _dt.GetLocalDate();
            var supplierCode = wo.OutsourceSupplierCode;
            if (string.IsNullOrEmpty(supplierCode))
            {
                var suppliers = await _db.OutsourceQuotations.AsNoTracking()
                    .Where(q => q.ProductCode == wo.ProductCode && q.IsActive
                             && q.EffectiveFrom <= today
                             && (q.EffectiveTo == null || q.EffectiveTo >= today))
                    .Select(q => q.SupplierCode).Distinct().Take(2).ToListAsync(ct);
                if (suppliers.Count != 1) return;
                supplierCode = suppliers[0];
            }

            var dispatch = await _db.Dispatches.AsNoTracking()
                .Where(d => d.WorkOrderId == wo.WorkOrderId && d.IsOutsource)
                .OrderBy(d => d.OperationSeq).FirstOrDefaultAsync(ct);
            var opSeq = dispatch?.OperationSeq ?? 1;

            var quote = _quotations!.Resolve(
                await _quotations.LoadActiveAsync(supplierCode, today, ct), wo.ProductCode, wo.RoutingId, opSeq);
            if (quote == null) return;

            var demand = await _db.OutsourceDemands.FirstOrDefaultAsync(d => d.WorkOrderId == wo.WorkOrderId, ct);
            if (demand == null)
            {
                demand = new OutsourceDemand
                {
                    DemandId     = await _docNumber.NextAsync("OutsourceDemand", plantId, today, ct),
                    WorkOrderId  = wo.WorkOrderId,
                    DispatchId   = dispatch?.DispatchId,
                    DemandType   = OutsourceDemandType.WholeProduct,
                    ProductCode  = wo.ProductCode,
                    DemandQty    = dispatch?.Quantity ?? wo.Quantity,
                    RequiredDate = DateOnly.FromDateTime(wo.PlannedEndDate),
                    PlantId      = plantId
                };
                _db.OutsourceDemands.Add(demand);
            }
            var qty = demand.DemandQty - demand.TransferredQty;
            if (qty <= 0) return;

            var expected = wo.OutsourceExpectedDeliveryDate ?? today.AddDays(quote.LeadDays);
            var poId = await _docNumber.NextAsync("OutsourcePurchaseOrder", plantId, today, ct);
            var now = DateTime.UtcNow;
            _db.OutsourcePurchaseOrders.Add(new OutsourcePurchaseOrder
            {
                OutsourcePurchaseOrderId = poId,
                SupplierCode = supplierCode,
                OrderDate    = today,
                ExpectedDeliveryDate = expected,
                Status       = OutsourcePoStatus.Confirmed,
                Remarks      = PzText.Get("Opo_AutoCreatedOnRelease", wo.WorkOrderId),
                PlantId      = plantId,
                CreatedAt    = now,
                UpdatedAt    = now,
                Lines =
                [
                    new OutsourcePurchaseOrderLine
                    {
                        OutsourcePurchaseOrderId = poId,
                        LineNo       = 1,
                        DemandId     = demand.DemandId,
                        SourceWorkOrderId = wo.WorkOrderId,
                        OperationSeq = opSeq,
                        ProductCode  = wo.ProductCode,
                        Qty          = qty,
                        SplitQty     = qty,
                        UomCode      = string.IsNullOrEmpty(quote.UomCode)
                            ? await _db.Products.AsNoTracking().Where(p => p.ProductCode == wo.ProductCode)
                                .Select(p => p.StockUom).FirstOrDefaultAsync(ct)
                            : quote.UomCode,
                        UnitPrice    = quote.UnitPrice,
                        AllowedScrapRate = quote.AllowedScrapRate,
                        CurrencyCode = quote.CurrencyCode
                    }
                ]
            });

            demand.TransferredQty += qty;
            demand.UpdatedAt = now;
            wo.OutsourcePurchaseOrderId = poId;
            wo.OutsourceSupplierCode = supplierCode;
            wo.OutsourceExpectedDeliveryDate = expected;
        }

        /// <summary>
        /// 直接核准（不送審）：標記 Approved 並產生 Push 料領料單。
        ///
        /// **Push 領料單是「核准」的必要副作用，不是簽核流程的一部分**——原本這兩件事耦合在
        /// <see cref="ApplyApprovalAsync"/> 的核准分支內，導致簡化模式若只是「跳過簽核判定」，
        /// 會連帶跳過領料單產生 → IssueMethod=Push 的用料永遠沒有領料單、庫存不扣、
        /// ActualMaterialCost 短計（mfg-domain.md §25.3-⑥）。故抽出獨立方法供兩條路徑共用。
        /// </summary>
        private WorkOrderReleaseOutcome ApproveDirectly(WorkOrder wo, List<WorkOrderMaterial> materials)
        {
            wo.ApprovalStatus = DocumentApprovalStatus.Approved;
            if (!wo.IsOutsource)
                CreatePushMaterialSlips(wo, materials);
            return WorkOrderReleaseOutcome.Released;
        }

        /// <summary>§17.3 Push 料件：核准後彙整 IssueMethod=Push 的用料為一張 MaterialSlip(Draft)。</summary>
        private void CreatePushMaterialSlips(WorkOrder wo, IEnumerable<WorkOrderMaterial> materials)
        {
            // 0 量的用料不開發料行（沒有東西可發；原本照樣產生 0 量的行，倉庫無從處理）
            var pushLines = materials.Where(m => m.IssueMethod == MaterialIssueMethod.Push && m.RequiredQty > 0).ToList();
            if (pushLines.Count == 0) return;

            var plantId = wo.PlantId ?? _userContext.PlantId ?? string.Empty;
            var today   = _dt.GetLocalDate();
            var (category, docType) = MaterialSlipFlow.PushIssueFor(wo.Type);
            var slipId  = _docNumber.NextAsync(docType, plantId, today).GetAwaiter().GetResult();
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
                RequestedBy   = _userContext.UserId ?? string.Empty,
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
            _db.MaterialSlips.Add(slip);
        }
    }
}
