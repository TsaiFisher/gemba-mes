using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.RPS.Contracts;

/// <summary>
/// 一次撈完目前使用者對**全部**報表工具的授權判定（§8.4 的 L1 過濾、§17.9 缺口 G1）。
///
/// <para><b>為什麼不直接用 <see cref="IReportToolCatalog.GetAllWithAccessAsync"/>：</b>
/// 那支是 <c>foreach + await</c> 逐支呼叫 <c>IPermissionService</c>，而每個 <c>CanXxx</c>
/// 都是一次 <c>MenuPermissions.AnyAsync</c>——**N 支工具就是 N 次 DB 往返**。
/// 畫面每頁跑一次尚可接受，但 Phase 2 是**每則提問**都要重組工具 schema，
/// 那正是 §17.9 G1 點名的缺口。本介面改成單次查詢後在記憶體判定。</para>
///
/// <para><b>刻意不改 <see cref="ReportToolCatalog"/> 本身</b>：它是 Phase 0-B 已上線、
/// 已有守門測試的類別，且其建構子被既有測試以 <c>(tools, permissionService)</c> 形式使用。
/// 兩條路徑的判定結果必須永遠一致，由 <c>ReportToolAccessBatchTests</c> 逐支比對守門
/// ——**這是複製判定邏輯的代價，必須用測試買回來**。</para>
/// </summary>
public interface IReportToolAccessBatch
{
    /// <summary>
    /// 全部工具＋授權旗標，依 <c>ToolKey</c> 排序。**一次 DB 查詢**。
    /// </summary>
    Task<IReadOnlyList<ReportToolAccess>> GetAccessAsync(CancellationToken ct = default);
}
