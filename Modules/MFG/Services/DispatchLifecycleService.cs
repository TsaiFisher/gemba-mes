using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 派工暫停的進入點（純記憶體，不查資料庫、不存檔）。所有「轉暫停」都要經過這裡，
    /// 才會記下原因與暫停前狀態——恢復時靠這兩個欄位決定回到哪裡、由誰解除。
    /// </summary>
    public static class DispatchHold
    {
        /// <summary>派工轉暫停；已暫停或已結束（完工／取消）的不動。回傳是否有改。</summary>
        public static bool Hold(Dispatch dispatch, DispatchHoldReason reason, DateTime now)
        {
            if (dispatch.Status is DispatchStatus.OnHold or DispatchStatus.Closed or DispatchStatus.Cancelled)
                return false;

            dispatch.HoldFromStatus = dispatch.Status;
            dispatch.HoldReason     = reason;
            dispatch.HoldAt         = now;
            dispatch.Status         = DispatchStatus.OnHold;
            dispatch.UpdatedAt      = now;
            return true;
        }

        /// <summary>派工是否已結束（完工或取消）——不佔產能、不再有後續動作。</summary>
        public static bool IsFinished(DispatchStatus status) =>
            status is DispatchStatus.Closed or DispatchStatus.Cancelled;

        /// <summary>
        /// 工單的派工是否全部結束、可自動轉完工：全部為完工或取消，且至少一張完工。
        /// 暫停**不算**——暫停的剩餘量要嘛恢復生產，要嘛由生管明確結案（<see cref="IDispatchLifecycleService.ShortCloseAsync"/>）；
        /// 舊規則把暫停當完成，工單會帶著沒做完的量靜默完工。全部取消（沒有任何產出）也不算完工。
        /// </summary>
        public static bool IsWorkOrderDone(IEnumerable<Dispatch> dispatches)
        {
            var list = dispatches as IReadOnlyCollection<Dispatch> ?? dispatches.ToList();
            return list.Count > 0
                && list.All(d => IsFinished(d.Status))
                && list.Any(d => d.Status == DispatchStatus.Closed);
        }

        /// <summary>機台狀態是否代表「停用」（不可生產）。與既有 §13.4 副作用的判定一致：可用＝正常或待機。</summary>
        public static bool IsMachineDown(MachineStatus status) =>
            status != MachineStatus.Available && status != MachineStatus.Standby;

        /// <summary>機台停用時轉暫停的狀態（已在機台上投入：換模、首件或生產）。</summary>
        private static readonly DispatchStatus[] HoldOnMachineDown =
        {
            DispatchStatus.SetupInProgress, DispatchStatus.ReadyToStart,
            DispatchStatus.PendingFPI, DispatchStatus.InProgress
        };

        /// <summary>
        /// 機台停用的派工副作用（mfg-domain.md §13.4）：已排的退回待排（重排時換機台）；
        /// 準備中、準備完成、等首件、生產中的轉暫停（原因＝機台停用），不重排——等機台恢復或由生管換機續做。
        /// 不存檔。模具保養到期（ToolingShotService）直接呼叫這裡；一般機台狀態異動走
        /// <see cref="IDispatchLifecycleService.ApplyMachineStatusAsync"/>（含恢復）。
        /// </summary>
        public static async Task<(int Unscheduled, int Held)> ApplyMachineDownAsync(
            ApplicationDbContext db, string machineCode, string? plantId, DateTime now, CancellationToken ct = default)
        {
            // SQL 以資料庫值過濾，但 identity map 回傳的是追蹤中的實體——呼叫端可能剛在記憶體裡改過狀態
            // （例：模具到期時觸發的那張派工剛被設成 Closed，尚未存檔），迴圈內一律以記憶體狀態複核。
            var onMachine = await OpenOnMachineAsync(db, machineCode, plantId, ct);

            int unscheduled = 0, held = 0;
            foreach (var d in onMachine)
            {
                if (d.Status == DispatchStatus.Scheduled)
                {
                    d.Status    = DispatchStatus.Pending;
                    d.UpdatedAt = now;
                    unscheduled++;
                }
                else if (HoldOnMachineDown.Contains(d.Status)
                         && Hold(d, DispatchHoldReason.MachineDown, now))
                {
                    held++;
                }
            }
            return (unscheduled, held);
        }

        internal static Task<List<Dispatch>> OpenOnMachineAsync(
            ApplicationDbContext db, string machineCode, string? plantId, CancellationToken ct) =>
            db.Dispatches
                .Where(d => d.MachineCode == machineCode && d.PlantId == plantId
                         && d.Status != DispatchStatus.Closed && d.Status != DispatchStatus.Cancelled)
                .ToListAsync(ct);
    }

    /// <summary>機台狀態變更對派工的影響筆數。</summary>
    public record MachineDispatchEffect(int Unscheduled, int Held, int Resumed);

    /// <summary>
    /// 派工生命週期中「排程以外」的狀態轉換：機台停用／恢復、暫停恢復、換機續做、剩餘量結案、工單作廢連帶取消。
    /// 桌機、手機與模具保養各自呼叫同一份實作，避免三處複本各改一邊。全部不呼叫 SaveChanges，由呼叫端同交易提交。
    /// </summary>
    public interface IDispatchLifecycleService
    {
        /// <summary>
        /// 機台狀態變更的派工副作用（mfg-domain.md §13.4）。
        /// 停用：已排的退回待排（重排時換機台）；準備中、準備完成、等首件、生產中的轉暫停（原因＝機台停用），
        /// 不重排——等機台恢復或由生管換機續做。
        /// 恢復可用：因機台停用而暫停的派工自動恢復。
        /// </summary>
        Task<MachineDispatchEffect> ApplyMachineStatusAsync(
            string machineCode, string? plantId, MachineStatus newStatus, DateTime now, CancellationToken ct = default);

        /// <summary>暫停的派工恢復到暫停前狀態；回傳恢復後的狀態。非暫停中丟 <see cref="InvalidOperationException"/>。</summary>
        Task<DispatchStatus> ResumeAsync(Dispatch dispatch, DateTime now, CancellationToken ct = default);

        /// <summary>
        /// 暫停的派工改到另一台機台繼續生產。原機台上未結束的上工、準備紀錄一併結束（工時照實入帳）。
        /// 尚無產出：整張派工改掛新機台。已有產出：原派工以已完成量結案，剩餘量另開一張續做派工（同拆分群組）。
        /// 續做的派工鎖定在新機台、狀態為待排，需重新換模（準備）與首件檢查；排程重排時只排在這台。
        /// 回傳承接剩餘量的派工（可能就是原派工）。
        /// </summary>
        Task<Dispatch> SwitchMachineAsync(Dispatch dispatch, string newMachineCode, DateTime now, CancellationToken ct = default);

        /// <summary>
        /// 暫停的派工不再生產剩餘量：有產出→以已完成量完工；沒有產出→取消。
        /// 之後若工單其餘派工都已結束，工單自動轉完工。
        /// </summary>
        Task ShortCloseAsync(Dispatch dispatch, DateTime now, CancellationToken ct = default);

        /// <summary>工單作廢：未結束的派工全部取消，未結束的準備紀錄一併關閉。回傳取消筆數。</summary>
        Task<int> CancelOpenDispatchesAsync(string workOrderId, DateTime now, CancellationToken ct = default);
    }

    public class DispatchLifecycleService : IDispatchLifecycleService
    {
        private readonly ApplicationDbContext _db;
        private readonly IFpiGateService _fpiGate;
        private readonly IProductionCostAccrualService _costAccrual;

        public DispatchLifecycleService(
            ApplicationDbContext db, IFpiGateService fpiGate, IProductionCostAccrualService costAccrual)
        {
            _db = db;
            _fpiGate = fpiGate;
            _costAccrual = costAccrual;
        }

        public async Task<MachineDispatchEffect> ApplyMachineStatusAsync(
            string machineCode, string? plantId, MachineStatus newStatus, DateTime now, CancellationToken ct = default)
        {
            if (DispatchHold.IsMachineDown(newStatus))
            {
                var (unscheduled, held) = await DispatchHold.ApplyMachineDownAsync(_db, machineCode, plantId, now, ct);
                return new MachineDispatchEffect(unscheduled, held, 0);
            }

            var resumed = 0;
            foreach (var d in await DispatchHold.OpenOnMachineAsync(_db, machineCode, plantId, ct))
            {
                if (d.Status != DispatchStatus.OnHold || d.HoldReason != DispatchHoldReason.MachineDown) continue;
                await ResumeAsync(d, now, ct);
                resumed++;
            }
            return new MachineDispatchEffect(0, 0, resumed);
        }

        public async Task<DispatchStatus> ResumeAsync(Dispatch dispatch, DateTime now, CancellationToken ct = default)
        {
            if (dispatch.Status != DispatchStatus.OnHold)
                throw new InvalidOperationException($"Dispatch {dispatch.DispatchId} is not on hold.");

            var target = await ResolveResumeStatusAsync(dispatch, ct);

            dispatch.Status         = target;
            dispatch.HoldReason     = null;
            dispatch.HoldFromStatus = null;
            dispatch.HoldAt         = null;
            dispatch.UpdatedAt      = now;
            return target;
        }

        /// <summary>
        /// 恢復後回到哪個狀態：
        /// 首件不合格→等首件檢查（重新量測）；有記錄暫停前狀態→回原狀態；
        /// 舊資料（沒記錄）→有產出視為生產中，否則依首件閘口決定等首件或準備完成。
        /// 回到「準備完成」前一律再過首件閘口，免得沒通過首件的派工被放行開工。
        /// </summary>
        private async Task<DispatchStatus> ResolveResumeStatusAsync(Dispatch dispatch, CancellationToken ct)
        {
            if (dispatch.HoldReason == DispatchHoldReason.FpiFailed) return DispatchStatus.PendingFPI;

            var target = dispatch.HoldFromStatus
                         ?? (dispatch.QtyDone > 0 ? DispatchStatus.InProgress : DispatchStatus.ReadyToStart);

            if (target == DispatchStatus.ReadyToStart && (await _fpiGate.EvaluateAsync(dispatch, ct)).IsBlocked)
                return DispatchStatus.PendingFPI;

            return target;
        }

        public async Task<Dispatch> SwitchMachineAsync(
            Dispatch dispatch, string newMachineCode, DateTime now, CancellationToken ct = default)
        {
            if (dispatch.Status != DispatchStatus.OnHold)
                throw new InvalidOperationException($"Dispatch {dispatch.DispatchId} is not on hold.");

            await CloseOpenWorkAsync(dispatch, now, ct);

            Dispatch continuation;
            if (dispatch.QtyDone <= 0)
            {
                continuation = dispatch;
            }
            else
            {
                continuation = new Dispatch
                {
                    DispatchId           = await NextChildIdAsync(dispatch.DispatchId, ct),
                    WorkOrderId          = dispatch.WorkOrderId,
                    OperationSeq         = dispatch.OperationSeq,
                    PlantId              = dispatch.PlantId,
                    PrevDispatchId       = dispatch.PrevDispatchId,
                    SplitGroupId         = dispatch.SplitGroupId ?? dispatch.DispatchId,
                    Quantity             = dispatch.Quantity - dispatch.QtyDone,
                    IsOutsource          = dispatch.IsOutsource,
                    ToolingCode          = dispatch.ToolingCode,
                    PlannedStartDate     = dispatch.PlannedStartDate,
                    PlannedEndDate       = dispatch.PlannedEndDate,
                    MaterialRisk         = dispatch.MaterialRisk,
                    CreatedAt            = now,
                    CreatedByRoleId      = dispatch.CreatedByRoleId,
                    OrganizationUnitPath = dispatch.OrganizationUnitPath
                };
                _db.Dispatches.Add(continuation);

                // 原派工只保留在原機台實際做完的量——與拆分同一語意（同群組數量加總＝原派工量）
                dispatch.SplitGroupId = continuation.SplitGroupId;
                dispatch.Quantity     = dispatch.QtyDone;
                dispatch.Status       = DispatchStatus.Closed;
                ClearHold(dispatch);
                dispatch.UpdatedAt    = now;
            }

            continuation.MachineCode     = newMachineCode;
            continuation.IsMachineLocked = true;
            continuation.Status          = DispatchStatus.Pending;
            continuation.ScheduledStart  = null;
            continuation.ScheduledEnd    = null;
            ClearHold(continuation);
            continuation.UpdatedAt       = now;
            return continuation;
        }

        public async Task ShortCloseAsync(Dispatch dispatch, DateTime now, CancellationToken ct = default)
        {
            if (dispatch.Status != DispatchStatus.OnHold)
                throw new InvalidOperationException($"Dispatch {dispatch.DispatchId} is not on hold.");

            await CloseOpenWorkAsync(dispatch, now, ct);

            if (dispatch.QtyDone > 0)
            {
                dispatch.Status = DispatchStatus.Closed;
            }
            else
            {
                // 取消的派工不佔時段，否則準備看板、甘特仍會顯示它
                dispatch.Status         = DispatchStatus.Cancelled;
                dispatch.ScheduledStart = null;
                dispatch.ScheduledEnd   = null;
            }
            ClearHold(dispatch);
            dispatch.UpdatedAt = now;

            await CompleteWorkOrderIfDoneAsync(dispatch.WorkOrderId, now, ct);
        }

        public async Task<int> CancelOpenDispatchesAsync(string workOrderId, DateTime now, CancellationToken ct = default)
        {
            var open = await _db.Dispatches
                .Where(d => d.WorkOrderId == workOrderId
                         && d.Status != DispatchStatus.Closed && d.Status != DispatchStatus.Cancelled)
                .ToListAsync(ct);

            foreach (var d in open)
            {
                await CloseOpenWorkAsync(d, now, ct);
                d.Status         = DispatchStatus.Cancelled;
                d.ScheduledStart = null;
                d.ScheduledEnd   = null;
                ClearHold(d);
                d.UpdatedAt      = now;
            }
            return open.Count;
        }

        /// <summary>
        /// 結束派工上未關閉的上工與準備紀錄，工時照實入帳（人確實在線上做過）。
        /// 機台停用時人員多半已離線但沒按下工，若不在這裡收掉，上工紀錄會一直開著、工時無限累計。
        /// </summary>
        private async Task CloseOpenWorkAsync(Dispatch dispatch, DateTime now, CancellationToken ct)
        {
            var sessions = await _db.ProductionSessions
                .Where(s => s.DispatchId == dispatch.DispatchId && s.Status == ProductionSessionStatus.Open)
                .ToListAsync(ct);
            foreach (var s in sessions)
            {
                s.EndAt     = now;
                s.Status    = ProductionSessionStatus.Closed;
                s.UpdatedAt = now;
                await _costAccrual.AccrueOnSessionStopAsync(s);
            }

            var setups = await _db.ProductionSetups
                .Where(s => s.DispatchId == dispatch.DispatchId && s.Status == ProductionSetupStatus.Open)
                .ToListAsync(ct);
            if (setups.Count == 0) return;
            foreach (var s in setups)
            {
                s.SetupEndAt = now;
                s.Status     = ProductionSetupStatus.Closed;
                s.UpdatedAt  = now;
            }
            await _costAccrual.AccrueOnSetupDoneAsync(setups, dispatch);
        }

        private async Task CompleteWorkOrderIfDoneAsync(string workOrderId, DateTime now, CancellationToken ct)
        {
            // 讀追蹤中實體（不可 AsNoTracking）：本次剛改的派工狀態尚未存檔
            var all = await _db.Dispatches.Where(d => d.WorkOrderId == workOrderId).ToListAsync(ct);
            if (!DispatchHold.IsWorkOrderDone(all)) return;

            var wo = await _db.WorkOrders.FirstOrDefaultAsync(w => w.WorkOrderId == workOrderId, ct);
            if (wo is { Status: WorkOrderStatus.InProgress })
            {
                wo.Status    = WorkOrderStatus.Completed;
                wo.UpdatedAt = now;
            }
        }

        /// <summary>續做派工編號：沿用拆分的「原編號-sN」規則，取既有最大序號往後編（同拆分，避免撞主鍵）。</summary>
        private async Task<string> NextChildIdAsync(string parentId, CancellationToken ct)
        {
            var prefix = $"{parentId}-s";
            var existing = await _db.Dispatches.IgnoreQueryFilters()
                .Where(d => d.DispatchId.StartsWith(prefix))
                .Select(d => d.DispatchId)
                .ToListAsync(ct);
            // 同一交易內剛加進來、尚未存檔的也要算
            existing.AddRange(_db.ChangeTracker.Entries<Dispatch>()
                .Where(e => e.State == EntityState.Added && e.Entity.DispatchId.StartsWith(prefix))
                .Select(e => e.Entity.DispatchId));

            var next = 1;
            foreach (var id in existing)
                if (int.TryParse(id.AsSpan(prefix.Length), out var n) && n >= next) next = n + 1;
            return $"{prefix}{next}";
        }

        private static void ClearHold(Dispatch d)
        {
            d.HoldReason     = null;
            d.HoldFromStatus = null;
            d.HoldAt         = null;
        }
    }
}
