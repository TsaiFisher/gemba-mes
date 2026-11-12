using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.BAS.Services;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 機台「可不可以用來生產」的單一判定來源：排程引擎挑機台、桌機與手機上工共用。
    ///
    /// <para>原本只有排程引擎有這條規則（Available 必含；Standby 看 <c>MfgStandbySchedulable</c>），
    /// 上工完全不看機台狀態——機台報修時，<c>MachineStatusEvent</c> 只把已排定退回待排、生產中轉暫停，
    /// **可開工**的派工照樣能在故障停機的機台上開工。規則分三處各寫一份，遲早會漂移，故收斂到這裡。</para>
    /// </summary>
    public static class MachineAvailability
    {
        /// <summary>上工閘口模式（廠別參數 <c>MfgMachineStatusGate</c>）。</summary>
        public const string GateNone    = "None";
        public const string GateWarning = "Warning";
        public const string GateBlock   = "Block";

        /// <summary>
        /// 未設定時的預設值是 Warning 而不是 Block：機台修好了但狀態沒人去改，
        /// Block 會讓現場突然不能開工。先以警示跑一段時間、確認現場有落實更新狀態，再由各廠改成 Block。
        /// </summary>
        public const string DefaultGate = GateWarning;

        /// <summary>可排程／可開工的機台狀態：Available 必含；Standby 依廠別參數 <c>MfgStandbySchedulable</c>。</summary>
        public static async Task<MachineStatus[]> UsableStatusesAsync(
            IPlantParamService plantParams, string? plantId, CancellationToken ct = default)
        {
            var standby = await plantParams.GetAsync(ProjectZSystemParamKeys.MfgStandbySchedulable, plantId, ct);
            return string.Equals(standby, "true", StringComparison.OrdinalIgnoreCase)
                ? [MachineStatus.Available, MachineStatus.Standby]
                : [MachineStatus.Available];
        }

        /// <summary>
        /// 開工前檢查機台狀態。查不到機台（生產線未登錄線別、派工沒掛機台）一律放行——
        /// 那不是「機台不能用」，而是「沒有機台可以看」。
        /// </summary>
        public static async Task<MachineStartCheck> CheckStartAsync(
            ApplicationDbContext db, IPlantParamService plantParams, string? plantId, string? machineCode,
            CancellationToken ct = default)
        {
            var gate = (await plantParams.GetAsync(ProjectZSystemParamKeys.MfgMachineStatusGate, plantId, ct) ?? DefaultGate).Trim();
            if (string.Equals(gate, GateNone, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(machineCode))
                return MachineStartCheck.Ok;

            // WorkCenterMachine 不是 IPlantEntity，廠別要經由 WorkCenter 帶條件（同 MachineStatusEvent 的理由）
            var status = await db.WorkCenterMachines.AsNoTracking()
                .Where(m => m.MachineCode == machineCode && m.WorkCenter!.PlantId == plantId)
                .Select(m => (MachineStatus?)m.MachineStatus)
                .FirstOrDefaultAsync(ct);
            if (status == null || (await UsableStatusesAsync(plantParams, plantId, ct)).Contains(status.Value))
                return MachineStartCheck.Ok;

            return new MachineStartCheck(
                Blocked: string.Equals(gate, GateBlock, StringComparison.OrdinalIgnoreCase),
                Warned:  !string.Equals(gate, GateBlock, StringComparison.OrdinalIgnoreCase),
                Status:  status);
        }

        /// <summary>
        /// 上工時現場改選了別台機台 → 派工改掛到實際使用的機台。回傳是否有改。
        ///
        /// <para>原本改選的機台只記在 <c>ProductionSession.MachineCode</c>，<c>Dispatch.MachineCode</c> 仍是排定的那台；
        /// 派工一進生產中就凍結，重跑排程不再動它的機台，於是甘特、準備看板、ETA 與凍結佔用都把它算在原機台，
        /// 實際在做的那台反被當成空閒、可能再被排入別的派工。現場可以選機台，系統就只能跟著實際走。</para>
        ///
        /// <para>未登錄線別的生產線以工作中心代碼當 <c>MachineCode</c>（整條線視為單一排程資源），
        /// 這種派工不改掛——改成某台機台會讓排程與甘特認不出這條線。</para>
        ///
        /// 呼叫端須先驗證 <paramref name="actualMachine"/> 是本廠有效機台（桌機、手機各有自己的規則）。
        /// </summary>
        public static async Task<bool> SyncDispatchToActualMachineAsync(
            ApplicationDbContext db, Dispatch dispatch, string? actualMachine, DateTime now, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(actualMachine) || actualMachine == dispatch.MachineCode) return false;

            var opWorkCenter = await db.WorkOrderOperations.AsNoTracking()
                .Where(o => o.WorkOrderId == dispatch.WorkOrderId && o.OperationSeq == dispatch.OperationSeq)
                .Select(o => o.WorkCenterId)
                .FirstOrDefaultAsync(ct);
            if (opWorkCenter != null && dispatch.MachineCode == opWorkCenter) return false;

            dispatch.MachineCode = actualMachine;
            dispatch.UpdatedAt   = now;
            return true;
        }
    }

    /// <summary>開工前的機台狀態檢查結果。Blocked／Warned 至多一個為 true；Status 是機台當下狀態（放行時可能為 null）。</summary>
    public readonly record struct MachineStartCheck(bool Blocked, bool Warned, MachineStatus? Status)
    {
        public static MachineStartCheck Ok => new(false, false, null);
    }
}
