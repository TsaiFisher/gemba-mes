using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.RPS.Services;

/// <summary>
/// 常用查詢存下來的參數（§7.9 L2）。
///
/// **只存參數，不存結果**（§7.9-2）：每次點開都重新查、重新走權限檢查。
/// 快取結果會讓使用者調職後點開舊查詢還看得到不該看的數字。
/// </summary>
public sealed class SavedQueryParams
{
    /// <summary>相對期間。**不存絕對日期**（§7.9-1）。</summary>
    public RelativePeriod? Period { get; set; }

    /// <summary>畫面查詢列的其餘欄位，鍵須在該工具的 <see cref="ReportScreenBinding.FilterKeys"/> 白名單內。</summary>
    public Dictionary<string, string> Filters { get; set; } = new(StringComparer.Ordinal);
}

public readonly record struct SavedQueryInput(
    string ToolKey,
    string Name,
    RelativePeriod? Period,
    IReadOnlyDictionary<string, string?> Filters);

/// <summary>清單一列。<c>IsAuthorized</c>／<c>IsValid</c> 為 false 時仍會出現在清單裡，只是禁用。</summary>
public readonly record struct SavedQueryItem(
    int Id,
    string Name,
    string ToolKey,
    RelativePeriod? Period,
    bool IsAuthorized,
    bool IsValid,
    string? InvalidReason);

public readonly record struct SavedQuerySaveResult(bool Success, int Id, string? ErrorMessage);

public readonly record struct SavedQueryRunResult(bool Success, string? Url, string? ErrorMessage);

public interface ISavedQueryService
{
    /// <summary>目前使用者自己的常用查詢。<paramref name="toolKey"/> 給值時只列該報表的。</summary>
    Task<IReadOnlyList<SavedQueryItem>> ListAsync(string? toolKey = null, CancellationToken ct = default);

    /// <summary>新增或以同名覆寫（唯一鍵 <c>(OwnerUserId, Name)</c>，§7.9-5）。</summary>
    Task<SavedQuerySaveResult> SaveAsync(SavedQueryInput input, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>解析為目標報表畫面的網址；權限與定義有效性都在這裡重新檢查。</summary>
    Task<SavedQueryRunResult> BuildRunUrlAsync(int id, CancellationToken ct = default);
}

/// <inheritdoc />
public class SavedQueryService : ISavedQueryService
{
    /// <summary>
    /// 固定選項，**不啟用任何多型／型別名稱處理**（§7.0.1-c）：
    /// 依使用者資料決定要載入什麼型別是典型的反序列化破口。
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // 期間存**列舉名稱**而非數字。這不是可讀性偏好：RelativePeriod 現有 17 個成員，
        // 日後在中間插一個新成員會讓所有以序數存下來的舊查詢默默指向另一個期間
        // （「上個月」變成「本季」，畫面照常顯示，只是數字全錯）。名稱不會漂移。
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxNameLength = 100;

    private readonly ApplicationDbContext    _db;
    private readonly IUserContext            _userContext;
    private readonly IReportToolCatalog      _catalog;
    private readonly IRelativePeriodResolver _periods;

    public SavedQueryService(
        ApplicationDbContext db,
        IUserContext userContext,
        IReportToolCatalog catalog,
        IRelativePeriodResolver periods)
    {
        _db          = db;
        _userContext = userContext;
        _catalog     = catalog;
        _periods     = periods;
    }

    public async Task<IReadOnlyList<SavedQueryItem>> ListAsync(
        string? toolKey = null, CancellationToken ct = default)
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrEmpty(userId)) return [];

        // 擁有者條件寫在查詢裡，不是撈出來再篩：查詢層過濾漏掉會直接變成越權讀取。
        var q = _db.ReportDefinitions.Where(d => d.OwnerUserId == userId);
        if (!string.IsNullOrEmpty(toolKey))
            q = q.Where(d => d.ToolKey == toolKey);

        var rows = await q.OrderBy(d => d.ToolKey).ThenBy(d => d.Name)
                          .AsNoTracking().ToListAsync(ct);

