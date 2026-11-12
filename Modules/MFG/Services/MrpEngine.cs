using SmartFactory.MES.Modules._Shared;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.INV.Services;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ORD.Models;
using SmartFactory.MES.Modules.PUR.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <inheritdoc cref="IMrpEngine"/>
    public class MrpEngine : IMrpEngine
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryReservationService _reservations;
        private readonly IDocNumberService _docNumber;
        private readonly IDateTimeService _dt;
        private readonly IPlantParamService _plantParams;

        /// <summary>生產批採番 DocType（BAS → 單號設定 須有此設定列）。</summary>
        public const string BatchDocType = "ProductionBatch";

        /// <summary>BOM 巢狀展開防呆上限（mfg-domain.md §10-3）。</summary>
        public const int MaxBomDepth = 20;

        /// <summary>每日最大工時上限（小時），用於無限產能倒推估算（mfg-domain.md §10-10）。</summary>
        private const decimal DailyWorkHours = 24m;

        private readonly IWorkCalendarService? _calendar;  // optional：未設定日曆時退回 24h/天估算

        // §25 製造簡化模式的自動投產委派。刻意設為必填建構參數（非 optional）——
        // 若可為 null，簡化模式開啟時會靜默不投產，錯誤只在下游「工單永遠停在 Planned」才顯現。
        private readonly IWorkOrderReleaseService _release;

        public MrpEngine(
            ApplicationDbContext db,
            IInventoryReservationService reservations,
            IDocNumberService docNumber,
            IDateTimeService dt,
            IPlantParamService plantParams,
            IWorkOrderReleaseService release,
            IWorkCalendarService? calendar = null)
        {
            _db = db;
            _reservations = reservations;
            _docNumber = docNumber;
            _dt = dt;
            _plantParams = plantParams;
            _release = release;
            _calendar = calendar;
        }

        public async Task<MrpRunResult> RunAsync(string mrpRunId, CancellationToken ct = default)
        {
            var run = await LoadRunAsync(mrpRunId, ct);
            if (run.Status != MrpRunStatus.Draft)
                throw new InvalidOperationException(
                    PzText.Get("Mfg_MrpAlreadyExpandedFmt", mrpRunId));
            return await ExpandAsync(run, ct);
        }

        /// <summary>
        /// 重跑（mfg-domain.md §10-5 / §12.2）：清除前次展開後重新展開。
        /// **已投產（InProgress/Completed/Closed）的工單不可被覆蓋** → 命中即拋
        /// <see cref="MrpReRunBlockedException"/>（錯誤碼 WO_IN_PROGRESS）。
        /// 「保留鎖定批、僅重建未投產」之增量重跑（§10-1 評估方向）列為未來精修。
        /// </summary>
        public async Task<MrpRunResult> ReRunAsync(string mrpRunId, CancellationToken ct = default)
        {
            var run = await LoadRunAsync(mrpRunId, ct);
            if (run.Status != MrpRunStatus.Executed)
                throw new InvalidOperationException(PzText.Get("Mfg_MrpNotExpandedFmt", mrpRunId));

            // §10-5 分級閘門：所有下游守門收斂於 EvaluateReRunAsync（唯一判定來源），
            // 此處只依評估等級決定是否中止，不再各自查一次 DB。
            var evaluation = await EvaluateReRunAsync(mrpRunId, ct);
            if (!evaluation.CanReRun)
            {
                // 工單已投產維持原有錯誤型別／錯誤碼（WO_IN_PROGRESS），呼叫端與測試依此判別。
                var started = evaluation.Blocking
                    .FirstOrDefault(i => i.Kind == MrpReRunImpedimentKind.WorkOrderStarted);
                if (started != null)
                    throw new MrpReRunBlockedException(started.DocumentId, started.Detail);

                throw new InvalidOperationException(
                    PzText.Get("Mfg_RerunBlockedHeader") + "\n" +
                    string.Join("\n", evaluation.Blocking.Select(i => i.Description)));
            }

            var batchIds = await _db.ProductionBatches
                .Where(b => b.MrpRunId == mrpRunId).Select(b => b.BatchId).ToListAsync(ct);
            var wos = await _db.WorkOrders
                .Where(w => batchIds.Contains(w.ProductionBatchId)).ToListAsync(ct);

            // 清除前次展開（皆未投產）：釋放預留 + 刪除批/工單/工序/用料/派工
            await _reservations.ReleaseBySourceAsync(ReservationSourceType.MrpRunLine, mrpRunId, ct);
            foreach (var wo in wos)
                await _reservations.ReleaseBySourceAsync(ReservationSourceType.WorkOrderMaterial, wo.WorkOrderId, ct);
            await DeleteExpansionAsync(batchIds, wos, ct);

            // 重設需求列計算欄（重新展開時重算）
            foreach (var line in run.Lines)
            {
                line.StockQty = line.PurchaseQty = line.OutsourceQty = line.MakeQty = 0;
                line.ProductionBatchId = null;
                line.ValidationError = null;
            }
            await _db.SaveChangesAsync(ct);

            return await ExpandAsync(run, ct);
        }

        /// <inheritdoc cref="IMrpEngine.ArchiveAsync"/>
        public async Task<IReadOnlyList<string>> ArchiveAsync(string mrpRunId, CancellationToken ct = default)
        {
            var run = await LoadRunAsync(mrpRunId, ct);
            if (run.Status != MrpRunStatus.Executed)
                throw new InvalidOperationException(
                    PzText.Get("Mfg_MrpNotExpandedArchiveFmt", mrpRunId));

            var batches = await _db.ProductionBatches
                .Where(b => b.MrpRunId == mrpRunId && b.Status != ProductionBatchStatus.Void)
                .ToListAsync(ct);
            var batchIds = batches.Select(b => b.BatchId).ToList();
            var wos = await _db.WorkOrders
                .Where(w => batchIds.Contains(w.ProductionBatchId)).ToListAsync(ct);
            var woIds = wos.Select(w => w.WorkOrderId).ToList();

            // 「已投產」判準與 EvaluateReRunAsync 一致：工單狀態 + 派工實際產出。
            var reportedWoIds = (await _db.Dispatches.AsNoTracking()
                .Where(d => woIds.Contains(d.WorkOrderId) && d.QtyDone > 0)
                .Select(d => d.WorkOrderId).Distinct().ToListAsync(ct)).ToHashSet();

            bool Started(WorkOrder w) =>
                w.Status is WorkOrderStatus.InProgress or WorkOrderStatus.Completed or WorkOrderStatus.Closed
                || reportedWoIds.Contains(w.WorkOrderId);

            var voidedBatchIds = new List<string>();
            foreach (var batch in batches)
            {
                var batchWos = wos.Where(w => w.ProductionBatchId == batch.BatchId).ToList();

                // 整批完全未投產才作廢；只要有一張工單動過，整批保持原狀繼續走生命週期
                if (batchWos.Any(Started)) continue;

                foreach (var w in batchWos.Where(w => w.Status != WorkOrderStatus.Void))
                {
                    w.Status = WorkOrderStatus.Void;
                    w.UpdatedAt = DateTime.UtcNow;
                    await _reservations.ReleaseBySourceAsync(
                        ReservationSourceType.WorkOrderMaterial, w.WorkOrderId, ct);
                }

                // 作廢工單的派工一併取消（與工單畫面作廢一致）：未投產＝沒有產出，
                // 留著會掛在派工清單當待辦、準備中者還被排程當凍結佔用。
                var batchWoIds = batchWos.Select(w => w.WorkOrderId).ToList();
                foreach (var d in await _db.Dispatches
                             .Where(d => batchWoIds.Contains(d.WorkOrderId)
                                      && d.Status != DispatchStatus.Closed && d.Status != DispatchStatus.Cancelled)
                             .ToListAsync(ct))
                {
                    d.Status         = DispatchStatus.Cancelled;
                    d.ScheduledStart = null;
                    d.ScheduledEnd   = null;
                    d.HoldReason     = null;
                    d.HoldFromStatus = null;
                    d.HoldAt         = null;
                    d.UpdatedAt      = DateTime.UtcNow;
                }
                batch.Status = ProductionBatchStatus.Void;
                voidedBatchIds.Add(batch.BatchId);
            }

            // 成品層預留掛在 MrpRunLine 上，釋放介面只支援整張單為單位——
            // 僅在所有生產批都被作廢（無任何存活批仍需這些成品庫存）時才整批釋放，
            // 否則會把仍屬存活批的成品庫存錯誤地放回可用量。
            if (voidedBatchIds.Count == batches.Count)
                await _reservations.ReleaseBySourceAsync(ReservationSourceType.MrpRunLine, mrpRunId, ct);

            run.Status = MrpRunStatus.Archived;
            run.IsDirty = false;
            run.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return voidedBatchIds;
        }

        /// <inheritdoc cref="IMrpEngine.EvaluateReRunAsync"/>
        /// <remarks>
        /// 判準是「下游是否已產生不可回收的外部承諾或實體／財務足跡」，不是「單據是否已核准」：
        /// <list type="bullet">
        /// <item>L3 阻擋：工單已投產、派工已回報產量或已有上工／整備紀錄、委外需求仍引用工單
        /// （皆為 Restrict FK 或實體足跡，重跑會刪不掉或抹掉既成事實）。</item>
        /// <item>L2 殘留：請購單已送審／已核准／已轉採購。重跑本身可執行（工單照常重建），
        /// 但這些請購單不會被自動收斂，需人工處理——即「品號由採購改自製後，既有請購單留在原地」
        /// 這類脫鉤的來源。</item>
        /// </list>
        /// 唯讀，不異動任何資料。
        /// </remarks>
        public async Task<MrpReRunEvaluation> EvaluateReRunAsync(string mrpRunId, CancellationToken ct = default)
        {
            var eval = new MrpReRunEvaluation { MrpRunId = mrpRunId };

            var batchIds = await _db.ProductionBatches.AsNoTracking()
                .Where(b => b.MrpRunId == mrpRunId).Select(b => b.BatchId).ToListAsync(ct);
            var wos = await _db.WorkOrders.AsNoTracking()
                .Where(w => batchIds.Contains(w.ProductionBatchId))
                .Select(w => new { w.WorkOrderId, w.Status }).ToListAsync(ct);
            var woIds = wos.Select(w => w.WorkOrderId).ToList();

            // (1) 工單已投產（§10-5 / §12.2）：不可覆蓋已投產工單
            foreach (var w in wos.Where(w =>
                w.Status is WorkOrderStatus.InProgress or WorkOrderStatus.Completed or WorkOrderStatus.Closed))
            {
                eval.Impediments.Add(new MrpReRunImpediment
                {
                    Kind        = MrpReRunImpedimentKind.WorkOrderStarted,
                    Level       = MrpReRunLevel.Blocked,
                    DocumentId  = w.WorkOrderId,
                    Detail      = w.Status.ToString(),
                    Description = PzText.Get("Mfg_RerunWoStartedFmt", w.WorkOrderId, w.Status)});
            }

            var dispatches = await _db.Dispatches.AsNoTracking()
                .Where(d => woIds.Contains(d.WorkOrderId))
                .Select(d => new { d.DispatchId, d.QtyDone }).ToListAsync(ct);

            // (2) 派工已回報產量：實體足跡，重跑等同抹掉既成產出
            foreach (var d in dispatches.Where(d => d.QtyDone > 0))
            {
                eval.Impediments.Add(new MrpReRunImpediment
                {
                    Kind        = MrpReRunImpedimentKind.DispatchReported,
                    Level       = MrpReRunLevel.Blocked,
                    DocumentId  = d.DispatchId,
                    Description = PzText.Get("Mfg_RerunDispatchReportedFmt", d.DispatchId, d.QtyDone)});
            }

            // (3) 派工已有上工／整備紀錄（Restrict FK）
            var dispatchIds = dispatches.Select(d => d.DispatchId).ToList();
            var touchedDispatchIds = await _db.ProductionSetups
                .Where(s => dispatchIds.Contains(s.DispatchId)).Select(s => s.DispatchId)
                .Union(_db.ProductionSessions
                    .Where(s => dispatchIds.Contains(s.DispatchId)).Select(s => s.DispatchId))
                .Distinct().ToListAsync(ct);
            foreach (var id in touchedDispatchIds)
            {
                eval.Impediments.Add(new MrpReRunImpediment
                {
                    Kind        = MrpReRunImpedimentKind.DispatchTouched,
                    Level       = MrpReRunLevel.Blocked,
                    DocumentId  = id,
                    Description = PzText.Get("Mfg_RerunDispatchStartedFmt", id)});
            }

            // (4) 委外需求（判準式，非「存在即擋」）：已轉委外採購單＝對外承諾，阻擋；
            //     尚未轉單者只是 Restrict FK 引用，重跑時連同工單一併刪除重建即可。
            var demands = await _db.OutsourceDemands.AsNoTracking()
                .Where(d => d.WorkOrderId != null && woIds.Contains(d.WorkOrderId))
                .Select(d => new { d.DemandId, d.WorkOrderId, d.TransferredQty }).ToListAsync(ct);
            foreach (var d in demands.Where(d => d.TransferredQty > 0))
            {
                eval.Impediments.Add(new MrpReRunImpediment
                {
                    Kind        = MrpReRunImpedimentKind.OutsourceDemandTransferred,
                    Level       = MrpReRunLevel.Blocked,
                    DocumentId  = d.DemandId,
                    Description = PzText.Get("Mfg_RerunOutsourceOrderedFmt", d.DemandId, d.WorkOrderId, d.TransferredQty)});
            }
            foreach (var d in demands.Where(d => d.TransferredQty <= 0))
            {
                eval.Impediments.Add(new MrpReRunImpediment
                {
                    Kind        = MrpReRunImpedimentKind.OutsourceDemandPending,
                    Level       = MrpReRunLevel.Residual,
                    DocumentId  = d.DemandId,
                    Description = PzText.Get("Mfg_RerunOutsourceRebuildFmt", d.DemandId, d.WorkOrderId)});
            }

            // (5) 請購單殘留（不阻擋重跑）：重跑會重建工單，但既有請購單不會被自動收斂。
            //     查詢刻意與 MrpRunController.CreatePurchaseRequestFromDemandsAsync 的去重查詢
            //     採同一組全域過濾條件，避免評估看得到、去重看不到（或反之）而彼此矛盾。
            var pr = await _db.PurchaseRequests.AsNoTracking().Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.SourceMrpRunId == mrpRunId, ct);
            if (pr != null)
            {
                var orderedLines = pr.Lines.Where(l => l.OrderedQty > 0).ToList();
                if (orderedLines.Count > 0)
                {
                    eval.Impediments.Add(new MrpReRunImpediment
                    {
                        Kind        = MrpReRunImpedimentKind.PurchaseRequestOrdered,
                        Level       = MrpReRunLevel.Residual,
                        DocumentId  = pr.PurchaseRequestId,
                        Detail      = string.Join(", ", orderedLines.Select(l => l.ProductCode)),
                        Description = PzText.Get("Mfg_RerunPrConvertedFmt", pr.PurchaseRequestId, string.Join("、", orderedLines.Select(l => l.ProductCode)))});
                }
                else if (pr.ApprovalStatus == DocumentApprovalStatus.PendingApproval)
                {
                    eval.Impediments.Add(new MrpReRunImpediment
                    {
                        Kind        = MrpReRunImpedimentKind.PurchaseRequestPendingApproval,
                        Level       = MrpReRunLevel.Residual,
                        DocumentId  = pr.PurchaseRequestId,
                        Description = PzText.Get("Mfg_RerunPrPendingFmt", pr.PurchaseRequestId)});
                }
                else if (pr.ApprovalStatus == DocumentApprovalStatus.Approved)
                {
                    eval.Impediments.Add(new MrpReRunImpediment
                    {
                        Kind        = MrpReRunImpedimentKind.PurchaseRequestApproved,
                        Level       = MrpReRunLevel.Residual,
                        DocumentId  = pr.PurchaseRequestId,
                        Description = PzText.Get("Mfg_RerunPrApprovedFmt", pr.PurchaseRequestId)});
                }
                // Draft 且無明細轉採購 → 去重邏輯會直接覆寫重建，非殘留項
            }

            return eval;
        }

        private async Task<MrpRun> LoadRunAsync(string mrpRunId, CancellationToken ct)
        {
            var run = await _db.MrpRuns.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.MrpRunId == mrpRunId, ct);
            return run ?? throw new InvalidOperationException(PzText.Get("Mfg_MrpRunNotFoundFmt", mrpRunId));
        }

        /// <summary>共用展開：依需求列展開為批/工單/派工，完成後 MrpRun 轉/維持 Executed。</summary>
        private async Task<MrpRunResult> ExpandAsync(MrpRun run, CancellationToken ct)
        {
            var mrpRunId = run.MrpRunId;
            var plantId = run.PlantId
                ?? throw new InvalidOperationException(PzText.Get("Mfg_MrpRunNoPlantFmt", mrpRunId));

            var simplifiedMode = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.InvSimplifiedMode, plantId), "true",
                StringComparison.OrdinalIgnoreCase);

            var lotPolicy = await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgLotPolicy, plantId) ?? "LotForLot";

            var ctx = new RunContext
            {
                PlantId = plantId,
                // _dt.Now 為廠別本地時間（Kind 非 Utc）；標為 UTC 供倒推防呆分支寫入 WorkOrder
                // 日期欄（timestamptz）時相容 Npgsql（房規：DateTime 一律存 UTC）。
                Today = DateTime.SpecifyKind(_dt.Now.Date, DateTimeKind.Utc),
                Result = new MrpRunResult { MrpRunId = mrpRunId },
                Ct = ct,
                IsSimplifiedMode = simplifiedMode,
                LotPolicy = lotPolicy,
                UomDecimals = await _db.Uoms.AsNoTracking()
                    .ToDictionaryAsync(u => u.UomCode, u => u.DecimalPlaces, ct),
            };

            // 派工/展開順序：交期(EDD) → 行序。生產批優先序 CN_P 屬批次層級，
            // 排程引擎（Phase C）再依規則細排；此處以交期近似 MRP 處理順序（mfg-domain.md §5.1）。
            var lines = run.Lines.OrderBy(l => l.RequiredDate).ThenBy(l => l.LineSeq).ToList();

            // 0. 可行性預檢（mfg-domain.md §11；全有全無）：先驗證**全部**需求列（品號存在、
            //    途程/BOM/單位換算），任一列失敗即整張中止、不產生任何生產批/工單——拋例外由
            //    呼叫端回滾交易並回填各列 ValidationError，修正主檔後重新執行。
            var products = new Dictionary<string, Product>();
            var failedLines = new List<MrpSkippedLine>();
            foreach (var line in lines)
            {
                var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductCode == line.ProductCode, ct);
                if (product == null)
                {
                    failedLines.Add(new MrpSkippedLine(line.LineSeq, line.ProductCode, PzText.Get("Mfg_ProductNotFoundFmt", line.ProductCode)));
                    continue;
                }
                products[line.ProductCode] = product;

                var errors = new List<string>();
                await ValidateLineAsync(product, errors, new HashSet<string>(), 0, ct);
                if (errors.Count > 0)
                    failedLines.Add(new MrpSkippedLine(line.LineSeq, line.ProductCode, string.Join("；", errors.Distinct())));
            }
            if (failedLines.Count > 0)
                throw new MrpExpansionValidationException(mrpRunId, failedLines);

            foreach (var line in lines)
            {
                var product = products[line.ProductCode];
                line.ValidationError = null;

                // 1. Fstock：成品層可用庫存，先預留滿足量（扣安全庫存緩衝，不下探安全存量）
                var taken = await ReserveAvailableAsync(ctx, line.ProductCode, line.RequiredQty, product.SafetyStock,
                    ReservationSourceType.MrpRunLine, mrpRunId, line.LineSeq, line.RequiredDate);
                line.StockQty = taken;
                line.PurchaseQty = line.OutsourceQty = line.MakeQty = 0;

                var hasRouting = !string.IsNullOrWhiteSpace(product.RoutingId);
                var mode = ParseMode(product.MrpMode, hasRouting);
                line.Mode = mode;

                var remain = line.RequiredQty - taken;
                if (remain <= 0)
                    continue; // 庫存全數滿足，不需展開

                // 2. 依 CD_MRP 決定剩餘需求處理（mfg-domain.md §5.1）
                switch (mode)
                {
                    case MrpMode.StockOnly:
                        ctx.Result.Shortages.Add(new MrpDemandSignal(
                            line.ProductCode, remain, line.RequiredDate, "僅庫存供應(Q)但庫存不足"));
                        break;

                    case MrpMode.Purchase:
                        line.PurchaseQty = remain;
                        var pqQty = ApplyLotSizing(remain, product, lotPolicy);
                        ctx.Result.PurchaseDemands.Add(new MrpDemandSignal(
                            line.ProductCode, pqQty, line.RequiredDate, "成品採購(P)"));
                        break;

                    case MrpMode.Outsource:
                        line.OutsourceQty = remain;
                        var osQty = ApplyLotSizing(remain, product, lotPolicy);
                        var osBatch = await CreateBatchAsync(ctx, run, line, osQty, ct);
                        await RunJAsync(ctx, product, osQty, line.RequiredQty, osBatch, line.RequiredDate, null, 0);
                        break;

                    case MrpMode.Make:
                        line.MakeQty = remain;
                        var mkQty = ApplyLotSizing(remain, product, lotPolicy);
                        var mkBatch = await CreateBatchAsync(ctx, run, line, mkQty, ct);
                        await RunMpsAsync(ctx, product, mkQty, line.RequiredQty, mkBatch, line.RequiredDate, null, 0);
                        break;
                }
            }

            run.Status    = MrpRunStatus.Executed;
            run.ExecutedAt = DateTime.UtcNow;
            run.IsDirty   = false;   // §21.6：展開完成後清除 dirty 標記
            await _db.SaveChangesAsync(ct);

            // §25.3 製造簡化模式：展開完成即自動投產，工單直接進入 Released（免投產閘門）。
            // 未設定／false 一律維持完整流程（工單停在 Planned 等人工投產）。
            await AutoReleaseIfSimplifiedAsync(ctx, ct);

            ctx.Result.BatchCount = ctx.Result.CreatedBatchIds.Count;
            return ctx.Result;
        }

        /// <summary>
        /// §25.3 製造簡化模式的自動投產：對本次展開產生的 Planned 工單逐張 AutoRelease。
        ///
        /// 順序與 <c>ReleaseBatchAsync</c> 一致——**半成品先於成品**，因為成品工單的用料含半成品，
        /// 先投成品會讓半成品尚未 Released 的層級關係與料況判定失準。
        /// 單張失敗不中斷其餘（缺料在簡化模式本就不擋單），以 Warnings 回報供畫面提示。
        /// </summary>
        private async Task AutoReleaseIfSimplifiedAsync(RunContext ctx, CancellationToken ct)
        {
            var enabled = string.Equals(
                await _plantParams.GetAsync(ProjectZSystemParamKeys.MfgSimplifiedMode, ctx.PlantId), "true",
                StringComparison.OrdinalIgnoreCase);
            if (!enabled || ctx.Result.CreatedBatchIds.Count == 0) return;

            var workOrderIds = await _db.WorkOrders.AsNoTracking()
                .Where(w => ctx.Result.CreatedBatchIds.Contains(w.ProductionBatchId)
                         && w.Status == WorkOrderStatus.Planned)
                .OrderBy(w => w.ParentWorkOrderId == null)   // false(半成品) 先、true(成品) 後
                .ThenBy(w => w.WorkOrderId)
                .Select(w => w.WorkOrderId)
                .ToListAsync(ct);

            foreach (var woId in workOrderIds)
            {
                var r = await _release.AutoReleaseAsync(woId, ct);
                if (r.Outcome == WorkOrderReleaseOutcome.Submitted)
                {
                    // 簡化模式仍設了門檻／強制送審條件（A4）：已投產並送審，核准後才可開工
                    ctx.Result.Warnings.Add(PzText.Get("Mfg_AutoReleaseSubmittedFmt", woId));
                    continue;
                }
                if (r.Outcome == WorkOrderReleaseOutcome.ReleasedNoApprovalFlow)
                    ctx.Result.Warnings.Add(PzText.Get("Mfg_ReleaseNoApprovalFlowFmt", woId));
                else if (r.Outcome != WorkOrderReleaseOutcome.Released)
                {
                    ctx.Result.Warnings.Add(PzText.Get("Mfg_AutoReleaseIncompleteFmt", woId, r.Outcome));
                    continue;
                }
                if (r.ShortageProducts.Count > 0)
                    ctx.Result.Warnings.Add(
                        PzText.Get("Mfg_AutoReleaseShortageFmt", woId, string.Join("、", r.ShortageProducts)));
            }
        }

        /// <summary>刪除一次展開的生產批/工單/工序/用料/派工（重跑清除未投產部分用）。</summary>
        private async Task DeleteExpansionAsync(List<string> batchIds, List<WorkOrder> wos, CancellationToken ct)
        {
            var woIds = wos.Select(w => w.WorkOrderId).ToList();

            // 尚未轉委外採購單的委外需求：Restrict FK 指向工單，須先刪除才能刪工單。
            // 已轉單者屬對外承諾，EvaluateReRunAsync 已在前面擋下，走不到這裡。
            _db.OutsourceDemands.RemoveRange(
                await _db.OutsourceDemands
                    .Where(d => d.WorkOrderId != null && woIds.Contains(d.WorkOrderId) && d.TransferredQty <= 0)
                    .ToListAsync(ct));
            var dispatchIds = await _db.Dispatches
                .Where(d => woIds.Contains(d.WorkOrderId)).Select(d => d.DispatchId).ToListAsync(ct);

            _db.DispatchMaterials.RemoveRange(
                await _db.DispatchMaterials.Where(m => dispatchIds.Contains(m.DispatchId)).ToListAsync(ct));
            _db.Dispatches.RemoveRange(
                await _db.Dispatches.Where(d => woIds.Contains(d.WorkOrderId)).ToListAsync(ct));
            _db.WorkOrderMaterials.RemoveRange(
                await _db.WorkOrderMaterials.Where(m => woIds.Contains(m.WorkOrderId)).ToListAsync(ct));
            _db.WorkOrderOperations.RemoveRange(
                await _db.WorkOrderOperations.Where(o => woIds.Contains(o.WorkOrderId)).ToListAsync(ct));
            _db.WorkOrders.RemoveRange(wos);
            _db.ProductionBatches.RemoveRange(
                await _db.ProductionBatches.Where(b => batchIds.Contains(b.BatchId)).ToListAsync(ct));
            await _db.SaveChangesAsync(ct);
        }

        // ── 自製遞迴展開（mfg-domain.md §5.1 RunMPS）─────────────────────────────
        private async Task<WorkOrder> RunMpsAsync(
            RunContext ctx, Product product, decimal qtyWo, decimal grossDemandQty, ProductionBatch batch,
            DateTime requiredDate, string? parentWorkOrderId, int depth, IReadOnlySet<string>? ancestors = null)
        {
            if (depth > MaxBomDepth)
                throw new MrpBomDepthExceededException(product.ProductCode, MaxBomDepth);
            // 祖先鏈循環偵測（mfg-domain.md §3.1(6)）：品號若已在展開鏈中即為循環用料
            if (ancestors != null && ancestors.Contains(product.ProductCode))
                throw new MrpBomCycleException(product.ProductCode);
            var chain = ancestors == null ? new HashSet<string>() : new HashSet<string>(ancestors);
            chain.Add(product.ProductCode);

            Routing? routing = null;
            if (!string.IsNullOrWhiteSpace(product.RoutingId))
            {
                routing = await _db.Routings
                    .Include(r => r.Operations).ThenInclude(op => op.Boms)
                    .FirstOrDefaultAsync(r => r.RoutingId == product.RoutingId && r.IsActive
                        && (r.EffectiveFrom == null || r.EffectiveFrom <= DateOnly.FromDateTime(ctx.Today))
                        && (r.EffectiveTo == null || r.EffectiveTo >= DateOnly.FromDateTime(ctx.Today)), ctx.Ct);
            }

            // 累積良率後綴乘積（mfg-domain.md §3.1(7)）：良率全 100% 時結果與舊行為完全相同
            var ops = routing?.Operations.OrderBy(o => o.OperationSeq).ToList()
                      ?? new List<RoutingOperation>();
            var cumYield = ComputeCumulativeYield(ops);
            // 投產量 = 需求量 ÷ 全線累積良率（第一道工序起算）
            var qtyInput = ops.Count > 0 ? qtyWo / cumYield[0] : qtyWo;

            var leadDays = routing != null ? EstimateLeadDays(routing, qtyInput) : 0;
            var bufferDays = routing?.BufferDays ?? 0;
            var batchSize = (routing != null && routing.BatchSize > 0) ? routing.BatchSize : 1m;

            // 逐工序倒推（mfg-domain.md §5.1）：優先用工作日曆，自需求日往回**逐道工序**
            // 倒推工序級計畫開工/完工日（含 Move/QueueTime；無限產能，不看機台負荷）。
            // 廠別無日曆設定或未注入服務時退回 24h/天粗估（此時各工序共用工單級日期）。
            var opMinutes = new decimal[ops.Count];
            for (int i = 0; i < ops.Count; i++)
                opMinutes[i] = OpMinutes(ops[i], qtyWo / cumYield[i]);   // 工序投產量 = 需求量 ÷ 累積良率

            var sched = await ComputeOpScheduleByCalendarAsync(ctx, batch, opMinutes, requiredDate, bufferDays);
            DateTime dtStart, plannedEnd;
            DateTime[] opStart, opEnd;
            if (sched != null)
            {
                (opStart, opEnd, dtStart, plannedEnd) = sched.Value;
            }
            else
            {
                // 退回工單級日數版：各工序共用同一組工單日期（無日曆時無法逐工序細分）
                (dtStart, plannedEnd) = BackwardSchedule(ctx, batch, requiredDate, bufferDays + leadDays);
                opStart = new DateTime[ops.Count];
                opEnd = new DateTime[ops.Count];
                for (int i = 0; i < ops.Count; i++) { opStart[i] = dtStart; opEnd[i] = plannedEnd; }
            }

            var woId = NextWorkOrderId(ctx, batch.BatchId);
            var wo = new WorkOrder
            {
                WorkOrderId = woId,
                ProductionBatchId = batch.BatchId,
                ProductCode = product.ProductCode,
                RoutingId = product.RoutingId ?? string.Empty,
                Quantity = qtyInput,
                // §24.2 / §24.8：毛需求 + 來源需求列快照，供投產（Release）net-at-release 重算錨定
                GrossDemandQty = grossDemandQty,
                SourceMrpRunId = batch.MrpRunId,
                SourceMrpRunLineSeq = batch.MrpRunLineSeq,
                ParentWorkOrderId = parentWorkOrderId,
                Status = WorkOrderStatus.Planned,
                PlannedStartDate = dtStart,
                PlannedEndDate = plannedEnd,
                PlantId = ctx.PlantId
            };
            _db.WorkOrders.Add(wo);
            ctx.Result.WorkOrderCount++;

            if (routing == null)
            {
                ctx.Result.Warnings.Add(
                    PzText.Get("Mfg_WoHeaderWithoutOpsFmt", product.ProductCode, product.RoutingId, woId));
                return wo;
            }

            string? prevDispatchId = null;
            for (int opIdx = 0; opIdx < ops.Count; opIdx++)
            {
                var op = ops[opIdx];
                // 途程工序委外已停用（mfg-domain.md §19.7）：日常委外改建半成品＋委外工單，臨時外包用派工「轉委外」。
                // 途程上殘留的委外勾選一律當廠內工序展開——不產生委外派工、也不產生委外建議。
                var opOutsource = false;
                // 本工序派工量 = 需求量 ÷ 本工序起算之累積良率（mfg-domain.md §3.1(7)）
                var qtyOp = qtyWo / cumYield[opIdx];
                _db.WorkOrderOperations.Add(new WorkOrderOperation
                {
                    WorkOrderId = woId,
                    OperationSeq = op.OperationSeq,
                    OperationName = op.OperationName,
                    WorkCenterId = op.WorkCenterId,
                    SetupTime = op.TM_SETUP,
                    UnitTime = op.TM_UNIT,
                    TimeUnit = op.TimeUnit,
                    SetupTimeUnit = op.SetupTimeUnit,
                    MoveTime = op.MoveTime,
                    QueueTime = op.QueueTime,
                    BatchMode = op.CD_BOR,
                    BatchQty = op.QT_BOR,
                    IsOutsource = opOutsource,
                    RequiresFpi = op.RequiresFpi,
                    RequiresInspection = op.RequiresInspection
                });

                var dispatchId = $"{woId}-{op.OperationSeq}";
                _db.Dispatches.Add(new Dispatch
                {
                    DispatchId = dispatchId,
                    WorkOrderId = woId,
                    OperationSeq = op.OperationSeq,
                    Quantity = qtyOp,
                    QtyDone = 0,
                    Status = DispatchStatus.Pending,
                    PrevDispatchId = prevDispatchId,   // 順序鏈結：前一道工序的派工
                    IsOutsource = opOutsource,
                    MachineCode = null,
                    ToolingCode = op.ToolingCode,      // §23.3：自 RoutingOperation 帶入
                    PlannedStartDate = opStart[opIdx], // 逐工序倒推（mfg-domain.md §5.1）
                    PlannedEndDate = opEnd[opIdx],
                    PlantId = ctx.PlantId
                });
                ctx.Result.DispatchCount++;
                prevDispatchId = dispatchId;

                // 工序委外時收集送外料，於工序展開後產生委外建議（mfg-domain.md §4.1）
                List<MrpOutsourceMaterial>? opSendMaterials = opOutsource ? new() : null;

                foreach (var bom in op.Boms.OrderBy(b => b.BomSeq))
                {
                    // 需求量 = 工序派工量 × QT_M / 批量，再套下角率／損耗率換算投入量
                    // （mfg-domain.md §3.1(7)/§5.1；RoutingBom.ApplyLossRates）
                    var reqQty = CeilToUom(
                        RoutingBom.ApplyLossRates(qtyOp * bom.Quantity / batchSize, bom.OffcutRate, bom.ScrapRate),
                        bom.UomCode, ctx.UomDecimals);
                    opSendMaterials?.Add(new MrpOutsourceMaterial(bom.ProductCode, reqQty, bom.UomCode));

                    var matProduct = await _db.Products.FirstOrDefaultAsync(p => p.ProductCode == bom.ProductCode, ctx.Ct);
                    var matHasRouting = !string.IsNullOrWhiteSpace(matProduct?.RoutingId);
                    var matMode = ParseMode(matProduct?.MrpMode, matHasRouting);

                    // Fstock：物料層先試扣庫存（扣安全庫存緩衝，不下探安全存量）
                    var matTaken = await ReserveAvailableAsync(ctx, bom.ProductCode, reqQty, matProduct?.SafetyStock ?? 0m,
                        ReservationSourceType.WorkOrderMaterial, woId, op.OperationSeq, requiredDate);
                    var matRemain = reqQty - matTaken;

                    string? childWoId = null;
                    if (matRemain > 0)
                    {
                        switch (matMode)
                        {
                            case MrpMode.Make:
                                // 下階半成品：共用同一生產批；交期 = **消耗它那道工序**的計畫開工日
                                // （庫出浮動：被後段工序用到的料交期較晚，須先完工；mfg-domain.md §5.1）
                                // 批量規則套用在「這一階自己要建立的供給量」，不是把父階批量化後的量往下傳
                                var childQty = ApplyLotSizing(matRemain, matProduct!, ctx.LotPolicy);
                                var childWo = await RunMpsAsync(ctx, matProduct!, childQty, reqQty, batch,
                                    opStart[opIdx], woId, depth + 1, chain);
                                childWoId = childWo.WorkOrderId;
                                break;
                            case MrpMode.Outsource:
                                var osQty2 = ApplyLotSizing(matRemain, matProduct!, ctx.LotPolicy);
                                var osWo = await RunJAsync(ctx, matProduct!, osQty2, reqQty, batch,
                                    opStart[opIdx], woId, depth + 1, chain);
                                childWoId = osWo.WorkOrderId;
                                break;
                            case MrpMode.Purchase:
                                var pqQty2 = ApplyLotSizing(matRemain, matProduct!, ctx.LotPolicy);
                                ctx.Result.PurchaseDemands.Add(new MrpDemandSignal(
                                    bom.ProductCode, pqQty2, requiredDate, "BOM 用料採購(P)"));
                                break;
                            case MrpMode.StockOnly:
                                ctx.Result.Shortages.Add(new MrpDemandSignal(
                                    bom.ProductCode, matRemain, requiredDate, "BOM 用料僅庫存(Q)但不足"));
                                break;
                        }
                    }

                    _db.WorkOrderMaterials.Add(new WorkOrderMaterial
                    {
                        WorkOrderId      = woId,
                        OperationSeq     = op.OperationSeq,
                        BomSeq           = bom.BomSeq,
                        ProductCode      = bom.ProductCode,
                        RequiredQty      = reqQty,
                        UomCode          = bom.UomCode,
                        ChildWorkOrderId = childWoId,
                        SourceMode       = matMode,
                        UnitCost         = matProduct?.StandardCost ?? 0m,  // 成本快照（mfg-domain.md §16.2）
                        // Phase F 發料方式快照（mfg-domain.md §17.3，Fallback：RoutingBom → 預設 Push）
                        IssueMethod      = bom.IssueMethod ?? MaterialIssueMethod.Push,
                        BackflushTiming  = bom.BackflushTiming,
                    });

                    _db.DispatchMaterials.Add(new DispatchMaterial
                    {
                        DispatchId = dispatchId,
                        BomSeq = bom.BomSeq,
                        ProductCode = bom.ProductCode,
                        RequiredQty = reqQty,
                        IssuedQty = 0,
                        UomCode = bom.UomCode
                    });
                }

                // 工序委外 → 產生委外建議（送外料 + 收回品；mfg-domain.md §4.1）
                if (opOutsource)
                {
                    ctx.Result.OutsourceDemands.Add(new MrpOutsourceDemand(
                        woId, op.OperationSeq, product.ProductCode, qtyOp, requiredDate,
                        $"工序委外（{op.OperationName ?? ("工序" + op.OperationSeq)}），待建委外採購單",
                        opSendMaterials ?? new List<MrpOutsourceMaterial>()));
                }
            }

            return wo;
        }

        // ── 委外展開（mfg-domain.md §5.1 RunJ / §19.6）──────────────────────────
        private async Task<WorkOrder> RunJAsync(
            RunContext ctx, Product product, decimal qty, decimal grossDemandQty, ProductionBatch batch,
            DateTime requiredDate, string? parentWorkOrderId, int depth, IReadOnlySet<string>? ancestors = null)
        {
            if (depth > MaxBomDepth)
                throw new MrpBomDepthExceededException(product.ProductCode, MaxBomDepth);
            // 祖先鏈循環偵測（mfg-domain.md §3.1(6)）
            if (ancestors != null && ancestors.Contains(product.ProductCode))
                throw new MrpBomCycleException(product.ProductCode);

            // 委外前置期倒推（以品號一般前置期天數為單位，無機台日曆）
            var (dtStart, plannedEnd) = BackwardSchedule(ctx, batch, requiredDate, product.LeadTimeDays);

            var woId = NextWorkOrderId(ctx, batch.BatchId);
            var wo = new WorkOrder
            {
                WorkOrderId = woId,
                ProductionBatchId = batch.BatchId,
                ProductCode = product.ProductCode,
                RoutingId = product.RoutingId ?? string.Empty,
                Quantity = qty,
                // §24.2 / §24.8：毛需求 + 來源需求列快照
                GrossDemandQty = grossDemandQty,
                SourceMrpRunId = batch.MrpRunId,
                SourceMrpRunLineSeq = batch.MrpRunLineSeq,
                ParentWorkOrderId = parentWorkOrderId,
                Status = WorkOrderStatus.Planned,
                PlannedStartDate = dtStart,
                PlannedEndDate = plannedEnd,
                // 委外（mfg-domain.md §19.2）：廠商/採購單於釋出建立採購單時填入
                IsOutsource = true,
                OutsourceExpectedDeliveryDate = DateOnly.FromDateTime(plannedEnd),
                PlantId = ctx.PlantId
            };
            _db.WorkOrders.Add(wo);
            ctx.Result.WorkOrderCount++;

            // 委外單一工序（無機台），派工 IsOutsource=true、MachineCode=null
            _db.WorkOrderOperations.Add(new WorkOrderOperation
            {
                WorkOrderId = woId,
                OperationSeq = 1,
                OperationName = "委外加工",
                WorkCenterId = string.Empty,
                SetupTime = 0,
                UnitTime = 0,
                TimeUnit = OperationTimeUnit.Minute,
                BatchMode = OperationMode.WholeOrder,
                BatchQty = 1,
                IsOutsource = true,
                RequiresFpi = false,
                RequiresInspection = false
            });

            var dispatchId = $"{woId}-1";
            _db.Dispatches.Add(new Dispatch
            {
                DispatchId = dispatchId,
                WorkOrderId = woId,
                OperationSeq = 1,
                Quantity = qty,
                QtyDone = 0,
                Status = DispatchStatus.Pending,
                PrevDispatchId = null,
                IsOutsource = true,
                MachineCode = null,
                PlannedStartDate = dtStart,    // 委外單一工序：計畫日 = 工單倒推日
                PlannedEndDate = plannedEnd,
                PlantId = ctx.PlantId
            });
            ctx.Result.DispatchCount++;

            // 若委外品有途程 BOM（需發給廠商的料），展開於此單一工序（不再遞迴下探，
            // 深層委外用料採購於委外完整流程切片 §19 再細化）
            var sendMaterials = new List<MrpOutsourceMaterial>();
            if (!string.IsNullOrWhiteSpace(product.RoutingId))
            {
                var routing = await _db.Routings
                    .Include(r => r.Operations).ThenInclude(op => op.Boms)
                    .FirstOrDefaultAsync(r => r.RoutingId == product.RoutingId && r.IsActive
                        && (r.EffectiveFrom == null || r.EffectiveFrom <= DateOnly.FromDateTime(ctx.Today))
                        && (r.EffectiveTo == null || r.EffectiveTo >= DateOnly.FromDateTime(ctx.Today)), ctx.Ct);
                if (routing != null)
                {
                    var batchSize = routing.BatchSize > 0 ? routing.BatchSize : 1m;
                    int bomSeq = 0;
                    foreach (var op in routing.Operations.OrderBy(o => o.OperationSeq))
                    {
                        foreach (var bom in op.Boms.OrderBy(b => b.BomSeq))
                        {
                            bomSeq++;
                            var reqQty = CeilToUom(
                                RoutingBom.ApplyLossRates(qty * bom.Quantity / batchSize, bom.OffcutRate, bom.ScrapRate),
                                bom.UomCode, ctx.UomDecimals);
                            sendMaterials.Add(new MrpOutsourceMaterial(bom.ProductCode, reqQty, bom.UomCode));
                            var matProduct = await _db.Products.FirstOrDefaultAsync(p => p.ProductCode == bom.ProductCode, ctx.Ct);
                            _db.WorkOrderMaterials.Add(new WorkOrderMaterial
                            {
                                WorkOrderId     = woId,
                                OperationSeq    = 1,
                                BomSeq          = bomSeq,
                                ProductCode     = bom.ProductCode,
                                RequiredQty     = reqQty,
                                UomCode         = bom.UomCode,
                                SourceMode      = MrpMode.Outsource,
                                UnitCost        = matProduct?.StandardCost ?? 0m,  // 成本快照
                                IssueMethod     = bom.IssueMethod ?? MaterialIssueMethod.Push,
                                BackflushTiming = bom.BackflushTiming,
                            });
                            _db.DispatchMaterials.Add(new DispatchMaterial
                            {
                                DispatchId = dispatchId,
                                BomSeq = bomSeq,
                                ProductCode = bom.ProductCode,
                                RequiredQty = reqQty,
                                IssuedQty = 0,
                                UomCode = bom.UomCode
                            });
                        }
                    }
                }
            }

            // 整品委外(S) 與工序委外走同一委外建議通道（mfg-domain.md §4.1），OperationSeq=1（單一委外工序）
            ctx.Result.OutsourceDemands.Add(new MrpOutsourceDemand(
                woId, 1, product.ProductCode, qty, requiredDate,
                "整品委外加工(S)，待建委外採購單", sendMaterials));
            return wo;
        }

        // ── 可行性預檢（mfg-domain.md §11 Phase B 落地）──────────────────────────
        /// <summary>
        /// 展開前唯讀走訪品號的途程/BOM 樹，收集阻擋性錯誤（自製缺有效途程、途程無工序、
        /// 用料品號不存在、用料缺單位換算、循環用料、BOM 超深）。委外品無途程不視為錯誤
        /// （RunJ 以單一委外工序處理）；採購/僅庫存為葉節點不展開、不檢。
        /// </summary>
        private async Task ValidateLineAsync(
            Product product, List<string> errors, HashSet<string> ancestors, int depth, CancellationToken ct)
        {
            if (depth > MaxBomDepth)
            {
                errors.Add(PzText.Get("Mfg_BomTooDeepFmt", product.ProductCode, MaxBomDepth));
                return;
            }
            if (ancestors.Contains(product.ProductCode))
            {
                errors.Add(PzText.Get("Mfg_BomCycleFmt", product.ProductCode));
                return;
            }

            var hasRouting = !string.IsNullOrWhiteSpace(product.RoutingId);
            var mode = ParseMode(product.MrpMode, hasRouting);

            // 採購 / 僅庫存：葉節點，不展開不檢
            if (mode is MrpMode.Purchase or MrpMode.StockOnly)
                return;

            Routing? routing = null;
            if (hasRouting)
            {
                var todayDate = DateOnly.FromDateTime(_dt.Now);
                routing = await _db.Routings
                    .Include(r => r.Operations).ThenInclude(op => op.Boms)
                    .FirstOrDefaultAsync(r => r.RoutingId == product.RoutingId && r.IsActive
                        && (r.EffectiveFrom == null || r.EffectiveFrom <= todayDate)
                        && (r.EffectiveTo == null || r.EffectiveTo >= todayDate), ct);
            }

            if (mode == MrpMode.Make)
            {
                if (routing == null)
                {
                    errors.Add(PzText.Get("Mfg_NoValidRoutingFmt", product.ProductCode, product.RoutingId));
                    return;
                }
                if (!routing.Operations.Any())
                {
                    errors.Add(PzText.Get("Mfg_RoutingNoOpsFmt", product.ProductCode, routing.RoutingId));
                    return;
                }
            }

            // 委外無途程 → 不再下探（RunJ 單一委外工序）；有途程則一併檢其送外料
            if (routing == null)
                return;

            var chain = new HashSet<string>(ancestors) { product.ProductCode };

            foreach (var op in routing.Operations)
            foreach (var bom in op.Boms)
            {
                var matProduct = await _db.Products.FirstOrDefaultAsync(p => p.ProductCode == bom.ProductCode, ct);
                if (matProduct == null)
                {
                    errors.Add(PzText.Get("Mfg_OpMaterialMissingFmt", product.ProductCode, op.OperationSeq, bom.ProductCode));
                    continue;
                }

                // 發料單位與用料品號庫存單位不同時，須有單位換算（直接查 UomConversions；GetConversionRate 查無回 1 無法判斷缺漏）
                if (!string.IsNullOrWhiteSpace(matProduct.StockUom) &&
                    !string.IsNullOrWhiteSpace(bom.UomCode) &&
                    !string.Equals(bom.UomCode.Trim(), matProduct.StockUom.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !await HasUomConversionAsync(bom.UomCode, matProduct.StockUom, ct))
                {
                    errors.Add(PzText.Get("Mfg_MaterialUomConversionMissingFmt", bom.ProductCode, bom.UomCode, matProduct.StockUom));
                }

                var matMode = ParseMode(matProduct.MrpMode, !string.IsNullOrWhiteSpace(matProduct.RoutingId));
                if (matMode is MrpMode.Make or MrpMode.Outsource)
                    await ValidateLineAsync(matProduct, errors, chain, depth + 1, ct);
            }
        }

        /// <summary>兩單位間是否存在換算列（任一方向）；同單位視為存在。</summary>
        private async Task<bool> HasUomConversionAsync(string fromUom, string toUom, CancellationToken ct)
        {
            if (string.Equals(fromUom.Trim(), toUom.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            return await _db.UomConversions.AsNoTracking().AnyAsync(c =>
                (c.FromUomCode == fromUom && c.ToUomCode == toUom) ||
                (c.FromUomCode == toUom && c.ToUomCode == fromUom), ct);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private async Task<ProductionBatch> CreateBatchAsync(
            RunContext ctx, MrpRun run, MrpRunLine line, decimal qty, CancellationToken ct)
        {
            var batchId = await _docNumber.NextAsync(BatchDocType, ctx.PlantId, DateOnly.FromDateTime(ctx.Today), ct);
            var batch = new ProductionBatch
            {
                BatchId = batchId,
                MrpRunId = run.MrpRunId,
                MrpRunLineSeq = line.LineSeq,
                ProductCode = line.ProductCode,
                Quantity = qty,
                Priority = line.Priority,   // 從需求列帶入（scheduling-spec-review 第一階段；預設 100）
                PlannedEndDate = line.RequiredDate,
                Status = ProductionBatchStatus.Planned,
                PlantId = ctx.PlantId
            };
            _db.ProductionBatches.Add(batch);
            // 先持久化批號，後續 WorkOrder.ProductionBatchId FK 才有對應主鍵
            await _db.SaveChangesAsync(ct);

            line.ProductionBatchId = batchId;
            ctx.Result.CreatedBatchIds.Add(batchId);
            return batch;
        }

        /// <summary>
        /// 單一工序淨工時（分鐘）＝整備＋單件×投產量＋移轉 MoveTime＋等候 QueueTime，
        /// 依工序 TimeUnit 換算為分鐘（§3.1(8)）。
        /// </summary>
        private static decimal OpMinutes(RoutingOperation op, decimal qty)
        {
            // 準備工時單位（SetupTimeUnit）可與單件/移轉/等候（TimeUnit）不同，需分別換算。
            return OperationTimeUnitExtensions.ToMinutes(op.TM_SETUP, op.SetupTimeUnit)
                 + OperationTimeUnitExtensions.ToMinutes(op.TM_UNIT * qty + op.MoveTime + op.QueueTime, op.TimeUnit);
        }

        /// <summary>
        /// 前置期淨工時（分鐘）＝Σ每工序（OpMinutes）。與排程引擎共用同一時間單位換算（§3.1(8)）。
        /// </summary>
        public static decimal EstimateLeadMinutes(Routing routing, decimal qty)
            => routing.Operations.Sum(op => OpMinutes(op, qty));

        /// <summary>
        /// 逐工序倒推（mfg-domain.md §5.1）：以廠別工作日曆，自需求日往回**逐道工序**算出
        /// 工序級 (start, end)（後製程先排，最末工序 end = 需求日，前一道 end = 後一道 start）。
        /// 無限產能——不看機台負荷，純消耗工作日曆工時。回傳每工序 start/end 陣列與工單級
        /// (woStart=首序 start−緩衝, woEnd)。過今日 → 改自今日正推並標 IsPredictedLate。
        /// 回傳 null 表示無法使用日曆（未注入服務／無工時／需求日當週完全無日曆／涵蓋不足）→ 呼叫端退回日數版。
        /// </summary>
        private async Task<(DateTime[] opStart, DateTime[] opEnd, DateTime woStart, DateTime woEnd)?>
            ComputeOpScheduleByCalendarAsync(
                RunContext ctx, ProductionBatch batch, decimal[] opMinutes, DateTime requiredDate, int bufferDays)
        {
            var n = opMinutes.Length;
            if (_calendar == null || n == 0 || opMinutes.Sum() <= 0) return null;

            // 廠級時段（機台/工作中心尚未指定）；逐日快取
            var cache = new Dictionary<DateOnly, IReadOnlyList<(int Start, int End)>>();
            async Task<IReadOnlyList<(int Start, int End)>> LoadAsync(DateOnly d)
            {
                if (!cache.TryGetValue(d, out var v))
                    cache[d] = v = await _calendar.GetSegmentsAsync(null, null, ctx.PlantId, d);
                return v;
            }

            // 探測：需求日往前 7 天內完全無時段 → 視為廠別未設日曆，退回日數版
            var probeDate = DateOnly.FromDateTime(requiredDate);
            bool anySegment = false;
            for (int i = 0; i < 7 && !anySegment; i++)
                anySegment = (await LoadAsync(probeDate.AddDays(-i))).Count > 0;
            if (!anySegment) return null;

            IReadOnlyList<(int Start, int End)> GetSegmentsSync(DateOnly d)
            {
                if (cache.TryGetValue(d, out var v)) return v;
                var segs = _calendar.GetSegmentsAsync(null, null, ctx.PlantId, d)
                               .GetAwaiter().GetResult();
                cache[d] = segs;
                return segs;
            }

            var opStart = new DateTime[n];
            var opEnd = new DateTime[n];

            // 逐工序倒推（後製程先）：cursor 由需求日往回滑
            try
            {
                var cursor = requiredDate;
                for (int i = n - 1; i >= 0; i--)
                {
                    opEnd[i] = cursor;
                    opStart[i] = opMinutes[i] > 0
                        ? WorkingTimeWalker.SubtractWorkingMinutes(cursor, opMinutes[i], GetSegmentsSync)
                        : cursor;
                    cursor = opStart[i];
                }
            }
            catch (InvalidOperationException)
            {
                return null;   // 日曆涵蓋不足（365 天內倒推不完）→ 退回日數版
            }

            var woStart = opStart[0].AddDays(-bufferDays);
            var woEnd = requiredDate;

            if (woStart.Date < ctx.Today.Date)
            {
                // 已來不及依需求日倒推 → 自今日**逐工序正推**，估算實際可完成日
                try
                {
                    var cursor = ctx.Today.AddDays(bufferDays);
                    for (int i = 0; i < n; i++)
                    {
                        opStart[i] = cursor;
                        opEnd[i] = opMinutes[i] > 0
                            ? WorkingTimeWalker.AddWorkingMinutes(cursor, opMinutes[i], GetSegmentsSync, downtimes: null, maxDays: 365)
                            : cursor;
                        cursor = opEnd[i];
                    }
                }
                catch (InvalidOperationException)
                {
                    return null;   // 正推也涵蓋不足 → 退回日數版
                }

                woStart = ctx.Today;
                woEnd = opEnd[n - 1];

                if (woEnd.Date > requiredDate.Date)
                {
                    batch.IsPredictedLate = true;
                    if (!ctx.Result.PredictedLateBatchIds.Contains(batch.BatchId))
                        ctx.Result.PredictedLateBatchIds.Add(batch.BatchId);
                }
            }

            return (opStart, opEnd, woStart, woEnd);
        }

        private static (DateTime dtStart, DateTime plannedEnd) BackwardSchedule(
            RunContext ctx, ProductionBatch batch, DateTime requiredDate, int totalLeadDays)
        {
            var dtStart = requiredDate.AddDays(-totalLeadDays);
            var plannedEnd = requiredDate;

            if (dtStart.Date < ctx.Today.Date)
            {
                dtStart = ctx.Today;
                plannedEnd = ctx.Today.AddDays(totalLeadDays);
                if (plannedEnd.Date > requiredDate.Date)
                {
                    batch.IsPredictedLate = true;
                    if (!ctx.Result.PredictedLateBatchIds.Contains(batch.BatchId))
                        ctx.Result.PredictedLateBatchIds.Add(batch.BatchId);
                }
            }

            return (dtStart, plannedEnd);
        }

        /// <summary>
        /// 稽核修正：同一次 MRP 展開內，多筆需求列不得重複認領同一批在途量（PO 待交貨／GR 待上架）。
        /// 這兩個來源都是即時對 PUR/INV 表重新查詢（不像 on-hand 有 InventoryReservation 記錄可扣減），
        /// 故改以 <see cref="RunContext.ClaimedScheduledReceipts"/> / <see cref="RunContext.ClaimedPendingPutAway"/>
        /// 在本次展開的記憶體內追蹤「本次已認領量」，每次查詢先扣掉已認領量再计算可再認領的餘額。
        /// GS 待下架（pendingPick）是每次查詢皆一致套用的「反向調整」而非可被搶完的供給池，維持原樣即時重算。
        /// 僅實際取自 on-hand 的部分（fromOnHand）建立 Category=OnHand 的 InventoryReservation——
        /// 避免把在途/待上架的認領誤記為 on-hand 消耗，污染後續需求列對 <see cref="IInventoryReservationService.GetPlantAvailableQtyAsync"/>
        /// 的判斷（該方法只應反映實體在手庫存的消耗）。
        /// </summary>
        private async Task<decimal> ReserveAvailableAsync(
            RunContext ctx, string productCode, decimal demandQty, decimal safetyStock,
            ReservationSourceType sourceType, string sourceDocId, int? sourceLineSeq, DateTime requiredDate)
        {
            var available = await _reservations.GetPlantAvailableQtyAsync(ctx.PlantId, productCode, ctx.Ct);

            // §21.3：加入 PO Scheduled Receipts（已核准 PO 在 requiredDate 前的待交量），扣除本次展開已認領量。
            // 庫存單位（StockQty − ReceivedStockQty）：要與 GetPlantAvailableQtyAsync 相加（2026-10-27，原取採購單位）
            var scheduledReceiptsRaw = await _db.PurchaseOrderLines
                .AsNoTracking()
                .Where(l => l.ProductCode == productCode
                         && !l.IsClosed
                         && l.ReceivedStockQty < l.StockQty
                         && l.DeliveryDate.HasValue
                         && l.DeliveryDate.Value <= DateOnly.FromDateTime(requiredDate))
                .Join(_db.PurchaseOrders.AsNoTracking()
                    .Where(po => po.PlantId == ctx.PlantId
                              && po.ApprovalStatus == DocumentApprovalStatus.Approved),
                    line => line.PurchaseOrderId,
                    po => po.PurchaseOrderId,
                    (line, po) => line.StockQty - line.ReceivedStockQty)
                .SumAsync(q => (decimal?)q, ctx.Ct) ?? 0m;
            ctx.ClaimedScheduledReceipts.TryGetValue(productCode, out var claimedSr);
            var scheduledReceipts = Math.Max(scheduledReceiptsRaw - claimedSr, 0m);

            // 全模式（inv_simplified_mode=false）：QtyOnHand 不含待上架/待下架在途量，需補足差額。
            // 簡化模式下入/出庫核准即過帳，QtyOnHand 已即時反映，無需調整。
            var netPendingPutAway = 0m;
            var pendingPick = 0m;
            if (!ctx.IsSimplifiedMode)
            {
                // + 進貨待上架（GR 已核准未過帳，庫存尚未增加），扣除本次展開已認領量
                var pendingPutAwayRaw = await _db.GoodsReceipts
                    .AsNoTracking()
                    .Where(gr => gr.PlantId == ctx.PlantId
                              && gr.ApprovalStatus == DocumentApprovalStatus.Approved
                              && gr.Status == DocStatus.Confirmed)
                    .Join(_db.Set<GoodsReceiptLine>().AsNoTracking()
                              .Where(l => l.ProductCode == productCode),
                          gr => gr.GoodsReceiptId,
                          l  => l.GoodsReceiptId,
                          (gr, l) => l.StockQty - l.QtyPutAway)
                    .SumAsync(q => (decimal?)q, ctx.Ct) ?? 0m;
                ctx.ClaimedPendingPutAway.TryGetValue(productCode, out var claimedPutAway);
                netPendingPutAway = Math.Max(pendingPutAwayRaw - claimedPutAway, 0m);

                // − 出貨待下架（GS 已核准未過帳，庫存尚未減少）：非可被搶完的供給池，
                // 每次查詢皆一致套用同一即時反向調整量，不需認領追蹤。
                pendingPick = await _db.GoodsShipments
                    .AsNoTracking()
                    .Where(gs => gs.PlantId == ctx.PlantId
                              && gs.ApprovalStatus == DocumentApprovalStatus.Approved
                              && gs.Status == DocStatus.Confirmed)
                    .Join(_db.GoodsShipmentLines.AsNoTracking()
                              .Where(l => l.ProductCode == productCode),
                          gs => gs.GoodsShipmentId,
                          l  => l.GoodsShipmentId,
                          (gs, l) => l.StockQty - l.QtyPutAway)
                    .SumAsync(q => (decimal?)q, ctx.Ct) ?? 0m;
            }

            var inTransitAdjustment = netPendingPutAway - pendingPick;
            // 安全庫存作為淨算下限緩衝：可取用量不得吃到安全庫存以下（mfg-domain.md 排程 8 點目標）
            var pool = Math.Max(available + scheduledReceipts + inTransitAdjustment - safetyStock, 0m);
            var take = Math.Min(pool, demandQty);
            if (take <= 0)
                return take;

            // 依序從 on-hand → PO 在途 → GR 待上架 分配本次取用量，各自更新對應的認領追蹤。
            // on-hand 這一桶的上限須先扣安全庫存——否則在途量夠大時，take 會超過
            // (available - safetyStock)，導致 fromOnHand 把「實體在手庫存」吃到安全庫存以下
            // （safetyStock 原本只從加總 pool 扣除，不會限制 on-hand 這一桶本身）。
            var onHandUsable = Math.Max(available - safetyStock, 0m);
            var fromOnHand = Math.Min(onHandUsable, take);
            var remaining = take - fromOnHand;

            var fromScheduled = Math.Min(scheduledReceipts, remaining);
            remaining -= fromScheduled;
            ctx.ClaimedScheduledReceipts[productCode] = claimedSr + fromScheduled;

            var fromPutAway = Math.Min(Math.Max(netPendingPutAway, 0m), remaining);
            ctx.ClaimedPendingPutAway.TryGetValue(productCode, out var claimedPutAway2);
            ctx.ClaimedPendingPutAway[productCode] = claimedPutAway2 + fromPutAway;

            // 只把實際取自 on-hand 的部分記為 Category=OnHand 預留；在途/待上架部分僅是規劃訊號，
            // 不消耗實體在手庫存，不應計入 GetPlantAvailableQtyAsync 的 on-hand 扣減。
            if (fromOnHand > 0)
                await _reservations.ReserveAsync(ctx.PlantId, productCode, fromOnHand,
                    sourceType, sourceDocId, sourceLineSeq, requiredDate, ctx.Ct);
            return take;
        }

        private static string NextWorkOrderId(RunContext ctx, string batchId)
        {
            ctx.WoSeqByBatch.TryGetValue(batchId, out var seq);
            seq++;
            ctx.WoSeqByBatch[batchId] = seq;
            return $"{batchId}_{seq:D2}";   // NO_WO = ID_PLN + "_" + 流水號（mfg-domain.md §5.1）
        }

        /// <summary>
        /// 估算製造前置天數（無限產能粗估）：Σ(整備 + 單位工時×數量) / 每日工時上限，向上取整。
        /// TM_SETUP / TM_UNIT 依各工序 TimeUnit（秒/分/時）解讀，換算為小時後彙總（mfg-domain.md §3.1(3)）。
        /// 精確時間語意（工作日曆）於 Phase C 排程引擎細化。
        /// </summary>
        private static int EstimateLeadDays(Routing routing, decimal qty)
        {
            decimal hours = 0m;
            foreach (var op in routing.Operations)
            {
                // 準備工時單位（SetupTimeUnit）可與單件工時（TimeUnit）不同，分別換算為分鐘再折小時。
                var opMinutes = OperationTimeUnitExtensions.ToMinutes(op.TM_SETUP, op.SetupTimeUnit)
                              + OperationTimeUnitExtensions.ToMinutes(op.TM_UNIT * qty, op.TimeUnit);
                hours += opMinutes / 60m;
            }

            if (hours <= 0m)
                hours = routing.AvgLeadTime; // 後備：途程平均工時（小時）
            if (hours <= 0m)
                return 0;

            var days = (int)Math.Ceiling(hours / DailyWorkHours);
            return days < 0 ? 0 : days;
        }

        /// <summary>
        /// 用料需求量依計量單位小數位無條件進位（mfg-domain.md §5.1）。
        /// 損耗率／下角率換算會算出小數（如 105 × 1.05 = 110.25），但整數計量單位（PCS 0 位）
        /// 不可能發 0.25 片；進位（寧多不少）確保發料量可執行，缺料判定也不會出現假差額。
        /// 查無該 UOM 設定時不進位（維持原值），避免主檔缺漏時竄改需求量。
        /// </summary>
        private static decimal CeilToUom(decimal qty, string? uomCode, IReadOnlyDictionary<string, int> uomDecimals)
        {
            if (string.IsNullOrEmpty(uomCode) || !uomDecimals.TryGetValue(uomCode, out var dp)) return qty;
            if (dp < 0) dp = 0;
            var factor = (decimal)Math.Pow(10, dp);
            return Math.Ceiling(qty * factor) / factor;
        }

        /// <summary>產銷模式字串（Product.MrpMode，CodeEntry: mrp_mode）解析為列舉。</summary>
        private static MrpMode ParseMode(string? code, bool hasRouting)
        {
            if (string.IsNullOrWhiteSpace(code))
                return hasRouting ? MrpMode.Make : MrpMode.Purchase;

            switch (code.Trim().ToUpperInvariant())
            {
                case "Q": case "STOCKONLY": case "STOCK": return MrpMode.StockOnly;
                case "P": case "PURCHASE": return MrpMode.Purchase;
                case "S": case "OUTSOURCE": return MrpMode.Outsource;
                case "M": case "MAKE": return MrpMode.Make;
                default:
                    if (int.TryParse(code, out var n) && Enum.IsDefined(typeof(MrpMode), n))
                        return (MrpMode)n;
                    return hasRouting ? MrpMode.Make : MrpMode.Purchase;
            }
        }

        /// <summary>
        /// 計算各工序的累積良率後綴乘積（mfg-domain.md §3.1(7)）。
        /// cumYield[i] = ops[i].YieldRate/100 × ops[i+1].YieldRate/100 × … × ops[n-1].YieldRate/100
        /// YieldRate ≤ 0 視為 100%（防呆）。良率全 100% 時陣列全為 1.0，行為與舊版一致。
        /// </summary>
        private static decimal[] ComputeCumulativeYield(List<RoutingOperation> ops)
        {
            var result = new decimal[ops.Count];
            if (ops.Count == 0) return result;
            result[ops.Count - 1] = Math.Max(ops[ops.Count - 1].YieldRate, 0.01m) / 100m;
            for (int i = ops.Count - 2; i >= 0; i--)
                result[i] = result[i + 1] * (Math.Max(ops[i].YieldRate, 0.01m) / 100m);
            return result;
        }

        /// <summary>
        /// 批量政策（mfg-domain.md §B）：依系統參數 <c>mfg_lot_policy</c> 調整淨需求為實際訂購量。
        /// LotForLot（預設）：依淨需求原量生產。
        /// MinMultiple：至少 MinLotSize，且為 LotMultiple 的倍數。
        /// </summary>
        private static decimal ApplyLotSizing(decimal netRequirement, Product product, string lotPolicy)
        {
            if (!string.Equals(lotPolicy, "MinMultiple", StringComparison.OrdinalIgnoreCase))
                return netRequirement; // LotForLot 或未知值：原量

            var minLot  = product.MinLotSize  is > 0 ? product.MinLotSize.Value  : 1m;
            var multiple = product.LotMultiple is > 0 ? product.LotMultiple.Value : 1m;

            var qty = Math.Max(netRequirement, minLot);
            if (multiple > 1m)
                qty = Math.Ceiling(qty / multiple) * multiple;

            return qty;
        }

        private sealed class RunContext
        {
            public string PlantId = string.Empty;
            public DateTime Today;
            public MrpRunResult Result = null!;
            public CancellationToken Ct;
            public bool IsSimplifiedMode = true;
            public string LotPolicy = "LotForLot";
            public Dictionary<string, int> WoSeqByBatch { get; } = new();

            /// <summary>本次展開內，各品號已認領的 PO 在途量（避免多筆需求重複認領同一批在途 PO）。</summary>
            public Dictionary<string, decimal> ClaimedScheduledReceipts { get; } = new();

            /// <summary>本次展開內，各品號已認領的 GR 待上架量（避免多筆需求重複認領同一批待上架庫存）。</summary>
            public Dictionary<string, decimal> ClaimedPendingPutAway { get; } = new();

            /// <summary>計量單位小數位（UomCode → DecimalPlaces），供用料需求量依 UOM 進位。</summary>
            public Dictionary<string, int> UomDecimals { get; set; } = new();
        }
    }
}
