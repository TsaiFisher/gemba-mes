using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 全量排程引擎（mfg-domain.md §7）。
    /// 對所有 Released 工單的 Pending 派工，依排序規則分配機台與時段，
    /// 寫入 Dispatch.ScheduledStart / ScheduledEnd / MachineCode。
    /// </summary>
    public class SchedulingEngine : ISchedulingEngine
    {
        private readonly ApplicationDbContext _db;
        private readonly IPlantParamService _plantParams;

        /// <summary>本次排程要（重新）配置時段的派工狀態（§10-8：Scheduled 釋放重排）。</summary>
        private static readonly DispatchStatus[] ReschedulableStatuses =
            { DispatchStatus.Pending, DispatchStatus.Scheduled };

        /// <summary>已佔用機台、排程不得移動的派工狀態（§10-8：InProgress 家族凍結）。</summary>
        private static readonly DispatchStatus[] FrozenStatuses =
        {
            DispatchStatus.SetupInProgress, DispatchStatus.ReadyToStart,
            DispatchStatus.InProgress, DispatchStatus.OnHold, DispatchStatus.PendingFPI
        };

        public SchedulingEngine(
            ApplicationDbContext db,
            IPlantParamService plantParams)
        {
            _db = db;
            _plantParams = plantParams;
        }

        // ── LoadCalendarContextAsync ────────────────────────────────────────────

        public async Task<CalendarContext> LoadCalendarContextAsync(
            string plantId, DateOnly dateFrom, DateOnly dateTo,
            CancellationToken ct = default)
        {
            // 一次撈回指定區間所有 WorkCalendarSegment（機台 > 工作中心 > 全廠，per-plant）
            var segments = await _db.Set<WorkCalendarSegment>().IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.PlantId == plantId && s.Date >= dateFrom && s.Date <= dateTo)
                .OrderBy(s => s.Date).ThenBy(s => s.Seq)
                .ToListAsync(ct);

            var map = segments
                .GroupBy(s => (s.MachineCode, s.WorkCenterId, s.Date))
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<WorkCalendarSegmentBuilder.Segment>)
                         g.Select(s => new WorkCalendarSegmentBuilder.Segment(s.StartMinute, s.EndMinute))
                          .ToList());

            // 計劃停機（排程期間、廠別內）
            var downtimes = await _db.Set<PlannedDowntime>().IgnoreQueryFilters()
                .AsNoTracking()
                .Where(d => d.PlantId == plantId && d.IsActive
                         && d.EndTime >= dateFrom.ToDateTime(new TimeOnly(0, 0))
                         && d.StartTime <= dateTo.ToDateTime(new TimeOnly(23, 59)))
                .ToListAsync(ct);

            return new CalendarContext(map, downtimes);
        }

        // ── RunAsync ────────────────────────────────────────────────────────────

        public async Task<ScheduleRunResult> RunAsync(
            string plantId, ScheduleRule rule, DateTime today,
            string? runBy = null, ScheduleTrigger trigger = ScheduleTrigger.Manual,
            CancellationToken ct = default, ScheduleRunMode mode = ScheduleRunMode.Full,
            TimeSpan? freezeWindow = null)
        {
            // 同廠互斥：兩次排程同時跑（兩位生管同時按、或日後自動排程撞上手動）會各自讀到同一份
            // 待排清單、各自配置時段後先後寫回，後寫的覆蓋先寫的，甚至與報工撞 xmin 讓整次失敗。
            // 以 PostgreSQL 交易層級 advisory lock 鎖「排程:廠別」：拿不到就是別人正在跑，直接回 Busy；
            // 交易結束（提交或回滾）自動釋放，不會因例外而殘留。InMemory（單元測試）無此機制，直接執行。
            if (!_db.Database.IsNpgsql())
                return await RunCoreAsync(plantId, rule, today, runBy, trigger, mode, freezeWindow, ct);

            var ownTx = _db.Database.CurrentTransaction == null;
            await using var tx = ownTx ? await _db.Database.BeginTransactionAsync(ct) : null;

            var locked = await _db.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtext({"mfg-schedule:" + plantId})) AS \"Value\"")
                .SingleAsync(ct);
            if (!locked) return ScheduleRunResult.BusyResult;

            var result = await RunCoreAsync(plantId, rule, today, runBy, trigger, mode, freezeWindow, ct);
            if (tx != null) await tx.CommitAsync(ct);
            return result;
        }

        // 本方法所有查詢一律 IgnoreQueryFilters，並各自以 plantId 明確過濾：
        //   - 背景自動排程沒有登入使用者，全域過濾是 PlantId == null、CreatedByRoleId == null，會靜默撈到 0 筆；
        //   - 手動排程時資料權限（組織層級）也不該套用——排程是全廠產能配置，看不到的派工照樣佔著機台，
        //     只排自己看得到的那部分會把別人的時段重複預約掉。
        private async Task<ScheduleRunResult> RunCoreAsync(
            string plantId, ScheduleRule rule, DateTime today, string? runBy, ScheduleTrigger trigger,
            ScheduleRunMode mode, TimeSpan? freezeWindow, CancellationToken ct)
        {
            var preserve = mode == ScheduleRunMode.PreserveAssignment;
            // 開始讀資料的時刻：之後發生的異動（含本次執行期間）都算「排程過時」，見 ScheduleFreshness。
            // 系統時鐘回撥時，已存在的異動時間戳可能比「現在」晚；這些這次都讀得到，DataAsOf 取兩者較晚者，
            // 否則剛排完就判定過時（時鐘正常時 latest ≤ now，行為不變）
            var dataAsOf = DateTime.UtcNow;
            if (await ScheduleFreshness.LatestChangeAtAsync(_db, plantId, ct) is { } latestChange && latestChange > dataAsOf)
                dataAsOf = latestChange;
            var todayDate = DateOnly.FromDateTime(today);
            var windowTo  = todayDate.AddDays(ScheduleFreshness.WindowDays);
            var ctx = await LoadCalendarContextAsync(plantId, todayDate, windowTo, ct);
            // 與日曆同時取指紋：執行期間若有人改日曆，下次比對就會判定過時
            var calendarFingerprint = await ScheduleFreshness.ComputeFingerprintAsync(_db, plantId, todayDate, windowTo, ct);

            // 取 Released / InProgress 工單及其派工（含工序）。
            // §10-8：Pending 與 Scheduled 重新配置；InProgress 家族凍結（佔用機台）。
            var workOrders = await _db.WorkOrders.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.PlantId == plantId
                         && (w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress)
                         // §18.2 投產前審核：送審中／被退回的工單不排（判定同 WorkOrderApprovalGate）
                         && w.ApprovalStatus != DocumentApprovalStatus.PendingApproval
                         && w.ApprovalStatus != DocumentApprovalStatus.Rejected)
                .Include(w => w.Operations)
                .ThenInclude(op => op.Dispatches)
                .ToListAsync(ct);

            // 預設規則「優先序」依生產批 Priority 排序。原本只靠 WorkOrder.ProductionBatch 導覽屬性，
            // 但上面的查詢沒有載入它——恆為 null、每批都當 100，排程側欄的「優先序（插單）」對排程
            // 完全沒有作用。另查一份對照表，不用 Include（必要關聯會變 INNER JOIN、改變撈到的工單）。
            var batchPriority = await _db.ProductionBatches.IgnoreQueryFilters().AsNoTracking()
                .Where(b => b.PlantId == plantId)
                .ToDictionaryAsync(b => b.BatchId, b => b.Priority, ct);

            // 只取有可（重）排派工的工單
            // 校準模式只處理已排派工（待排的不排入）
            var pendingWos = workOrders
                .Where(w => w.Operations.Any(op =>
                    op.Dispatches.Any(d => preserve ? d.Status == DispatchStatus.Scheduled
                                                    : ReschedulableStatuses.Contains(d.Status))))
                .ToList();

            // 註：pendingWos 為空也不能提早 return——生產中派工的計畫結束仍須依未完成量重算
            // （見下方凍結派工區塊），否則「全部都在生產中」的廠別按重新排程等於沒反應。

            // 取可用機台：Available 必含；Standby 依系統參數決定。與開工閘口共用同一份判定。
            var allowedStatuses = await MachineAvailability.UsableStatusesAsync(_plantParams, plantId, ct);

            var machines = await _db.WorkCenterMachines.IgnoreQueryFilters()
                .AsNoTracking()
                .Include(m => m.WorkCenter)
                .Where(m => m.WorkCenter != null && m.WorkCenter.PlantId == plantId
                            && allowedStatuses.Contains(m.MachineStatus))
                .ToListAsync(ct);

            // 生產線工作中心（IsProductionLine=true）：
            //   - 未登錄線別 → 整條線視為單一排程資源，key 用 WorkCenterCode
            //     （詳見 AllocateDispatchSlot 生產線分支）；
            //   - 已登錄線別（WorkCenterMachine，每筆＝一條線）→ 與一般機台同流程，
            //     多線平行、挑最早可用的線（上方 machines 查詢已含線別列）。
            var productionLineWcs = await _db.WorkCenters.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.PlantId == plantId && w.IsProductionLine)
                .Select(w => w.WorkCenterCode)
                .ToListAsync(ct);

            // 「已登錄線別」判斷不可用上方 machines（受狀態閘門過濾）——若線別全數
            // 停線/保養，會誤 fallback 成單一資源照排；要以「存在非廢棄線別」為準，
            // 全數不可排時走機台分支自然回報 skipped。
            var lineRegisteredWcs = (await _db.WorkCenterMachines.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.WorkCenter != null && m.WorkCenter.PlantId == plantId
                            && m.WorkCenter.IsProductionLine
                            && m.MachineStatus != MachineStatus.Retired)
                .Select(m => m.WorkCenterCode)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            // 機台最早可用時間追蹤（key = MachineCode；生產線另以 WorkCenterCode 為 key，見下）。
            // §10-8 / §7.1：起點 = max(now, 該機台凍結中派工的最大 ScheduledEnd)——
            // 不再從「今天」裸起算，否則連跑兩次排程會雙重預約同一時段。
            var machineAvail = machines.ToDictionary(m => m.MachineCode, _ => today);
            foreach (var wc in productionLineWcs)
                machineAvail.TryAdd(wc, today);

            // 凍結派工：時段不移動，但「計畫結束」要依未完成量（Quantity−QtyDone）重算——
            // 帳面 ScheduledEnd 是投產前以整批量算出來的，生產中的工單一旦有進度就不再成立：
            //   - 進度落後/超時：重估 > ScheduledEnd，機台佔用順延到實際可完成時刻，
            //     新工作不會被排進「帳面已結束、實際仍在生產」的時段；
            //   - 進度超前：重估 < ScheduledEnd，計畫結束往前收，後續工序/同機台派工得以提早排入；
            //   - OnHold：無法預知恢復時刻，重估只是「立即恢復」的樂觀下界，故只准往後延、
            //     不准把原計畫結束往前拉；
            //   - ScheduledEnd 為 null（手動開工未排程）：同樣以剩餘量自 now 估，不再漏佔。
            // 重估工時與報工即時 ETA 同口徑（EtaHelper.CalcRemainingMinutes 依完工比例縮減
            // 標準工時），避免同一張派工在「報工重算」與「全量重排」兩條路徑得到不同的完工預估。
            // 委外派工無工時概念（以工作天步進），不在此重算。
            // 只算未結案工單的派工：工單一旦完工／結案／作廢，殘留的派工不得再佔機台
            // （舊資料曾有暫停派工跟著工單完工、永遠留在凍結佔用裡，機台前面永遠空一段排不進）。
            var frozenOccupations = await _db.Dispatches.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(d => d.PlantId == plantId
                         && FrozenStatuses.Contains(d.Status)
                         && _db.WorkOrders.Any(w => w.WorkOrderId == d.WorkOrderId
                                                 && (w.Status == WorkOrderStatus.Released
                                                     || w.Status == WorkOrderStatus.InProgress)))
                .Select(d => new { d.DispatchId, d.MachineCode, d.ScheduledStart, d.ScheduledEnd,
                                   d.WorkOrderId, d.OperationSeq, d.Quantity, d.QtyDone,
                                   d.Status, d.IsOutsource })
                .ToListAsync(ct);

            var frozenWoIds = frozenOccupations.Select(d => d.WorkOrderId).Distinct().ToList();
            var frozenOps = await _db.WorkOrderOperations.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(o => frozenWoIds.Contains(o.WorkOrderId))
                .ToListAsync(ct);
            var frozenOpMap = frozenOps
                .GroupBy(o => (o.WorkOrderId, o.OperationSeq))
                .ToDictionary(g => g.Key, g => g.First());

            // 先後關係要靠 BOM 母子鏈，而父工單未必自己也有凍結派工，故整批（同生產批）
            // 的工單都要撈，深度才算得出來。
            var frozenBatchIds = await _db.WorkOrders.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => frozenWoIds.Contains(w.WorkOrderId))
                .Select(w => w.ProductionBatchId)
                .Distinct()
                .ToListAsync(ct);
            var batchWoRows = await _db.WorkOrders.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.PlantId == plantId && frozenBatchIds.Contains(w.ProductionBatchId))
                .Select(w => new { w.WorkOrderId, w.ParentWorkOrderId, w.ProductionBatchId })
                .ToListAsync(ct);
            var parentOf = batchWoRows.ToDictionary(w => w.WorkOrderId, w => w.ParentWorkOrderId);
            var batchOf  = batchWoRows.ToDictionary(w => w.WorkOrderId, w => w.ProductionBatchId);

            var frozenInputs = frozenOccupations
                .Select(o => new FrozenDispatchInput(
                    o.DispatchId, o.WorkOrderId, o.OperationSeq,
                    batchOf.TryGetValue(o.WorkOrderId, out var b) ? b : null,
                    o.MachineCode, o.ScheduledStart, o.ScheduledEnd,
                    o.Quantity, o.QtyDone, o.Status, o.IsOutsource))
                .ToList();

            // 重算後的計畫時段（DispatchId → 新起訖）：供接力給後續工序、寫回資料庫。
            // 起訖成對重算——只重算 End 會讓甘特長條「左緣是舊化石、右緣是新重估」。
            // machineAvail 由 EstimateFrozenDispatches 就地更新（凍結派工彼此也要排隊）。
            var frozenNewSlots = new Dictionary<string, (DateTime Start, DateTime End)>();
            var frozenEstimates = EstimateFrozenDispatches(
                frozenInputs, parentOf, frozenOpMap, ctx, today, machineAvail);

            foreach (var (dispatchId, est) in frozenEstimates)
            {
                if (!est.Recalculated) continue;
                frozenNewSlots[dispatchId] =
                    (DateTime.SpecifyKind(est.Start, DateTimeKind.Utc),
                     DateTime.SpecifyKind(est.End,   DateTimeKind.Utc));
            }

            // 把重算後的計畫時段套進本次排程的記憶體圖，後續工序才會接力到「重算後」的完成
            // 時刻（否則 opEnd 仍讀到投產前以整批量算的舊 ScheduledEnd）。
            if (frozenNewSlots.Count > 0)
            {
                foreach (var d in workOrders.SelectMany(w => w.Operations).SelectMany(op => op.Dispatches))
                {
                    if (!frozenNewSlots.TryGetValue(d.DispatchId, out var slot)) continue;
                    d.ScheduledStart = slot.Start;
                    d.ScheduledEnd   = slot.End;
                }
            }

            // §24.7：料況確認已上移至投產（硬閘），排程不再重查料況。
            // Dispatch.MaterialRisk 於投產（Release）時設定，排程不改寫。

            // 凍結時界（只有自動重排會傳，手動排程由生管全權重排）：已排、尚未結束、且在時界內開工的派工
            // 保留原機台與時段，視同凍結——現場可能已照排程備料、備模。機台已不可用的不保留（照常重排）。
            // 機台佔用是「每台一個最早可用指標」、不插空檔，所以時界內若有派工被取消或提早完工，
            // 那段空檔要等時界過了才會被回填（mfg-domain.md §10-8 凍結時界）。
            var fenced = new HashSet<string>();
            if (!preserve && freezeWindow is { } fw && fw > TimeSpan.Zero)
            {
                var fenceUntil = today + fw;
                foreach (var d in pendingWos.SelectMany(w => w.Operations).SelectMany(op => op.Dispatches))
                {
                    if (d.Status != DispatchStatus.Scheduled || d.MachineCode == null
                        || d.ScheduledStart is not { } fs || d.ScheduledEnd is not { } fe) continue;
                    if (fs >= fenceUntil || fe <= today) continue;
                    if (!machineAvail.TryGetValue(d.MachineCode, out var avail)) continue;
                    fenced.Add(d.DispatchId);
                    if (fe > avail) machineAvail[d.MachineCode] = fe;
                }
            }

            // 排程紀錄用：本次可（重）排派工排程前的機台與時段
            var before = preserve
                ? new Dictionary<string, DispatchSlotBefore>()
                : pendingWos.SelectMany(w => w.Operations).SelectMany(op => op.Dispatches)
                    .Where(d => ReschedulableStatuses.Contains(d.Status))
                    .ToDictionary(d => d.DispatchId,
                                  d => new DispatchSlotBefore(d.Status, d.MachineCode, d.ScheduledStart, d.WorkOrderId, d.OperationSeq));

            // 依規則排序生產批
            // 校準模式：批的先後依目前最早的已排時刻——維持機台上原本的先後，不依規則重排
            var sortedBatches = preserve ? SortBatchesByCurrentStart(pendingWos) : SortBatches(pendingWos, rule, batchPriority);

            var scheduled = new List<Dispatch>();
            var skipped   = new List<string>();

            foreach (var batchWos in sortedBatches)
            {
                // 批次內：依 BOM 深度排（深階先排），同深度依 OperationSeq
                var orderedOps = OrderOperationsInBatch(batchWos);

                DateTime prevEnd = today;   // 前置工序的完成時刻（批內跨工單/工序傳遞）
                PrevOpContext? prevCtx = null;   // 前置工序脈絡（§7.3 b 工序重疊推算用）

                foreach (var (wo, op) in orderedOps)
                {
                    // 1:N —— 同工序可能有多個（拆分）派工：兄弟派工皆以前序工序完成時刻 prevEnd 為起點
                    // 平行配置（同機台由 machineAvail 串行、不同機台平行）；後序工序需等所有兄弟中最晚者。
                    var opDispatches = op.Dispatches;
                    if (opDispatches == null || opDispatches.Count == 0) continue;

                    var wcMachines = machines.Where(m => m.WorkCenterCode == op.WorkCenterId).ToList();
                    var isProdLine = productionLineWcs.Contains(op.WorkCenterId)
                                     && !lineRegisteredWcs.Contains(op.WorkCenterId);
                    var opEnd = prevEnd;   // 本工序所有派工完成的最晚時刻
                    // 本工序的起點/工時：作為「下一道工序」的前工序脈絡（兄弟派工取最早起點、加總工時）
                    DateTime? opStart = null;
                    decimal opWorkMinutes = 0;

                    foreach (var dispatch in opDispatches)
                    {
                        opWorkMinutes += CalcRequiredMinutes(op, dispatch.Quantity);

                        // 校準模式不排入待排派工（也不佔機台、不接力）
                        if (preserve && dispatch.Status == DispatchStatus.Pending) continue;

                        // 凍結派工（含凍結時界內的已排派工）：不移動，但其完成時刻要接力給後續工序
                        // （否則後序會排在前序完成前）
                        if (!ReschedulableStatuses.Contains(dispatch.Status) || fenced.Contains(dispatch.DispatchId))
                        {
                            if (dispatch.ScheduledEnd.HasValue && dispatch.ScheduledEnd.Value > opEnd)
                                opEnd = dispatch.ScheduledEnd.Value;
                            if (dispatch.ScheduledStart.HasValue
                                && (opStart == null || dispatch.ScheduledStart.Value < opStart))
                                opStart = dispatch.ScheduledStart.Value;
                            continue;
                        }

                        // 人指定的機台（換機台／拆分指定／換機續做）只排在那台；那台不可用才退回一般挑選。
                        // 校準模式一律留在目前的機台。
                        var lockedMachine = (preserve || dispatch.IsMachineLocked) && dispatch.MachineCode != null
                            ? machines.FirstOrDefault(m => m.MachineCode == dispatch.MachineCode)
                            : null;
                        if (preserve && lockedMachine == null && !isProdLine)
                        {
                            // 目前的機台不可用（理應已被退回待排）：校準不替它換機台，原樣保留
                            if (dispatch.ScheduledEnd.HasValue && dispatch.ScheduledEnd.Value > opEnd)
                                opEnd = dispatch.ScheduledEnd.Value;
                            continue;
                        }
                        var candidates = lockedMachine != null
                            ? new List<WorkCenterMachine> { lockedMachine }
                            : wcMachines;

                        var slot = AllocateDispatchSlot(
                            dispatch, op, wo, prevEnd, candidates, ctx, machineAvail, today,
                            isProdLine && lockedMachine == null, prevCtx);

                        if (slot == null)
                        {
                            // 校準排不進（例：日曆沒有產能）就維持原時段，不把它退回待排
                            if (!preserve) skipped.Add(dispatch.DispatchId);
                            continue;
                        }

                        dispatch.MachineCode    = slot.MachineCode;
                        // timestamptz 欄位：SpecifyKind(Utc)（mfg-domain.md UTC 地雷規則）
                        dispatch.ScheduledStart = DateTime.SpecifyKind(slot.Start, DateTimeKind.Utc);
                        dispatch.ScheduledEnd   = DateTime.SpecifyKind(slot.End, DateTimeKind.Utc);
                        dispatch.Status         = DispatchStatus.Scheduled;
                        // §24.7：MaterialRisk 由投產時設定，排程不改寫

                        if (slot.End > opEnd) opEnd = slot.End;
                        if (opStart == null || slot.Start < opStart) opStart = slot.Start;
                        scheduled.Add(dispatch);
                    }

                    prevEnd = opEnd;

                    // 工序間移轉/等候間隔（MoveTime + QueueTime）：機台佔用僅算 setup + unit，
                    // 這段不佔機台，但會延後下一道工序最早可開工時刻，使排程每工序總歷時
                    // ＝機台佔用＋間隔，與 MRP OpMinutes（整備＋單件×量＋Move＋Queue）口徑一致（§3.1(8)）。
                    var gapMinutes = OperationTimeUnitExtensions.ToMinutes(op.MoveTime + op.QueueTime, op.TimeUnit);
                    if (gapMinutes > 0)
                    {
                        IReadOnlyList<(int Start, int End)> GetPlantSegs(DateOnly d) =>
                            ctx.GetSegments(null, null, d).Select(s => (s.Start, s.End)).ToList();
                        prevEnd = WorkingTimeWalker.AddWorkingMinutes(prevEnd, gapMinutes, GetPlantSegs);
                    }

                    // 交棒給下一道工序（§7.3 b）。委外工序無工時/移轉批量概念，以整批處理
                    // （傳 WholeOrder），下一序就退回「前序完工才開工」，與舊系統一致。
                    prevCtx = new PrevOpContext(
                        Start:        opStart ?? prevEnd,
                        End:          prevEnd,
                        WorkMinutes:  opWorkMinutes,
                        Mode:         op.IsOutsource ? OperationMode.WholeOrder : op.BatchMode,
                        TransferQty:  op.BatchQty,
                        SetupMinutes: OperationTimeUnitExtensions.ToMinutes(op.SetupTime, op.SetupTimeUnit),
                        UnitMinutes:  OperationTimeUnitExtensions.ToMinutes(op.UnitTime, op.TimeUnit),
                        GapMinutes:   gapMinutes);
                }
            }

            // 批次更新資料庫
            var skippedIds = skipped.ToHashSet();
            if (scheduled.Count > 0 || skippedIds.Count > 0 || frozenNewSlots.Count > 0)
            {
                var ids = scheduled.Select(d => d.DispatchId)
                    .Concat(skippedIds)
                    .Concat(frozenNewSlots.Keys)
                    .ToHashSet();
                var toUpdate = await _db.Dispatches.IgnoreQueryFilters()
                    .Where(d => ids.Contains(d.DispatchId))
                    .ToListAsync(ct);

                var slotDict = scheduled.ToDictionary(d => d.DispatchId);
                foreach (var d in toUpdate)
                {
                    if (slotDict.TryGetValue(d.DispatchId, out var s))
                    {
                        // 指定的機台不可用、改排到別台時，指定已不成立
                        if (d.IsMachineLocked && d.MachineCode != s.MachineCode) d.IsMachineLocked = false;
                        d.MachineCode    = s.MachineCode;
                        d.ScheduledStart = s.ScheduledStart;
                        d.ScheduledEnd   = s.ScheduledEnd;
                        d.Status         = DispatchStatus.Scheduled;
                        // §24.7：MaterialRisk 由投產時設定，排程不改寫
                    }
                    else if (frozenNewSlots.TryGetValue(d.DispatchId, out var slot))
                    {
                        // 生產中派工：狀態與機台不動，只更新依未完成量重算的計畫時段。
                        // 起訖必須成對寫回——舊寫法對起點用 ??=（已有值就不動），會留下
                        // 前一次排程的化石起點，甘特上表現為「排程開始在過去」。
                        d.ScheduledStart = slot.Start;
                        d.ScheduledEnd   = slot.End;
                        d.UpdatedAt      = DateTime.UtcNow;
                    }
                    else if (d.Status == DispatchStatus.Scheduled)
                    {
                        // 本次未能排入（無機台）的原 Scheduled 派工：釋回未排清單，
                        // 不保留舊時段（舊時段已不在任何機台佔用模型內，留著會誤導甘特）
                        d.Status         = DispatchStatus.Pending;
                        d.MachineCode    = null;
                        d.IsMachineLocked = false;
                        d.ScheduledStart = null;
                        d.ScheduledEnd   = null;
                        // MaterialRisk 不在排程改寫（投產時設定）
                    }
                }
            }

            if (preserve)
            {
                // 校準不代表已納入新異動（待排沒排、機台沒換），不更新排程紀錄
                await _db.SaveChangesAsync(ct);
                return new ScheduleRunResult(scheduled.Count, skipped.Count, skipped);
            }

            // 最近一次排程紀錄（排程頁「上次排程時間」）。與派工異動同一次存檔。
            var state = await _db.ScheduleRunStates.FirstOrDefaultAsync(s => s.PlantId == plantId, ct);
            if (state == null)
            {
                state = new ScheduleRunState { PlantId = plantId };
                _db.ScheduleRunStates.Add(state);
            }
            state.LastRunAt     = DateTime.UtcNow;
            state.LastRunBy     = runBy;
            state.LastScheduled = scheduled.Count;
            state.LastSkipped   = skipped.Count;
            state.DataAsOf      = dataAsOf;
            state.LastTrigger   = trigger.ToString();
            state.FingerprintFrom = todayDate;
            state.FingerprintTo   = windowTo;
            state.CalendarFingerprint = calendarFingerprint;

            var log = BuildRunLog(plantId, rule, today, runBy, trigger, freezeWindow, dataAsOf,
                                  before, scheduled, skipped, fenced.Count);
            _db.ScheduleRunLogs.Add(log);
            await PruneRunLogsAsync(plantId, ct);

            await _db.SaveChangesAsync(ct);

            return new ScheduleRunResult(scheduled.Count, skipped.Count, skipped, Fenced: fenced.Count);
        }

        /// <summary>排程前一張可（重）排派工的狀態、機台與起點（排程紀錄比對用）。</summary>
        private readonly record struct DispatchSlotBefore(
            DispatchStatus Status, string? MachineCode, DateTime? Start, string WorkOrderId, int OperationSeq);

        /// <summary>
        /// 比對排程前後，產生排程紀錄與異動明細。開始時刻差不到一分鐘視為沒動（避免秒級誤差灌明細）。
        /// 「明天」以 <paramref name="today"/> 的日期加一天判斷——時刻數字本身就是廠別當地時間。
        /// </summary>
        private static ScheduleRunLog BuildRunLog(
            string plantId, ScheduleRule rule, DateTime today, string? runBy, ScheduleTrigger trigger,
            TimeSpan? freezeWindow, DateTime dataAsOf,
            IReadOnlyDictionary<string, DispatchSlotBefore> before,
            IReadOnlyList<Dispatch> scheduled, IReadOnlyList<string> skipped, int fencedCount)
        {
            var tomorrow = DateOnly.FromDateTime(today).AddDays(1);
            bool IsTomorrow(DateTime? t) => t is { } v && DateOnly.FromDateTime(v) == tomorrow;

            var changes = new List<ScheduleRunChange>();
            foreach (var d in scheduled)
            {
                if (!before.TryGetValue(d.DispatchId, out var b)) continue;
                ScheduleChangeKind? kind =
                    b.Status == DispatchStatus.Pending ? ScheduleChangeKind.NewlyScheduled
                    : b.MachineCode != d.MachineCode ? ScheduleChangeKind.MachineChanged
                    : b.Start is not { } bs || d.ScheduledStart is not { } ns
                      || Math.Abs((ns - bs).TotalMinutes) >= 1 ? ScheduleChangeKind.Moved
                    : null;
                if (kind == null) continue;
                changes.Add(new ScheduleRunChange
                {
                    DispatchId = d.DispatchId, WorkOrderId = b.WorkOrderId, OperationSeq = b.OperationSeq, Kind = kind.Value,
                    OldMachineCode = b.Status == DispatchStatus.Pending ? null : b.MachineCode,
                    OldStart       = b.Status == DispatchStatus.Pending ? null : b.Start,
                    NewMachineCode = d.MachineCode, NewStart = d.ScheduledStart,
                });
            }
            foreach (var id in skipped)
            {
                // 原本就待排、這次仍排不進的不算異動
                if (!before.TryGetValue(id, out var b) || b.Status != DispatchStatus.Scheduled) continue;
                changes.Add(new ScheduleRunChange
                {
                    DispatchId = id, WorkOrderId = b.WorkOrderId, OperationSeq = b.OperationSeq,
                    Kind = ScheduleChangeKind.Unscheduled, OldMachineCode = b.MachineCode, OldStart = b.Start,
                });
            }

            var ordered = changes
                .OrderBy(c => c.NewStart ?? c.OldStart ?? DateTime.MaxValue)
                .ThenBy(c => c.DispatchId, StringComparer.Ordinal)
                .ToList();

            return new ScheduleRunLog
            {
                PlantId      = plantId,
                StartedAt    = dataAsOf,
                FinishedAt   = DateTime.UtcNow,
                Trigger      = trigger.ToString(),
                RunBy        = runBy,
                Rule         = rule.ToString(),
                FreezeHours  = freezeWindow is { } fw && fw > TimeSpan.Zero ? (decimal)fw.TotalHours : null,
                ScheduledCount = scheduled.Count,
                SkippedCount   = skipped.Count,
                FencedCount    = fencedCount,
                NewlyScheduledCount = changes.Count(c => c.Kind == ScheduleChangeKind.NewlyScheduled),
                MovedCount          = changes.Count(c => c.Kind == ScheduleChangeKind.Moved),
                MachineChangedCount = changes.Count(c => c.Kind == ScheduleChangeKind.MachineChanged),
                UnscheduledCount    = changes.Count(c => c.Kind == ScheduleChangeKind.Unscheduled),
                TomorrowChangedCount = changes.Count(c => IsTomorrow(c.OldStart) || IsTomorrow(c.NewStart)),
                ChangesTruncated = ordered.Count > ScheduleRunLog.MaxChangeRows,
                Changes = ordered.Take(ScheduleRunLog.MaxChangeRows).ToList(),
            };
        }

        /// <summary>刪除本廠超過保留期的排程紀錄（明細由資料庫 ON DELETE CASCADE 帶走）。</summary>
        private async Task PruneRunLogsAsync(string plantId, CancellationToken ct)
        {
            var cutoff = DateTime.UtcNow.AddDays(-ScheduleRunLog.RetentionDays);
            var old = _db.ScheduleRunLogs.Where(l => l.PlantId == plantId && l.StartedAt < cutoff);
            if (_db.Database.IsNpgsql())
            {
                await old.ExecuteDeleteAsync(ct);
                return;
            }
            // InMemory（單元測試）不支援 ExecuteDelete
            _db.ScheduleRunLogs.RemoveRange(await old.Include(l => l.Changes).ToListAsync(ct));
        }

        // ── EstimateFrozenDispatch ──────────────────────────────────────────────

        /// <summary>
        /// 凍結派工（生產中家族）的計畫時段重估——**起訖一起重算**。
        ///
        /// 完工重估：依未完成量（Quantity−QtyDone）自 <paramref name="today"/> 走工作日曆，
        /// 與報工即時 ETA 同口徑（<see cref="EtaHelper.CalcRemainingMinutes"/>）。
        /// OnHold 無法預知恢復時刻，重估只是「立即恢復」的樂觀下界，故只准往後延。
        ///
        /// 起點重估（本次修正）：舊寫法只重算 ScheduledEnd、對 ScheduledStart 用 <c>??=</c>
        /// （已有值就不動），於是**左緣是前一次排程的化石、右緣是本次自 today 的重估**，
        /// 長條長度既不等於工時、也不等於任何一次排程的結果，甘特上會看到「起點排在過去」。
        /// 判準取「有沒有實際投入」而非狀態字面：
        ///   有產出（QtyDone&gt;0）或機台已實際投入（InProgress／OnHold／SetupInProgress）
        ///     → 帳面起點是真實開工時刻，保留（起點在過去是正確的）；
        ///   其餘（ReadyToStart／PendingFPI 且尚無產出）→ 一件都還沒做，整份工時都是自
        ///     today 起算，起點必須跟著移到 today（並 snap 進可用時段，否則起點會停在
        ///     沒有產能的時刻，見 <see cref="WorkingTimeWalker.SnapToAvailable"/>）。
        /// </summary>
        /// <param name="chainEarliest">
        /// 前置工單/工序的完工時刻（同批接力）。這是**往後推**那條的候選起點。
        /// ⚠️ 不可拿它當倒推的下限——倒推的用意就是讓本工序**早於**前置完工開工（重疊），
        /// 拿前置完工當地板會把重疊整個抵消。null 等同 <paramref name="today"/>。
        /// </param>
        /// <param name="resourceAvail">
        /// 本張所在機台的最早可用時刻（同機台前一張的完工）。這才是**兩條路徑共同的硬地板**
        /// ——機台被佔著就是被佔著，重疊也不能穿過去。語意與
        /// <see cref="PlaceSlot"/> 的 <c>floor</c> 一致。null 等同 <paramref name="today"/>。
        /// </param>
        /// <remarks>
        /// <paramref name="chainEarliest"/> 與 <paramref name="resourceAvail"/>
        /// **只對「尚未實際投入」的派工生效**——已在跑的機台不可能因為別人晚了就倒退重來，
        /// 它的起點是既成事實，只有完工時刻需要重估。
        /// </remarks>
        /// <param name="prev">
        /// 前置工序脈絡（§7.3 b 工序重疊）。有值且移轉方式非「整批」時，起訖改依重疊語意推算，
        /// 與 <see cref="AllocateDispatchSlot"/> 共用 <see cref="ResolveOverlapStart"/>。
        /// 同樣只對「尚未實際投入」的派工生效。
        /// </param>
        public static FrozenDispatchEstimate EstimateFrozenDispatch(
            FrozenDispatchInput occ,
            WorkOrderOperation? op,
            CalendarContext ctx,
            DateTime today,
            DateTime? chainEarliest = null,
            PrevOpContext? prev = null,
            DateTime? resourceAvail = null)
        {
            var start = occ.ScheduledStart ?? today;
            var end   = occ.ScheduledEnd   ?? today;

            var remainingQty = Math.Max(occ.Quantity - occ.QtyDone, 0m);
            if (remainingQty <= 0 || occ.IsOutsource || op == null || op.IsOutsource)
                return new FrozenDispatchEstimate(start, end, false);

            IReadOnlyList<(int Start, int End)> GetSegs(DateOnly d) =>
                ctx.GetSegments(occ.MachineCode, op.WorkCenterId, d).Select(s => (s.Start, s.End)).ToList();
            var downtimes = ctx.GetDowntimeRanges(occ.MachineCode, op.WorkCenterId);

            var started = HasPhysicallyStarted(occ.Status, occ.QtyDone);

            // 兩個不同的下限，語意不可混用（混用會讓倒推重疊失效）：
            //   earliest ＝ 同批接力點，只是**往後推**那條的候選起點；
            //   floor    ＝ 機台硬地板，兩條路徑都不得穿過。
            var earliest = !started && chainEarliest is { } ce && ce > today ? ce : today;
            var floor    = !started && resourceAvail is { } ra && ra > today ? ra : today;
            var from     = earliest > floor ? earliest : floor;

            var stdMin = CalcRequiredMinutes(op, occ.Quantity);
            var remMin = EtaHelper.CalcRemainingMinutes(stdMin, occ.QtyDone, occ.Quantity);

            // ── 工序重疊（§7.3 b）：與主迴圈同一套語意 ──────────────────────────────
            // 凍結派工同樣可以「邊收移轉批邊做」，不必等前置整批完工。少了這段，同一批裡
            // 凍結的那張會被畫成單純接力、未凍結的那張是正確的倒推重疊，兩張語意不一致。
            // 已實際投入者不套用——它的起點是既成事實，重算只為了更新完工時刻。
            if (!started)
            {
                var unitMin = OperationTimeUnitExtensions.ToMinutes(op.UnitTime, op.TimeUnit);
                var (candidateStart, backwardAnchorEnd) =
                    ResolveOverlapStart(prev, remMin, unitMin, earliest, today);

                if (backwardAnchorEnd.HasValue)
                {
                    // 錨點落在非上班時段時先退回最近一段產能的結束點，否則會多倒推一段空白
                    var endSnap = WorkingTimeWalker.SnapBackToAvailable(backwardAnchorEnd.Value, GetSegs);
                    if (endSnap != null && endSnap.Value > floor)
                    {
                        try
                        {
                            // SubtractWorkingMinutes 不處理計劃停機，先自可用時段扣掉，
                            // 否則倒推起點會偏晚、實際做不完
                            var bStart = WorkingTimeWalker.SubtractWorkingMinutes(
                                endSnap.Value, remMin, ExcludeDowntimes(GetSegs, downtimes));
                            // 下限是**機台**可用時刻，不是前置完工——倒推的用意就是早於前置完工開工
                            if (bStart >= floor)
                                return new FrozenDispatchEstimate(bStart, endSnap.Value, true);
                        }
                        catch (InvalidOperationException)
                        {
                            // 倒推超出日曆視窗 → 落回往後計算
                        }
                    }
                }

                from = candidateStart > floor ? candidateStart : floor;
            }

            var est = WorkingTimeWalker.AddWorkingMinutes(from, remMin, GetSegs, downtimes);

            // OnHold 只准往後延（重估為樂觀下界）；其餘生產中狀態一律以重估為準
            end = occ.Status == DispatchStatus.OnHold && occ.ScheduledEnd.HasValue
                  && occ.ScheduledEnd.Value > est
                ? occ.ScheduledEnd.Value
                : est;

            if (!started)
                start = WorkingTimeWalker.SnapToAvailable(from, GetSegs) ?? from;

            return new FrozenDispatchEstimate(start, end, true);
        }

        /// <summary>
        /// BOM 深度：無 <c>ParentWorkOrderId</c> 為 0（成品），每往上一層 +1（半成品深階大）。
        /// 查不到父工單時就地停止（與 <see cref="OrderOperationsInBatch"/> 同語意），
        /// 並以已走訪集合擋住資料異常造成的環狀父子鏈。
        /// </summary>
        public static int BomDepth(string workOrderId, IReadOnlyDictionary<string, string?> parentOf)
        {
            var seen  = new HashSet<string>();
            var depth = 0;
            var cur   = workOrderId;
            while (seen.Add(cur)
                   && parentOf.TryGetValue(cur, out var parent)
                   && parent != null
                   && parentOf.ContainsKey(parent))
            {
                depth++;
                cur = parent;
            }
            return depth;
        }

        /// <summary>
        /// 一整批凍結派工的計畫時段重估——**含彼此之間的先後關係**（mfg-domain.md §10-8）。
        ///
        /// 為什麼要有複數版：凍結派工不進 <c>pendingWos</c>，因此排程主迴圈的 BOM 深度排序與
        /// <c>prevEnd</c> 接力**對它們一行都不生效**。舊實作是每張各自自 today 起算自己的剩餘
        /// 工時，等於假設所有機台平行、彼此無關，於是
        ///   ① 母件工時短時會**早於自己的元件完工**（實例 PB-2607-00014：母件 640 分算到 8/17、
        ///      元件 2460 分算到 8/20）；
        ///   ② 同一台機器上的兩張凍結派工會**同時開工**、時段重疊。
        ///
        /// 排序與主迴圈一致：同批內 BOM 深階先（元件先做完才輪到母件），同深度依工序序號。
        /// 跨批之間沒有物料相依，只共用機台，故僅透過 <paramref name="machineAvail"/> 互相推擠；
        /// 批的先後以批號排序取得穩定結果（同一份資料每次跑出來一樣）。
        ///
        /// <paramref name="machineAvail"/> 會被就地更新為各機台的最晚完工，供後續主迴圈接力。
        /// </summary>
        public static IReadOnlyDictionary<string, FrozenDispatchEstimate> EstimateFrozenDispatches(
            IReadOnlyList<FrozenDispatchInput> occupations,
            IReadOnlyDictionary<string, string?> parentOf,
            IReadOnlyDictionary<(string WorkOrderId, int OperationSeq), WorkOrderOperation> opMap,
            CalendarContext ctx,
            DateTime today,
            IDictionary<string, DateTime>? machineAvail = null)
        {
            var result = new Dictionary<string, FrozenDispatchEstimate>();

            var ordered = occupations
                .OrderBy(o => o.BatchId ?? string.Empty, StringComparer.Ordinal)
                .ThenByDescending(o => BomDepth(o.WorkOrderId, parentOf))
                .ThenBy(o => o.OperationSeq)
                .ThenBy(o => o.DispatchId, StringComparer.Ordinal)
                .ToList();

            // 同批內的接力點（前置工單/工序的完工時刻）與前置脈絡（§7.3 b 工序重疊用）
            var batchPrevEnd = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var batchPrevCtx = new Dictionary<string, PrevOpContext>(StringComparer.Ordinal);

            foreach (var occ in ordered)
            {
                var batchKey = occ.BatchId ?? string.Empty;

                // 兩個下限分開傳（不可先取 max）：接力點只約束「往後推」那條，
                // 機台可用時刻才是兩條路徑共同的硬地板。合併的話倒推重疊會被自己抵消。
                DateTime? chainEarliest =
                    batchPrevEnd.TryGetValue(batchKey, out var prevEnd) ? prevEnd : null;
                DateTime? resAvail =
                    occ.MachineCode != null && machineAvail != null
                    && machineAvail.TryGetValue(occ.MachineCode, out var mAvail) ? mAvail : null;

                opMap.TryGetValue((occ.WorkOrderId, occ.OperationSeq), out var op);
                PrevOpContext? prevCtx =
                    batchPrevCtx.TryGetValue(batchKey, out var pc) ? pc : null;

                var est = EstimateFrozenDispatch(occ, op, ctx, today, chainEarliest, prevCtx, resAvail);
                result[occ.DispatchId] = est;

                if (!batchPrevEnd.TryGetValue(batchKey, out var cur) || est.End > cur)
                    batchPrevEnd[batchKey] = est.End;

                // 交棒給同批的下一張（§7.3 b）。移轉方式/移轉批量取自**本工序**——它決定
                // 「怎麼把半成品交給下一站」。委外無工時/移轉批量概念，一律以整批處理。
                if (op != null)
                {
                    var gapMinutes = OperationTimeUnitExtensions.ToMinutes(
                        op.MoveTime + op.QueueTime, op.TimeUnit);
                    batchPrevCtx[batchKey] = new PrevOpContext(
                        Start:        est.Start,
                        End:          est.End.AddMinutes((double)gapMinutes),
                        WorkMinutes:  CalcRequiredMinutes(op, occ.Quantity),
                        Mode:         op.IsOutsource ? OperationMode.WholeOrder : op.BatchMode,
                        TransferQty:  op.BatchQty,
                        SetupMinutes: OperationTimeUnitExtensions.ToMinutes(op.SetupTime, op.SetupTimeUnit),
                        UnitMinutes:  OperationTimeUnitExtensions.ToMinutes(op.UnitTime, op.TimeUnit),
                        GapMinutes:   gapMinutes);
                }

                if (occ.MachineCode != null && machineAvail != null
                    && machineAvail.TryGetValue(occ.MachineCode, out var t) && est.End > t)
                    machineAvail[occ.MachineCode] = est.End;
            }

            return result;
        }

        /// <summary>
        /// 派工是否已實際投入產能——決定重估時要不要保留帳面起點。
        /// 有產出即算（不論狀態）；無產出時只認「機台已被佔用」的狀態。
        /// </summary>
        private static bool HasPhysicallyStarted(DispatchStatus status, decimal qtyDone) =>
            qtyDone > 0
            || status == DispatchStatus.InProgress
            || status == DispatchStatus.OnHold
            || status == DispatchStatus.SetupInProgress;

        // ── ResolveOverlapStart ─────────────────────────────────────────────────

        /// <summary>
        /// 工序重疊起點推算（mfg-domain.md §7.3 b，對應舊系統 WW02 L363-401）。
        ///
        /// 移轉方式取自「前工序」——決定它如何把半成品交給本工序：
        ///   整批(3)：前工序全部完工才移轉 → 本工序起點 = 前工序完工（無重疊，即原行為）
        ///   定量(2)：每滿 QT_BOR 移轉一次；連續(1)：每 1 件即可移轉
        /// 有重疊時再比兩序的總工時決定推算方向（這是關鍵，不是一律往後堆）：
        ///   前工序較快（prev.WorkMinutes &lt; 本工序工時）→ 本工序是瓶頸，第一批一到就能開工
        ///     且之後不會斷料 → **往後推**：起點 = 前工序開始 + 產出第一批的時間 + 移轉間隔
        ///   前工序較慢 → 本工序早開工只會等料、圖上會出現假的長條，實際是最後一批才決定
        ///     完工時刻 → **往前推**：終點 = 前工序完工 + 本工序處理最後一批的時間，再倒推起點
        ///
        /// ⚠️ 這段刻意抽出來共用：凍結派工（生產中家族）走的是另一條路徑
        /// （<see cref="EstimateFrozenDispatches"/>），若只在 <see cref="AllocateDispatchSlot"/>
        /// 裡實作，同一批的兩張派工會因為「一張凍結、一張沒凍結」而畫成兩種語意
        /// ——實例：PB-2607-00014（凍結）被畫成單純接力，PB-2607-00015（未凍結）
        /// 卻是正確的倒推重疊。
        /// </summary>
        public static (DateTime CandidateStart, DateTime? BackwardAnchorEnd) ResolveOverlapStart(
            PrevOpContext? prev,
            decimal requiredMin,
            decimal unitMin,
            DateTime earliest,
            DateTime today)
        {
            if (prev is not { } p || p.Mode == OperationMode.WholeOrder)
                return (earliest, null);

            // 每次移轉量：定量取 QT_BOR，連續取 1 件
            var quantum = p.Mode == OperationMode.Batch
                ? (p.TransferQty > 0 ? p.TransferQty : 1m)
                : 1m;

            if (p.WorkMinutes < requiredMin)
            {
                // 往後推：前工序產出第一個移轉批的時刻（含其整備與移轉間隔）
                var offset = p.SetupMinutes + p.UnitMinutes * quantum + p.GapMinutes;
                var s = p.Start.AddMinutes((double)offset);
                // 上限為前工序完工：重疊起點不該晚於「整批移轉」的起點，否則反而更差
                if (s > p.End) s = p.End;
                return (s > today ? s : today, null);
            }

            // 往前推：最後一個移轉批在前工序完工時抵達，本工序再做這一批即結束
            return (earliest, p.End.AddMinutes((double)(unitMin * quantum)));
        }

        // ── AllocateDispatchSlot ────────────────────────────────────────────────

        /// <summary>
        /// 單張派工時段配置（mfg-domain.md §7.3）。
        /// 委外（IsOutsource=true）：timeS=max(prevEnd,today)，以 QT_BOR 天步進工作日。
        /// 生產線（WorkCenter.IsProductionLine=true 且未登錄線別）：整條線視為單一排程資源，
        /// 不挑機台，用工作中心層級的日曆解析可用時段；已登錄線別的生產線不走此分支
        /// （isProductionLine=false），線別即機台，走一般機台配置。
        /// 一般機台：選最早可用機台，依前工序 CD_BOR 決定重疊起點（往後推）或倒推錨點
        /// （往前推），用 walker 消耗工時。
        /// </summary>
        public static DispatchSlot? AllocateDispatchSlot(
            Dispatch dispatch,
            WorkOrderOperation op,
            WorkOrder wo,
            DateTime prevEnd,
            IReadOnlyList<WorkCenterMachine> wcMachines,
            CalendarContext ctx,
            Dictionary<string, DateTime> machineAvail,
            DateTime today,
            bool isProductionLine = false,
            PrevOpContext? prev = null)
        {
            var requiredMin = CalcRequiredMinutes(op, dispatch.Quantity);
            var earliest    = prevEnd > today ? prevEnd : today;

            // ── 委外（無機台）─────────────────────────────────────────────────
            if (dispatch.IsOutsource || op.IsOutsource)
            {
                // 委外以 QT_BOR 當「天數」直接累加工作日
                var daysNeeded = (int)Math.Ceiling((double)op.BatchQty);
                var timeS = earliest;
                var timeE = AddWorkingDays(timeS, daysNeeded, wo.PlantId, ctx);
                return new DispatchSlot(null, timeS, timeE);
            }

            // ── 工序重疊（mfg-domain.md §7.3 b，對應舊系統 WW02 L363-401）────────
            // 移轉方式取自「前工序」——決定它如何把半成品交給本工序：
            //   整批(3)：前工序全部完工才移轉 → 本工序起點 = 前工序完工（無重疊，即原行為）
            //   定量(2)：每滿 QT_BOR 移轉一次；連續(1)：每 1 件即可移轉
            // 有重疊時再比兩序的總工時決定推算方向（這是關鍵，不是一律往後堆）：
            //   前工序較快（prev.WorkMinutes < 本工序工時）→ 本工序是瓶頸，第一批一到就能開工
            //     且之後不會斷料 → **往後推**：起點 = 前工序開始 + 產出第一批的時間 + 移轉間隔
            //   前工序較慢 → 本工序早開工只會等料、圖上會出現假的長條，實際是最後一批才決定
            //     完工時刻 → **往前推**：終點 = 前工序完工 + 本工序處理最後一批的時間，再倒推起點
            var unitMin = OperationTimeUnitExtensions.ToMinutes(op.UnitTime, op.TimeUnit);

            var (candidateStart, backwardAnchorEnd) =
                ResolveOverlapStart(prev, requiredMin, unitMin, earliest, today);

            // ── 生產線（整條線視為單一排程資源，不挑機台）──────────────────────────
            if (isProductionLine)
            {
                var lineCode = op.WorkCenterId!;
                var lineAvail = machineAvail.TryGetValue(lineCode, out var lt) ? lt : today;
                IReadOnlyList<(int Start, int End)> GetLineSegs(DateOnly d) =>
                    ctx.GetSegments(null, lineCode, d).Select(s => (s.Start, s.End)).ToList();

                return PlaceSlot(lineCode, candidateStart, backwardAnchorEnd, requiredMin,
                                 lineAvail, today, GetLineSegs,
                                 ctx.GetDowntimeRanges(null, lineCode), machineAvail);
            }

            // ── 一般機台 ───────────────────────────────────────────────────────
            if (wcMachines.Count == 0) return null;   // 無可用機台 → 跳過

            // 選機台：取目前最早可用的機台
            string? bestMachine = null;
            DateTime bestAvail  = DateTime.MaxValue;

            foreach (var m in wcMachines)
            {
                var avail = machineAvail.TryGetValue(m.MachineCode, out var t) ? t : today;
                if (avail < bestAvail)
                {
                    bestAvail   = avail;
                    bestMachine = m.MachineCode;
                }
            }

            if (bestMachine == null) return null;

            IReadOnlyList<(int Start, int End)> GetSegs(DateOnly d) =>
                ctx.GetSegments(bestMachine, op.WorkCenterId, d).Select(s => (s.Start, s.End)).ToList();

            return PlaceSlot(bestMachine, candidateStart, backwardAnchorEnd, requiredMin,
                             bestAvail, today, GetSegs,
                             ctx.GetDowntimeRanges(bestMachine, op.WorkCenterId), machineAvail);
        }

        /// <summary>
        /// 把一段工時放進指定資源（機台或生產線）的日曆，並更新該資源的最早可用時刻。
        ///
        /// <paramref name="backwardAnchorEnd"/> 有值時先試「往前計算」：以錨點為完工時刻倒推起點；
        /// 若倒推起點早於資源最早可用（或今天）代表這段時間其實排不進去，依 §7.3 c 落回往後計算
        /// （舊系統 WW02 的 <c>bForward = "N"</c> 回退）。
        /// </summary>
        private static DispatchSlot? PlaceSlot(
            string resourceKey,
            DateTime candidateStart,
            DateTime? backwardAnchorEnd,
            decimal requiredMin,
            DateTime resourceAvail,
            DateTime today,
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            IReadOnlyList<(DateTime From, DateTime To)> downtimes,
            Dictionary<string, DateTime> machineAvail)
        {
            var floor = resourceAvail > today ? resourceAvail : today;

            if (backwardAnchorEnd.HasValue)
            {
                // 錨點落在非上班時段時先退回最近一段產能的結束點，否則會多倒推一段空白
                var endSnap = WorkingTimeWalker.SnapBackToAvailable(backwardAnchorEnd.Value, getSegments);
                if (endSnap != null && endSnap.Value > floor)
                {
                    try
                    {
                        // SubtractWorkingMinutes 本身不處理計劃停機，先把停機自可用時段扣掉，
                        // 否則倒推起點會偏晚、實際做不完（往後推走 AddWorkingMinutes 則原生支援）。
                        var start = WorkingTimeWalker.SubtractWorkingMinutes(
                            endSnap.Value, requiredMin, ExcludeDowntimes(getSegments, downtimes));

                        if (start >= floor)
                        {
                            machineAvail[resourceKey] = endSnap.Value;
                            return new DispatchSlot(resourceKey, start, endSnap.Value);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // 倒推超出日曆視窗 → 落回往後計算
                    }
                }
            }

            // 排程起點 = max(重疊最早時間, 資源最早可用)，再校正到下一個真正有產能的時刻——
            // 否則若這個時間點落在非上班時段，ScheduledStart 會卡在沒有產能的時刻，
            // 只有 ScheduledEnd 經 AddWorkingMinutes 校正，起訖跟實際產能對不起來。
            var rawStart  = candidateStart > floor ? candidateStart : floor;
            var timeStart = WorkingTimeWalker.SnapToAvailable(rawStart, getSegments);
            if (timeStart == null) return null;   // 排程視窗內查無任何可用產能 → 跳過

            var timeEnd = WorkingTimeWalker.AddWorkingMinutes(
                timeStart.Value, requiredMin, getSegments, downtimes);

            machineAvail[resourceKey] = timeEnd;
            return new DispatchSlot(resourceKey, timeStart.Value, timeEnd);
        }

        /// <summary>
        /// 包一層可用時段查詢，把計劃停機自當日時段中扣除（供倒推用）。
        /// </summary>
        private static Func<DateOnly, IReadOnlyList<(int Start, int End)>> ExcludeDowntimes(
            Func<DateOnly, IReadOnlyList<(int Start, int End)>> getSegments,
            IReadOnlyList<(DateTime From, DateTime To)> downtimes)
        {
            if (downtimes.Count == 0) return getSegments;

            return date =>
            {
                var segs = getSegments(date);
                if (segs.Count == 0) return segs;

                var dayStart = date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc);
                // 換算成當日分鐘區間（跨日停機自動裁到 0..1440）
                var blocks = new List<(int From, int To)>();
                foreach (var dt in downtimes)
                {
                    var from = (int)Math.Floor((dt.From - dayStart).TotalMinutes);
                    var to   = (int)Math.Ceiling((dt.To   - dayStart).TotalMinutes);
                    if (to <= 0 || from >= 1440) continue;
                    blocks.Add((Math.Max(from, 0), Math.Min(to, 1440)));
                }
                if (blocks.Count == 0) return segs;

                var result = new List<(int Start, int End)>();
                foreach (var seg in segs)
                {
                    var pieces = new List<(int Start, int End)> { seg };
                    foreach (var b in blocks)
                    {
                        var next = new List<(int Start, int End)>();
                        foreach (var p in pieces)
                        {
                            if (b.To <= p.Start || b.From >= p.End) { next.Add(p); continue; }
                            if (b.From > p.Start) next.Add((p.Start, b.From));
                            if (b.To   < p.End)   next.Add((b.To,   p.End));
                        }
                        pieces = next;
                    }
                    result.AddRange(pieces);
                }
                return result;
            };
        }

        // ── 輔助方法 ────────────────────────────────────────────────────────────

        /// <summary>計算派工所需工時（分鐘）。</summary>
        public static decimal CalcRequiredMinutes(WorkOrderOperation op, decimal qty)
        {
            // 準備工時單位（SetupTimeUnit）可與單件工時（TimeUnit）不同，分別換算。
            return OperationTimeUnitExtensions.ToMinutes(op.SetupTime, op.SetupTimeUnit)
                 + OperationTimeUnitExtensions.ToMinutes(op.UnitTime * qty, op.TimeUnit);
        }

        /// <summary>委外用：從 start 往後加 days 個工作日（有工時的日期）。</summary>
        private static DateTime AddWorkingDays(DateTime start, int days, string? plantId, CalendarContext ctx)
        {
            var date = DateOnly.FromDateTime(start);
            int added = 0;
            for (int i = 0; i < 365 && added < days; i++, date = date.AddDays(1))
            {
                var segs = ctx.GetSegments(null, null, date);
                if (segs.Count > 0) added++;
            }
            // DateOnly.ToDateTime(TimeOnly) 不論來源 Kind 一律回傳 Unspecified，
            // 須明確指定 Utc 才能寫回 Dispatch.ScheduledEnd 等 timestamptz 欄位（同 WorkingTimeWalker 修法）。
            return date.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc);
        }

        /// <summary>依規則排序生產批，傳回「每批的 WorkOrder 清單」。</summary>
        private static List<List<WorkOrder>> SortBatchesByCurrentStart(IReadOnlyList<WorkOrder> workOrders) =>
            workOrders.GroupBy(w => w.ProductionBatchId)
                .OrderBy(g => g.SelectMany(w => w.Operations).SelectMany(op => op.Dispatches)
                               .Where(d => d.Status == DispatchStatus.Scheduled && d.ScheduledStart.HasValue)
                               .Select(d => d.ScheduledStart!.Value)
                               .DefaultIfEmpty(DateTime.MaxValue).Min())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.ToList()).ToList();

        private static List<List<WorkOrder>> SortBatches(
            IReadOnlyList<WorkOrder> workOrders, ScheduleRule rule, IReadOnlyDictionary<string, int> batchPriority)
        {
            // 以 BatchId 分組
            var byBatch = workOrders.GroupBy(w => w.ProductionBatchId).ToList();

            // 排序
            IOrderedEnumerable<IGrouping<string, WorkOrder>> sorted = rule switch
            {
                ScheduleRule.EDD  => byBatch.OrderBy(g => g.Min(w => w.PlannedEndDate)),
                ScheduleRule.SPT  => byBatch.OrderBy(g => g.Sum(w =>
                                        w.Operations.Sum(op => (double)CalcRequiredMinutes(op, op.WorkOrder?.Quantity ?? 1)))),
                ScheduleRule.FIFO => byBatch.OrderBy(g => g.Min(w => w.CreatedAt)),
                ScheduleRule.LIFO => byBatch.OrderByDescending(g => g.Min(w => w.CreatedAt)),
                _                 => byBatch.OrderBy(g =>
                                        batchPriority.TryGetValue(g.Key, out var p) ? p : 100),   // Priority（預設）
            };

            return sorted.Select(g => g.ToList()).ToList();
        }

        /// <summary>
        /// 批次內工序排序：BOM 深階先排（半成品先），同深度依 OperationSeq 升序。
        /// </summary>
        private static List<(WorkOrder Wo, WorkOrderOperation Op)> OrderOperationsInBatch(
            IReadOnlyList<WorkOrder> batchWos)
        {
            // 依 BOM 深度排序（無 ParentWorkOrderId = 成品 = 最後排；有 Parent = 半成品 = 先排）
            static int Depth(WorkOrder wo, IReadOnlyList<WorkOrder> all, int depth = 0)
            {
                if (wo.ParentWorkOrderId == null) return depth;
                var parent = all.FirstOrDefault(w => w.WorkOrderId == wo.ParentWorkOrderId);
                return parent == null ? depth : Depth(parent, all, depth + 1);
            }

            return batchWos
                .SelectMany(wo => wo.Operations.Select(op => (wo, op)))
                .OrderByDescending(x => Depth(x.wo, batchWos))
                .ThenBy(x => x.op.OperationSeq)
                .ToList();
        }
    }
}