        var items = new List<SavedQueryItem>(rows.Count);
        foreach (var row in rows)
        {
            var authorized = await _catalog.IsAuthorizedAsync(row.ToolKey);
            var (parms, invalidReason) = TryParse(row);

            items.Add(new SavedQueryItem(
                row.Id, row.Name, row.ToolKey, parms?.Period,
                authorized, invalidReason is null, invalidReason));
        }
        return items;
    }

    public async Task<SavedQuerySaveResult> SaveAsync(
        SavedQueryInput input, CancellationToken ct = default)
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrEmpty(userId))
            return new(false, 0, "SavedQuery_NoUser");

        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)         return new(false, 0, "SavedQuery_NameRequired");
        if (name.Length > MaxNameLength) return new(false, 0, "SavedQuery_NameTooLong");

        if (ReportScreenBindings.Find(input.ToolKey) is not { } binding)
            return new(false, 0, "SavedQuery_UnknownTool");

        // 存不下自己執行不了的查詢——否則清單上會出現一整排永遠點不開的項目。
        if (!await _catalog.IsAuthorizedAsync(input.ToolKey))
            return new(false, 0, "SavedQuery_Forbidden");

        // §7.9-1：有日期區間的畫面一律存相對期間，存絕對日期的常用查詢下個月就過期了。
        if (binding.UsesDateRange && input.Period is null)
            return new(false, 0, "SavedQuery_PeriodRequired");

        var parms = new SavedQueryParams { Period = input.Period };
        foreach (var key in binding.FilterKeys)
        {
            if (input.Filters is null) break;
            if (input.Filters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                parms.Filters[key] = value.Trim();
        }

        var existing = await _db.ReportDefinitions
            .FirstOrDefaultAsync(d => d.OwnerUserId == userId && d.Name == name, ct);

        if (existing is null)
        {
            existing = new ReportDefinition
            {
                Name            = name,
                OwnerUserId     = userId,
                OwnerRoleId     = _userContext.ActiveRoleId ?? "",
                CreatedByRoleId = _userContext.ActiveRoleId ?? "",
                PlantId         = _userContext.PlantId,
                CreatedAt       = DateTime.UtcNow,
                OutputFormat    = "Screen",
            };
            _db.ReportDefinitions.Add(existing);
        }
        else
        {
            existing.UpdatedAt = DateTime.UtcNow;
        }

        // 同名覆寫時工具也可能換了（使用者在別張報表用了同一個名字）。
        existing.ToolKey    = input.ToolKey;
        existing.ParamsJson = JsonSerializer.Serialize(parms, Json);
        existing.IsActive   = true;

        // ScheduleKind 維持 null＝純常用查詢（§7.0）。此階段不寫任何排程欄位。

        await _db.SaveChangesAsync(ct);
        return new(true, existing.Id, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrEmpty(userId)) return false;

        var row = await _db.ReportDefinitions
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == userId, ct);
        if (row is null) return false;

        _db.ReportDefinitions.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SavedQueryRunResult> BuildRunUrlAsync(int id, CancellationToken ct = default)
    {
        var userId = _userContext.UserId;
        if (string.IsNullOrEmpty(userId))
            return new(false, null, "SavedQuery_NoUser");

        var row = await _db.ReportDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == userId, ct);
        if (row is null) return new(false, null, "SavedQuery_NotFound");

        // ⚠️ 互動觸發一律以**當下使用者**解析權限，絕不沿用 OwnerRoleId（§7.9 L3 定案）。
        // OwnerRoleId 是給日後排程用的（排程沒有檢視者，只有 owner）；兩條路徑混用
        // 就是權限提升管道。此處刻意不讀 row.OwnerRoleId。
        if (!await _catalog.IsAuthorizedAsync(row.ToolKey))
            return new(false, null, "SavedQuery_Forbidden");

        if (ReportScreenBindings.Find(row.ToolKey) is not { } binding)
            return new(false, null, "SavedQuery_UnknownTool");

        var (parms, invalidReason) = TryParse(row);
        if (parms is null) return new(false, null, invalidReason);

        var sb = new StringBuilder($"/{binding.Controller}/Index?searched=true");

        if (binding.UsesDateRange)
        {
            if (parms.Period is null) return new(false, null, "SavedQuery_Invalid");

            DateRange range;
            try
            {
                range = await _periods.ResolveAsync(
                    parms.Period.Value, _userContext.TimeZoneId, ct);
            }
            catch (InvalidOperationException)
            {
                // ThisFiscalPeriod／LastFiscalPeriod 在沒有建會計期間的廠會擲例外
                // （解算端刻意不退回日曆月）。這裡把它轉成「這張常用查詢現在跑不了」，
                // 而不是讓點一個連結變成 500。
                return new(false, null, "SavedQuery_NoFiscalPeriod");
            }

            var appended = await AppendPeriodAsync(sb, binding.PeriodStyle, range, ct);
            if (!appended) return new(false, null, "SavedQuery_NoFiscalPeriod");
        }

        // 再過一次白名單：ParamsJson 是資料庫內容，可能早於目前的繫結定義。
        foreach (var key in binding.FilterKeys)
        {
            if (!parms.Filters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            sb.Append('&').Append(Uri.EscapeDataString(key))
              .Append('=').Append(Uri.EscapeDataString(value));
        }

        return new(true, sb.ToString(), null);
    }


    /// <summary>
    /// 把解算出來的區間串成該畫面看得懂的期間參數。
    ///
    /// <para><b>會計期間是查出來的，不是把日期格式化成 yyyyMM</b>：期間可以自訂起訖
    /// （<c>AccountingPeriod.StartDate/EndDate</c>），也可以不存在。用
    /// <c>range.To.ToString("yyyyMM")</c> 會在自訂期間的廠別指到一個不存在或錯誤的期別，
    /// 而畫面對查無期間是靜默顯示空報表——不會有任何訊號。</para>
    ///
    /// <para>回傳 <c>false</c>＝那一天不落在任何會計期間內，呼叫端據此回報失敗。</para>
    /// </summary>
    private async Task<bool> AppendPeriodAsync(
        StringBuilder sb, ReportPeriodStyle style, DateRange range, CancellationToken ct)
    {
        switch (style)
        {
            case ReportPeriodStyle.DateRange:
                sb.Append("&fromDate=").Append(range.From.ToString("yyyy-MM-dd"))
                  .Append("&toDate=").Append(range.To.ToString("yyyy-MM-dd"));
                return true;

            case ReportPeriodStyle.FiscalAsOf:
            {
                var to = await PeriodIdAtAsync(range.To, ct);
                if (to is null) return false;
                sb.Append("&asOfPeriod=").Append(Uri.EscapeDataString(to));
                return true;
            }

            case ReportPeriodStyle.FiscalRange:
            {
                var from = await PeriodIdAtAsync(range.From, ct);
                var to   = await PeriodIdAtAsync(range.To, ct);
                if (from is null || to is null) return false;
                sb.Append("&fromPeriod=").Append(Uri.EscapeDataString(from))
                  .Append("&toPeriod=").Append(Uri.EscapeDataString(to));
                return true;
            }

            case ReportPeriodStyle.Year:
                // 取解算區間**迄日**的年份。ThisYear／LastYear 解出來的區間本來就落在同一年，
                // 所以這個取法對年度類相對期間是精確的；非年度類選項由畫面那一側擋掉。
                sb.Append("&year=").Append(range.To.Year);
                return true;

            default:
                return true;   // None：畫面不吃期間，不串任何東西
        }
    }

    /// <summary>某一天落在哪個會計期間。查無回 null。</summary>
    private async Task<string?> PeriodIdAtAsync(DateOnly day, CancellationToken ct)
        // 不繞過廠別過濾：期間依廠別分開，報表要的是本廠的會計行事曆
        // （與 RelativePeriodResolver.FiscalAsync 同一個理由）。
        => await _db.AccountingPeriods.AsNoTracking()
            .Where(p => p.StartDate <= day && day <= p.EndDate)
            .OrderBy(p => p.StartDate)
            .Select(p => p.PeriodId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// 反序列化 <c>ParamsJson</c>。失敗**不得靜默跳過**（§7.0.1-c）——工具的參數演進會讓
    /// 舊資料解不開，靜默忽略等於給使用者一個看起來正常、內容卻是預設值的查詢。
    /// </summary>
    private static (SavedQueryParams? Parms, string? InvalidReason) TryParse(ReportDefinition row)
    {
        if (string.IsNullOrWhiteSpace(row.ParamsJson))
            return (new SavedQueryParams(), null);

        try
        {
            var parms = JsonSerializer.Deserialize<SavedQueryParams>(row.ParamsJson, Json);
            return parms is null
                ? (null, "SavedQuery_Invalid")
                : (parms, null);
        }
        catch (JsonException)
        {
            return (null, "SavedQuery_Invalid");
        }
    }
}
