using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>
    /// 首件檢查（FPI）閘口判定結果（mfg-domain.md §26.2）。
    /// </summary>
    /// <param name="RequiresFpi">該派工所屬工序是否要求首件檢查（<c>WorkOrderOperation.RequiresFpi</c>）。</param>
    /// <param name="HasPassed">該派工是否已有判定為 Pass 的 FPI 檢驗單。</param>
    public readonly record struct FpiGateResult(bool RequiresFpi, bool HasPassed)
    {
        /// <summary>是否應擋下開工：要求首件檢查但尚未通過。</summary>
        public bool IsBlocked => RequiresFpi && !HasPassed;
    }

    /// <summary>
    /// 首件檢查（FPI）閘口（mfg-domain.md §26）。
    ///
    /// **以事實判定，不以派工狀態判定**——舊版守門條件為 <c>Status == PendingFPI</c>，
    /// 而該狀態只能經備料（SetupDone）進入，因此「不做備料直接開工」即可繞過（§26.1 缺口 A）。
    /// 本服務改查「工序是否要求 FPI」與「是否已有 Pass 的 FPI 檢驗單」兩項事實，
    /// 不論派工處於 Scheduled、ReadyToStart 或（簡化模式的）Pending 都成立。
    ///
    /// 桌機（<c>DispatchController</c>）與手機（<c>MobileMfgController</c>）共用本服務，
    /// 避免兩端各寫一份判定而再次漂移（§26.1 缺口 B 即為此類漂移）。
    /// </summary>
    public interface IFpiGateService
    {
        /// <summary>判定指定派工的 FPI 閘口狀態。</summary>
        Task<FpiGateResult> EvaluateAsync(Dispatch dispatch, CancellationToken ct = default);

        /// <summary>
        /// 套用 FPI 判定結果到派工狀態：Pass → <see cref="DispatchStatus.ReadyToStart"/>（解鎖）、
        /// Fail → <see cref="DispatchStatus.OnHold"/>（暫停）。不呼叫 SaveChanges，由呼叫端同交易提交。
        /// </summary>
        void ApplyResult(Dispatch dispatch, bool passed, DateTime now);
    }
}
