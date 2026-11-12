using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.MFG.Reporting;

/// <summary>不良率的請求。期間比對的是**報工時刻**，且區間是半開的（見工具說明）。</summary>
public class DefectRateRequest : ReportRequestBase
{
    /// <summary>品號關鍵字，**模糊比對**（維持現況）。</summary>
    public string? ProductKeyword { get; set; }

    /// <summary>
    /// 報工時刻的下界（含）。<c>null</c> ＝ 不設下界，與抽出前「起日解析失敗就不篩」一致。
    /// 與 <see cref="ReportedBefore"/> 各自獨立，理由同 <c>WorkOrderProgressRequest</c>。
    /// </summary>
    public DateOnly? ReportedFrom { get; set; }

    /// <summary>
    /// 報工時刻的上界日期。實際條件是 <c>&lt; 本日期 + 1 天</c>（＝該日整天算在內，
    /// 隔天 00:00 不算）。<c>null</c> ＝ 退回「今天 + 1 天」，與抽出前一致。
    /// </summary>
    public DateOnly? ReportedBefore { get; set; }
}

/// <summary>
/// 不良率（§六 M2）。由 <c>DefectRateController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>MfgReportCharacterizationTests</c> 守門（已做突變驗證）。
///
/// <para>⚠️ <b>時間區間是半開的</b>（<c>&gt;= start &amp;&amp; &lt; end</c>），與 ORD／PUR
/// 報表的全閉區間相反。這是本報表最容易在重構時被「順手統一」掉的一行，
/// 統一之後迄日隔天 00:00 的報工會被多算進來。</para>
/// </summary>
public class DefectRateTool
    : IReportTool<DefectRateRequest, IReadOnlyList<DefectRateRow>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;

    public DefectRateTool(ApplicationDbContext db, IRelativePeriodResolver period)
    {
        _db     = db;
        _period = period;
    }

    public string ToolKey     => "DefectRate";
    public string Description =>
        "依品號彙總報工的合格量與不良量，算出不良率與報工筆數，依不良率降冪。"
        + "期間比對的是報工時刻，迄日當天整天算在內。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。此畫面的閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終），由 <c>MfgReportCharacterizationTests</c> 的閘門那條鎖住。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：理由同其他工具，見 OrderOverdueTool。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`DefectRateRow` 只有數量與比率）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(DefectRateRequest);
    public Type ResponseType => typeof(IReadOnlyList<DefectRateRow>);

    public async Task<IReadOnlyList<DefectRateRow>> ExecuteAsync(
        DefectRateRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";

        // 排程／LLM 走相對期間；畫面走兩個各自獨立的界線。
        DateOnly? from   = request.ReportedFrom;
        DateOnly? before = request.ReportedBefore;
        if (request.Period.HasValue || request.AbsolutePeriod.HasValue)
        {
            var range = await _period.ResolveAsync(request, ct: ct);
            from   = range.From == default ? null : range.From;
            before = range.To;
        }

        // 界線是廠別當地日期的 00:00 換成 UTC（ReportedAt 是 timestamptz），見 LocalDayStartUtc 說明。
        var startDt = from is { } a
            ? _period.LocalDayStartUtc(a, request.TimeZoneId)
            : DateTime.SpecifyKind(default, DateTimeKind.Utc);
        var endDt   = before is { } b
            ? _period.LocalDayStartUtc(b.AddDays(1), request.TimeZoneId)
            : DateTime.UtcNow.AddDays(1);

        var query =
            from pr in _db.ProductionReports
            join wo in _db.WorkOrders on pr.WorkOrderId equals wo.WorkOrderId
            where wo.PlantId == plantId
               && pr.ReportedAt >= startDt
               && pr.ReportedAt <  endDt        // ⚠️ 半開，見類別說明
            select new { wo.ProductCode, pr.QtyOk, pr.QtyNg };

        if (!string.IsNullOrWhiteSpace(request.ProductKeyword))
            query = query.Where(r => r.ProductCode.Contains(request.ProductKeyword));

        var raw = await query.AsNoTracking().ToListAsync(ct);
        var productUomMap = await _db.Products.AsNoTracking()
            .Select(p => new { p.ProductCode, p.StockUom })
            .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom, ct);

        return raw
            .GroupBy(r => r.ProductCode)
            .Select(g => new DefectRateRow
            {
                ProductCode = g.Key,
                UomCode     = productUomMap.GetValueOrDefault(g.Key),
                QtyOk       = g.Sum(x => x.QtyOk),
                QtyNg       = g.Sum(x => x.QtyNg),
                ReportCount = g.Count(),
            })
            .OrderByDescending(r => r.DefectRate)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<DefectRateRow>("DefectRate.ProductCode", r => r.ProductCode),
        Column<DefectRateRow>("DefectRate.QtyOk",       r => r.QtyOk),
        Column<DefectRateRow>("DefectRate.QtyNg",       r => r.QtyNg),
        Column<DefectRateRow>("DefectRate.TotalQty",    r => r.TotalQty),
        Column<DefectRateRow>("DefectRate.DefectRate",  r => r.DefectRate),
        Column<DefectRateRow>("DefectRate.ReportCount", r => r.ReportCount),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。本工具的 <c>ExecuteAsync</c> 回的就是列集合，
    /// 所以這裡只是轉成契約要的 <c>IReadOnlyList&lt;object&gt;</c>；
    /// 分頁與「查不到資料 vs 拒絕執行」的規定見契約成員的說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<DefectRateRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
