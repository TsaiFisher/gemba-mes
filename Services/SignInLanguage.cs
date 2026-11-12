using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Services;

/// <summary>
/// 桌機登入成功時寫入語系 cookie（<see cref="LanguagePreference.Resolve"/>：個人 → 所在廠別預設 → zh-TW）。
///
/// <para><b>為什麼掛在 cookie 驗證事件而不是登入頁</b>：兩步驟驗證與復原碼登入走 Identity 內建頁面
/// （LoginWith2fa／LoginWithRecoveryCode），專案裡沒有它們的程式碼——原本只有密碼登入頁寫 cookie，
/// 有開 2FA 的帳號登入後一律沒有套用個人或廠別語系（2026-10-27）。<c>OnSignedIn</c> 是三條路徑共同的終點。</para>
///
/// <para><b>只在登入頁的請求套用</b>：切換角色、啟用 2FA 等也會 <c>RefreshSignInAsync</c>，同樣觸發這個事件；
/// 那時若重設語系，會蓋掉使用者在畫面上剛選的語言。手機 API 登入不在此列（手機語系由登入回應的 language 決定）。</para>
/// </summary>
public static class SignInLanguage
{
    private static readonly string[] InteractiveLoginPaths =
    {
        "/Identity/Account/Login",
        "/Identity/Account/LoginWith2fa",
        "/Identity/Account/LoginWithRecoveryCode",
    };

    public static bool IsInteractiveLogin(PathString path) =>
        InteractiveLoginPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    public static async Task OnSignedInAsync(CookieSignedInContext ctx)
    {
        if (!IsInteractiveLogin(ctx.Request.Path)) return;
        var userId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return;

        var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        LanguagePreference.AppendCookie(ctx.Response, await LanguagePreference.ResolveForUserAsync(db, user));
    }
}
