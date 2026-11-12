using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services
{
    /// <summary>工序順序守門判定結果（mfg-domain.md §25.4）。</summary>
    public readonly record struct OperationSequenceCheck(bool Blocked, decimal PrevQtyDone, decimal Attempted);

    /// <summary>
    /// 工序順序守門（mfg-domain.md §25.4）：後製程累計產出不得超過前製程。
    ///
    /// 完整流程由排程保證前工序先做（<c>PrevDispatchId</c> 鏈結＋時段配置），故**只在簡化模式生效**——
    /// 免排程後現場可從任一工序直接開工，第 3 工序先報工會產出「後製程做得比前製程多」的
    /// 不可能資料（在製品數量可能為負）。
    ///
    /// 桌機報工／完工與手機報工三條入口共用同一份判定，避免雙軌漂移。
    /// </summary>
    public interface IOperationSequenceGuard
    {
        /// <summary>
        /// 檢查本次報工是否會讓本工序累計產出超過前工序。
        /// 首道工序（<c>PrevDispatchId == null</c>）與完整模式一律放行。
        /// </summary>
        Task<OperationSequenceCheck> CheckAsync(Dispatch dispatch, decimal qtyOk, CancellationToken ct = default);
    }
}
