using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>報表畫面上顯示「對象廠別」用的一筆資料：代碼、依語系的顯示名稱、預設時區。</summary>
public sealed record ReportPlantInfo(string Id, string Label, string? TimeZoneId);

/// <summary>
/// 報表排程、收件匣、執行紀錄共用的廠別／時區顯示。
///
/// <para><b>為什麼需要</b>（2026-10-07 人工驗收）：排程建在哪個廠別，報表就只查那個廠別的資料，
/// 期間與執行時刻也依那個廠別的時區換算；但畫面上原本完全看不到廠別。排程建在 P2、資料在 ADM 時，
/// 結果是「狀態成功、0 筆」，沒有任何線索指出原因。排程維護又是管理視角（看得到全部廠別），
/// 所以不同廠別的排程混在同一張清單裡也分不出來。</para>
/// </summary>
public static class ReportPlantDisplay
{
    /// <summary>全部廠別的對照表（代碼 → 顯示資料）。英文介面優先用英文名稱，沒填就退回中文名稱。</summary>
    public static async Task<Dictionary<string, ReportPlantInfo>> LoadAsync(
        ApplicationDbContext db, CancellationToken ct = default)
    {
        var english = CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var plants = await db.Plants.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.NameEn, p.DefaultTimeZone })
            .ToListAsync(ct);

        return plants.ToDictionary(
            p => p.Id,
            p => new ReportPlantInfo(
                p.Id,
                $"{p.Id} {(english && !string.IsNullOrWhiteSpace(p.NameEn) ? p.NameEn : p.Name)}",
                p.DefaultTimeZone));
    }

    /// <summary>
    /// 把 UTC 時刻換成**排程所屬時區**的當地時間。
    ///
    /// <para>原本清單用 <c>IDateTimeService.ToLocalTime</c>＝**看的人**的時區：在 ADM 看 P2 的排程，
    /// 「每日 08:00」會顯示成台北的 20:00，看起來像設定錯了。執行時刻是依排程所屬時區算的，
    /// 顯示也要用同一個時區，並把時區印出來。</para>
    /// </summary>
    /// <returns>當地時間；時區解不開時回 <c>null</c>，由呼叫端退回看的人的時區。</returns>
    public static DateTime? ToScheduleLocal(DateTime utc, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
