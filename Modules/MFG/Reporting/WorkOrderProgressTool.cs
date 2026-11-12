using SmartFactory.MES.Modules.MFG.Services;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.MFG.Reporting;

/// <summary>工單進度的請求。期間比對的是**計劃完工日**。</summary>
public class WorkOrderProgressRequest : ReportRequestBase
{
    /// <summary>品號關鍵字，**模糊比對**（與 ORD／PUR 報表的精確比對不同，維持現況）。</summary>
    public string? ProductKeyword { get; set; }

    /// <summary>工單狀態。解析不出來時**不篩**，而不是回空清單（現況，特徵測試鎖住）。</summary>
    public string? Status { get; set; }

    /// <summary>
    /// 計劃完工日的下界／上界，各自獨立。
    ///
    /// <para><b>刻意不用基底的 <c>AbsolutePeriod</c></b>：抽出前這兩個界線是**分開解析**的
    /// （起日解析失敗就不設下界、迄日解析失敗就不設上界），而 <see cref="DateRange"/>
    /// 表達不了「只有一邊」。硬塞進去會讓「迄日留白」從『不設上界』變成『上界＝今天』，
    /// 那是**未來的工單整批消失**的行為變更，而畫面上沒有任何訊號。</para>
    ///
    /// <para>排程與 LLM 路徑請改用基底的 <see cref="ReportRequestBase.Period"/>；
    /// 兩者同時給時 <c>Period</c>／<c>AbsolutePeriod</c> 優先。</para>
    /// </summary>
    public DateOnly? PlannedEndFrom { get; set; }

    /// <inheritdoc cref="PlannedEndFrom"/>
    public DateOnly? PlannedEndTo { get; set; }

    /// <summary>
    /// 「是否逾期」的比較基準時刻。<c>null</c> ＝ <c>DateTime.UtcNow</c>，與抽出前一致。
    ///
    /// <para>這裡是<b>時刻</b>而不是日期，也是刻意的：<c>PlannedEndDate</c> 帶時間部分，
    /// 拿當地午夜去比會讓「今天稍早就該完工」的工單不算逾期——那是行為變更。
    /// 這個欄位存在的目的是讓測試與排程能給定一個確定的時刻，不是改變語意。</para>
    /// </summary>
    public DateTime? AsOfInstant { get; set; }
}

