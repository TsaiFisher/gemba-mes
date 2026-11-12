using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// <see cref="IReportToolAccessBatch"/> 的單次查詢實作（§17.9 G1）。
/// </summary>
public class ReportToolAccessBatch : IReportToolAccessBatch
{
    private readonly IReportToolCatalog _catalog;
    private readonly ApplicationDbContext _db;
    private readonly IUserContext _userContext;

    public ReportToolAccessBatch(
        IReportToolCatalog catalog,
        ApplicationDbContext db,
        IUserContext userContext)
    {
        _catalog     = catalog;
        _db          = db;
        _userContext = userContext;
    }

    public async Task<IReadOnlyList<ReportToolAccess>> GetAccessAsync(CancellationToken ct = default)
    {
        var tools = _catalog.All;

        // 沒有作用中角色＝一律不放行。與 PermissionService 的每一支 CanXxx 同語意
        // （它在 activeRoleId 為空時直接 return false，不查 DB）。
        var roleId = _userContext.ActiveRoleId;
        if (string.IsNullOrEmpty(roleId))
            return tools.Select(t => new ReportToolAccess(t, false)).ToList();

        var keys = tools.Select(t => t.ToolKey).ToList();

        // ⚠️ 單次查詢，且只投影出要用的三個旗標。
        // AsNoTracking 在這裡是安全的：單表、無 join、純唯讀投影。
        var grants = await _db.MenuPermissions
            .AsNoTracking()
            .Where(p => p.RoleId == roleId && keys.Contains(p.MenuKey))
            .Select(p => new { p.MenuKey, p.CanRead, p.CanCreate, p.CanPrint })
            .ToDictionaryAsync(x => x.MenuKey, x => x, StringComparer.Ordinal, ct);

        return tools.Select(t =>
        {
            if (!grants.TryGetValue(t.ToolKey, out var g))
                return new ReportToolAccess(t, false);   // 沒有列＝沒授權

            // 動詞依各工具自行宣告，**不可硬寫成 CanRead**——理由與
            // ReportToolCatalog.CheckAsync 上的註解相同（CanRead 恆為 CanCreate 的真超集，
            // 寫死會讓「某支畫面日後另立閘門」變成無痕放寬）。
            var ok = t.Permission switch
            {
                ReportToolPermission.CanRead   => g.CanRead,
                ReportToolPermission.CanCreate => g.CanCreate,
                ReportToolPermission.CanPrint  => g.CanPrint,
                _ => throw new InvalidOperationException(
                         $"工具 {t.ToolKey} 宣告了未知的權限動詞 {t.Permission}。"),
            };

            return new ReportToolAccess(t, ok);
        }).ToList();

        // 註：PermissionService.IsModuleLicensed 目前是 `=> true` 的樁（§17.9 缺口 G3），
        // 故此處不需對應處理。**它日後若真的實作，這裡必須跟著加**，
        // 否則兩條路徑會在「模組未授權」時給出不同答案。
    }
}
