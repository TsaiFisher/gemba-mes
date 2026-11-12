using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Services;

/// <summary>
/// 語系切換的兩個效果：本次瀏覽器的 culture cookie，以及帳號偏好（ApplicationUser.Language）。
/// 帳號偏好是推播、手機登入預設語系等「不在使用者請求裡」產生內容時唯一讀得到的語系；
/// 只寫 cookie 的話，那些地方永遠停在管理員當初設定的值。
/// 寫帳號偏好是狀態變更，只能由 POST（桌機帶 anti-forgery、手機走 JSON API）觸發——GET 只寫 cookie。
/// </summary>
public static class LanguagePreference
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { "zh-TW", "en-US" };

    /// <summary>個人與廠別都沒有（有效）設定時的語系。</summary>
    public const string Fallback = "zh-TW";

    /// <summary>
    /// 介面語系的解析順序：<b>個人語系 → 所在廠別的預設語系 → zh-TW</b>（2026-10-27 使用者裁決「個人優先、廠別補位」）。
    /// <para>原本 <c>Plant.DefaultLanguage</c> 只被存起來、在廠別清單上顯示，沒有任何地方讀它決定語系。
    /// 個人語系留空（使用者維護選「依廠別預設」）的帳號，登入與切換廠別時採用該廠的預設語系；
    /// 一旦本人在畫面上切換語系，就會寫成個人偏好（見 <see cref="PersistAsync"/>），之後以個人為準。</para>
    /// <para>不支援的值（舊資料自由輸入的字串）視同未設定，往下一層找，不會原樣丟給文化設定。</para>
    /// </summary>
    public static string Resolve(string? personal, string? plantDefault) =>
        personal != null && Supported.Contains(personal) ? personal
        : plantDefault != null && Supported.Contains(plantDefault) ? plantDefault
        : Fallback;

    /// <summary>
    /// 依 <see cref="Resolve"/> 解析某位使用者的語系。<paramref name="plantId"/> 省略時取使用者目前的作用廠別
    /// （切換廠別時傳入目標廠別）。
    /// </summary>
    public static async Task<string> ResolveForUserAsync(ApplicationDbContext db, ApplicationUser? user, string? plantId = null)
    {
        var plant = plantId ?? user?.ActivePlantId;
        var plantDefault = string.IsNullOrEmpty(plant) ? null
            : await db.Plants.AsNoTracking().Where(p => p.Id == plant).Select(p => p.DefaultLanguage).FirstOrDefaultAsync();
        return Resolve(user?.Language, plantDefault);
    }

    /// <summary>這位使用者有沒有自己的（有效）語系設定；沒有＝跟著廠別預設走。</summary>
    public static bool HasPersonal(ApplicationUser? user) =>
        user?.Language != null && Supported.Contains(user.Language);

    public static void AppendCookie(HttpResponse response, string culture) =>
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax
            });

    /// <summary>寫回帳號偏好。失敗不丟例外——cookie 已是這次請求要的效果，偏好下次切換時會再寫。</summary>
    public static async Task PersistAsync(ApplicationDbContext db, string? userId, string culture)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var user = await db.Users.FindAsync(userId);
        if (user == null || user.Language == culture) return;
        user.Language = culture;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { }
    }
}