/// <summary>
/// 工單進度（§六 M1）。由 <c>WorkOrderProgressController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>MfgReportCharacterizationTests</c> 守門（已做突變驗證）。
/// </summary>
public class WorkOrderProgressTool
    : IReportTool<WorkOrderProgressRequest, IReadOnlyList<WorkOrderProgressRow>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    public WorkOrderProgressTool(
        ApplicationDbContext db,
        IRelativePeriodResolver period,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _db      = db;
        _period  = period;
        _headers = headers;
    }

    public string ToolKey     => "WorkOrderProgress";
    public string Description =>
        "列出工單的計劃量、完工量（派工累計）與進度百分比，逾期工單排在最前面。"
        + "不含作廢工單；已結案的工單即使計劃完工日已過也不算逾期。期間比對的是計劃完工日。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。此畫面的閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終），由 <c>MfgReportCharacterizationTests</c> 的閘門那條鎖住。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：理由同其他工具，見 OrderOverdueTool。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`WorkOrderProgressRow` 只有數量與進度）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(WorkOrderProgressRequest);
    public Type ResponseType => typeof(IReadOnlyList<WorkOrderProgressRow>);

    public async Task<IReadOnlyList<WorkOrderProgressRow>> ExecuteAsync(
        WorkOrderProgressRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";
        var now     = request.AsOfInstant ?? DateTime.UtcNow;

        // 排程／LLM 走相對期間；畫面走兩個各自獨立的界線（理由見 PlannedEndFrom 說明）。
        DateOnly? from = request.PlannedEndFrom;
        DateOnly? to   = request.PlannedEndTo;
        if (request.Period.HasValue || request.AbsolutePeriod.HasValue)
        {
            var range = await _period.ResolveAsync(request, ct: ct);
            from = range.From == default ? null : range.From;
            to   = range.To;
        }

        var woQuery = _db.WorkOrders
            .Where(wo => wo.PlantId == plantId && wo.Status != WorkOrderStatus.Void);

        if (!string.IsNullOrWhiteSpace(request.ProductKeyword))
            woQuery = woQuery.Where(wo => wo.ProductCode.Contains(request.ProductKeyword));

        // 界線是廠別當地日期的 00:00 換成 UTC（PlannedEndDate 是 timestamptz），見 LocalDayStartUtc 說明。
        if (from is { } f)
        {
            var lower = _period.LocalDayStartUtc(f, request.TimeZoneId);
            woQuery = woQuery.Where(wo => wo.PlannedEndDate >= lower);
        }

        if (to is { } t)
        {
            // ⚠️ 迄日刻意 +1 天，這是現況且它有作用：計劃完工日帶時間部分的工單
            //（2026-05-31 08:00）在 <= to 之下會整批消失，而畫面上沒有任何訊號。
            var upper = _period.LocalDayStartUtc(t.AddDays(1), request.TimeZoneId);
            woQuery = woQuery.Where(wo => wo.PlannedEndDate <= upper);
        }

        if (!string.IsNullOrWhiteSpace(request.Status) &&
            Enum.TryParse<WorkOrderStatus>(request.Status, out var statusEnum))
            woQuery = woQuery.Where(wo => wo.Status == statusEnum);

        var workOrders = await woQuery.AsNoTracking().ToListAsync(ct);

        if (workOrders.Count == 0) return [];

        var productCodes = workOrders.Select(wo => wo.ProductCode).Distinct().ToList();
        var productUomMap = await _db.Products.AsNoTracking()
            .Where(p => productCodes.Contains(p.ProductCode))
            .Select(p => new { p.ProductCode, p.StockUom })
            .ToDictionaryAsync(p => p.ProductCode, p => p.StockUom, ct);

        // 各工單完工量＝最後一道工序的完工量。原本是全部派工加總：多工序工單每道都報工，
        // 3 道各 100 會顯示完工 300／進度 300%（2026-10-05 回報）。定義見 WorkOrderOutput。
        var ids = workOrders.Select(wo => wo.WorkOrderId).ToList();
        var progressMap = await WorkOrderOutput.CompletedQtyAsync(_db, ids, ct);

        return workOrders
            .Select(wo => new WorkOrderProgressRow
            {
                WorkOrderId      = wo.WorkOrderId,
                ProductCode      = wo.ProductCode,
                UomCode          = productUomMap.GetValueOrDefault(wo.ProductCode),
                Type             = wo.Type,
                Status           = wo.Status,
                PlannedStartDate = wo.PlannedStartDate,
                PlannedEndDate   = wo.PlannedEndDate,
                Quantity         = wo.Quantity,
                QtyDone          = progressMap.TryGetValue(wo.WorkOrderId, out var d) ? d : 0m,
                IsOverdue        = wo.PlannedEndDate < now
                                && wo.Status != WorkOrderStatus.Closed
                                && wo.Status != WorkOrderStatus.Void,
            })
            .OrderBy(r => r.IsOverdue ? 0 : 1)
            .ThenBy(r => r.PlannedEndDate)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄畫面現行的匯出輸出**。
    ///
    /// <para>「逾期」那一欄的<b>值</b>本身也是在地化的（中文印「是」、英文印「Y」，
    /// 未逾期留白）——所以本工具注入了標題資源。這是本批唯一有在地化<b>值</b>的報表；
    /// 把它寫成硬編的 <c>CurrentUICulture</c> 判斷也能動，但那樣這個字串就落在
    /// 任何資源守門之外，而它會出現在使用者收到的檔案裡。</para>
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<WorkOrderProgressRow>("WorkOrderProgress.WorkOrderId",      r => r.WorkOrderId),
        Column<WorkOrderProgressRow>("WorkOrderProgress.ProductCode",      r => r.ProductCode),
        Column<WorkOrderProgressRow>("WorkOrderProgress.Type",             r => r.Type.ToString()),
        Column<WorkOrderProgressRow>("WorkOrderProgress.Status",           r => r.Status.ToString()),
        Column<WorkOrderProgressRow>("WorkOrderProgress.PlannedStartDate", r => r.PlannedStartDate.ToString("yyyy-MM-dd")),
        Column<WorkOrderProgressRow>("WorkOrderProgress.PlannedEndDate",   r => r.PlannedEndDate.ToString("yyyy-MM-dd")),
        Column<WorkOrderProgressRow>("WorkOrderProgress.Quantity",         r => r.Quantity),
        Column<WorkOrderProgressRow>("WorkOrderProgress.QtyDone",          r => r.QtyDone),
        Column<WorkOrderProgressRow>("WorkOrderProgress.ProgressPct",      r => r.ProgressPct),
        Column<WorkOrderProgressRow>("WorkOrderProgress.IsOverdue",
            r => r.IsOverdue ? _headers["WorkOrderProgress.OverdueYes"].Value : ""),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。本工具的 <c>ExecuteAsync</c> 回的就是列集合，
    /// 所以這裡只是轉成契約要的 <c>IReadOnlyList&lt;object&gt;</c>；
    /// 分頁與「查不到資料 vs 拒絕執行」的規定見契約成員的說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<WorkOrderProgressRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
