using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 一次權限判定的結果。不通過時 <see cref="Reason"/> 直接寫進
/// <c>ReportRun.ErrorMessage</c>——管理者要看得出「為什麼停了」，而不是只看到停了。
/// </summary>
public readonly record struct SchedulePermissionResult(bool Granted, string? Reason)
{
    public static SchedulePermissionResult Ok() => new(true, null);
    public static SchedulePermissionResult Denied(string reason) => new(false, reason);
}

/// <summary>
/// 排程路徑專用的權限檢查（§7.8 S1／S2／S3）。
///
/// <para><b>為什麼不用 <c>IPermissionService</c>：</b>那支服務讀
/// <c>IUserContext.ActiveRoleId</c>——也就是「目前登入者」。派工器是
/// <c>BackgroundService</c>，沒有登入者；在那裡注入 <c>IUserContext</c> 只會拿到
/// 空的角色，於是每一個檢查都回 false，排程**全面靜默不執行**。
/// 所以這裡改成「明確帶入 roleId」的查詢，不依賴任何環境狀態。</para>
///
/// <para><b>S1 的重點是「每次執行都重查」</b>，不是建立時查一次：
/// 建立排程時是業務主管、半年後調職，排程仍在每月寄出銷售明細——這是真實的洩漏路徑，
/// 而且完全沒有徵兆，因為排程本來就是無人看管地跑。</para>
/// </summary>
public interface IReportSchedulePermissionChecker
{
    /// <summary>owner 是否仍可執行該工具（§7.8 S1）。帳號停用、角色被移除、失去 MenuKey 權限都算失效。</summary>
    Task<SchedulePermissionResult> CheckOwnerAsync(
        string userId, string roleId, string toolKey, CancellationToken ct = default);

    /// <summary>收件人是否仍可收到該工具的報表（§7.8 S3）。</summary>
    Task<SchedulePermissionResult> CheckRecipientAsync(
        string userId, string toolKey, CancellationToken ct = default);
}

/// <inheritdoc />
public class ReportSchedulePermissionChecker : IReportSchedulePermissionChecker
{
    private readonly ApplicationDbContext _db;
    private readonly IReportToolCatalog _catalog;

    public ReportSchedulePermissionChecker(ApplicationDbContext db, IReportToolCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<SchedulePermissionResult> CheckOwnerAsync(
        string userId, string roleId, string toolKey, CancellationToken ct = default)
    {
        if (_catalog.Find(toolKey) is not { } tool)
            return SchedulePermissionResult.Denied($"報表工具 {toolKey} 已不存在");

        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(roleId))
            return SchedulePermissionResult.Denied("排程缺少擁有者或角色");

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return SchedulePermissionResult.Denied("擁有者帳號已刪除");

        // 停用在本專案是以 LockoutEnd 表達（Identity 的鎖定機制），沒有另外的 IsActive 欄位。
        if (user.LockoutEnd is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow)
            return SchedulePermissionResult.Denied("擁有者帳號已停用");

        // 角色被整個刪掉，或使用者已不再屬於這個角色——兩者都是調職／異動的常見形態。
        var roleExists = await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct);
        if (!roleExists)
            return SchedulePermissionResult.Denied("排程使用的角色已刪除");

        var stillInRole = await _db.UserRoles.AsNoTracking()
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct);
        if (!stillInRole)
            return SchedulePermissionResult.Denied("擁有者已不屬於該角色");

        if (!await HasPermissionAsync(roleId, toolKey, tool.Permission, ct))
            return SchedulePermissionResult.Denied($"該角色已無 {toolKey} 的執行權限");

        return SchedulePermissionResult.Ok();
    }

    public async Task<SchedulePermissionResult> CheckRecipientAsync(
        string userId, string toolKey, CancellationToken ct = default)
    {
        if (_catalog.Find(toolKey) is not { } tool)
            return SchedulePermissionResult.Denied($"報表工具 {toolKey} 已不存在");

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return SchedulePermissionResult.Denied("收件人帳號已刪除");

        if (user.LockoutEnd is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow)
            return SchedulePermissionResult.Denied("收件人帳號已停用");

        // 收件人以**他自己的所有角色**判定（不是 owner 的角色）：
        // 用 owner 的角色去判定，等於讓 owner 的權限傳染給收件人，那正是 §八 要防的事。
        var roleIds = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        foreach (var roleId in roleIds)
            if (await HasPermissionAsync(roleId, toolKey, tool.Permission, ct))
                return SchedulePermissionResult.Ok();

        return SchedulePermissionResult.Denied($"收件人無 {toolKey} 的檢視權限");
    }

    /// <summary>
    /// ⚠️ 動詞依工具自行宣告，**不可在這裡硬寫成 <c>CanRead</c>**。2026-08-14 裁決後
    /// 所有報表畫面的閘門恰好都是 <c>CanRead</c>，但那是現況不是契約——寫死會讓日後
    /// 某支畫面另立閘門變成無痕放寬（<c>CanRead</c> 恆為 <c>CanCreate</c> 的真超集）。
    /// 這裡的判定必須與 <c>ReportToolCatalog</c> 那一套一致。
    /// </summary>
    private Task<bool> HasPermissionAsync(
        string roleId, string menuKey, ReportToolPermission permission, CancellationToken ct)
        => permission switch
        {
            ReportToolPermission.CanRead => _db.MenuPermissions.AsNoTracking()
                .AnyAsync(p => p.RoleId == roleId && p.MenuKey == menuKey && p.CanRead, ct),
            ReportToolPermission.CanCreate => _db.MenuPermissions.AsNoTracking()
                .AnyAsync(p => p.RoleId == roleId && p.MenuKey == menuKey && p.CanCreate, ct),
            ReportToolPermission.CanPrint => _db.MenuPermissions.AsNoTracking()
                .AnyAsync(p => p.RoleId == roleId && p.MenuKey == menuKey && p.CanPrint, ct),
            _ => throw new InvalidOperationException(
                     $"工具 {menuKey} 宣告了未知的權限動詞 {permission}。"),
        };
}
