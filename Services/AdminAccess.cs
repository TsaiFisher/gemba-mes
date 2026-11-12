using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Services;

/// <summary>
/// 「只限系統管理員」的判斷：看<b>作用中角色</b>是不是 Admin，不看「所屬角色裡有沒有 Admin」。
///
/// <para><b>為什麼不用 <c>[Authorize(Roles = "Admin")]</c></b>：Identity 的角色宣告是使用者<b>所屬的全部角色</b>。
/// 屬於 Admin 的人把作用中角色切成操機員後，選單照作用中角色只剩操機員的項目，
/// 直接輸入網址卻仍進得了帳號、角色、權限設定——兩套規則對同一個人給出相反答案。
/// 反過來，權限矩陣若勾了這些項目給非 Admin 角色，選單會出現、點進去卻是 403
///（2026-11-11 筆電驗收：「管理人員」看得到管理系統的項目卻全部被拒）。</para>
///
/// <para>選單以 <see cref="MenuItem.Roles"/> 標記只限 Admin 的項目，側邊欄與權限矩陣都依本類別判斷，
/// 規則只有一份。被移除或廠別失效的作用中角色，<see cref="IUserContext.ActiveRoleId"/> 為 null，一律不算。</para>
/// </summary>
public static class AdminAccess
{
    /// <summary>系統管理員角色名稱。</summary>
    public const string RoleName = "Admin";

    /// <summary>作用中角色是否為系統管理員。</summary>
    public static async Task<bool> IsActiveAdminAsync(IUserContext userContext, ApplicationDbContext db,
        CancellationToken ct = default)
    {
        var roleId = userContext.ActiveRoleId;
        if (string.IsNullOrEmpty(roleId)) return false;
        return await db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId && r.Name == RoleName, ct);
    }

    /// <summary>以目前請求的服務判斷（給 View／Filter 用）。</summary>
    public static Task<bool> IsActiveAdminAsync(HttpContext http) =>
        IsActiveAdminAsync(http.RequestServices.GetRequiredService<IUserContext>(),
            http.RequestServices.GetRequiredService<ApplicationDbContext>(), http.RequestAborted);

    /// <summary>選單項目是否只限特定角色（目前只用於 Admin）。</summary>
    public static bool IsRoleRestricted(MenuItem item) => item.Roles.Count > 0;
}

/// <summary>
/// 只限作用中角色為 Admin 的端點（取代 <c>[Authorize(Roles = "Admin")]</c>，理由見 <see cref="AdminAccess"/>）。
/// 未登入回 Challenge；已登入但作用中角色不是 Admin 回 Forbid（頁面由 /Error/403 顯示，API 為 403）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireActiveAdminAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }
        if (!await AdminAccess.IsActiveAdminAsync(context.HttpContext))
            context.Result = new ForbidResult();
    }
}

/// <summary>
/// 依權限矩陣把關的端點：作用中角色對 <see cref="MenuKey"/> 要有 <see cref="Action"/> 權限。
/// 用在原本寫死 Admin、改為可授權的管理畫面（稽核紀錄、代碼維護、系統參數、簽核範本、簽核設定），
/// 讓選單看得到的人點得進去、選單看不到的人進不去。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireMenuPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    public RequireMenuPermissionAttribute(string menuKey, MenuPermissionAction action = MenuPermissionAction.Read)
    {
        MenuKey = menuKey;
        Action = action;
    }

    public string MenuKey { get; }
    public MenuPermissionAction Action { get; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }
        // 方法上的標記優先於類別上的（類別標 Read 當預設，個別動作再標 Create／Update…）
        var closest = context.ActionDescriptor.EndpointMetadata.OfType<RequireMenuPermissionAttribute>().LastOrDefault();
        // 以值比較：EndpointMetadata 裡的實例不保證就是正在執行的這一個；值相同時兩個都檢查也無妨
        if (closest != null && (closest.MenuKey != MenuKey || closest.Action != Action)) return;

        var permissions = context.HttpContext.RequestServices.GetRequiredService<IPermissionService>();
        var allowed = Action switch
        {
            MenuPermissionAction.Create => await permissions.CanCreate(MenuKey),
            MenuPermissionAction.Update => await permissions.CanUpdate(MenuKey),
            MenuPermissionAction.Delete => await permissions.CanDelete(MenuKey),
            MenuPermissionAction.Print  => await permissions.CanPrint(MenuKey),
            _                           => await permissions.CanRead(MenuKey),
        };
        if (!allowed) context.Result = new ForbidResult();
    }
}

/// <summary>權限矩陣的動作欄。</summary>
public enum MenuPermissionAction { Read, Create, Update, Delete, Print }
