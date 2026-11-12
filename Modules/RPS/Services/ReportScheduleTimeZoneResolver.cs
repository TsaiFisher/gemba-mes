using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Models;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 決定一份排程的「下次執行時刻」要用哪個時區（manual-test-plan §11 #10 收口）。
///
/// <para><b>順序與全站一致</b>：<c>Plant.DefaultTimeZone</c> → 建立者個人時區 →
/// <see cref="ReportScheduleCalculator.FallbackTimeZoneId"/>。
/// 這正是 <c>IUserContext.TimeZoneId</c> 的順序——只是這裡不能用那個服務：
/// 派工器是 <c>BackgroundService</c>，沒有使用者上下文
/// （注入 <c>IUserContext</c> 的話會靜默拿到空值，而不是拋例外）。</para>
///
/// <para>兩邊對「空白算不算有值」的判定也已一致：本類別一向用 <c>IsNullOrWhiteSpace</c>，
/// 而 <c>IUserContext.TimeZoneId</c> 當時用的是 <c>??</c>（只擋 null）——
/// 廠別時區是空白字串時，那邊會跳過個人時區而這邊不會。Core #31（§11 #13）已對齊。</para>
///
/// <para><b>修掉的是什麼</b>：原本兩條路徑各算各的——
/// <c>ReportDispatcherService.AdvanceAsync</c> 讀「建立者的個人時區」
/// （<c>db.Users…Select(u =&gt; u.TimeZoneId)</c>，多數帳號是 null 於是退到 Asia/Taipei），
/// 而 <c>ReportScheduleController</c> 存檔時**根本沒傳時區**（<c>timeZoneId: null</c>），
/// 一律 Asia/Taipei。於是同一份「每日 08:00」的排程，換一個人建立就在不同時刻寄出、
/// 涵蓋不同的一天，而兩張排程在畫面上長得一模一樣——沒有任何欄位看得出差別。
/// 存檔與推進還可能各用一個時區，設定一改下次執行時刻就跳動。</para>
///
/// <para><b>為什麼不必替 <see cref="ReportDefinition"/> 補廠別歸屬</b>：它已經有
/// <c>PlantId</c>（建立時取 <c>UserContext.PlantId</c>），只是刻意不實作 <c>IPlantEntity</c>
/// ——沒有全域查詢過濾，但**資料上這一列本來就知道自己屬於哪個廠**。</para>
/// </summary>
public static class ReportScheduleTimeZoneResolver
{
    /// <summary>
    /// 查出這份排程該用的時區代碼。查不到就回 <c>null</c>，
    /// 由 <see cref="ReportScheduleCalculator"/> 退到它自己的 fallback。
    /// </summary>
    public static async Task<string?> ResolveAsync(
        ApplicationDbContext db, ReportDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.IsNullOrWhiteSpace(definition.PlantId))
        {
            var plantTz = await db.Plants.AsNoTracking()
                .Where(p => p.Id == definition.PlantId)
                .Select(p => p.DefaultTimeZone)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(plantTz)) return plantTz;
        }

        if (string.IsNullOrWhiteSpace(definition.OwnerUserId)) return null;

        var ownerTz = await db.Users.AsNoTracking()
            .Where(u => u.Id == definition.OwnerUserId)
            .Select(u => u.TimeZoneId)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(ownerTz) ? null : ownerTz;
    }
}
