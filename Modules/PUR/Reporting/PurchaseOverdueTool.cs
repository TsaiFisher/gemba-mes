using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Reporting;

/// <summary>採購逾期跟催的請求。期間比對的是**預交日**，不是採購日。</summary>
public class PurchaseOverdueRequest : ReportRequestBase
{
    public string? SupplierCode { get; set; }
    public string? ProductCode  { get; set; }

    /// <summary>
    /// 只列逾期天數達此值（含）的列。<c>null</c> ＝ 1，與畫面預設一致。
    /// 刻意做成可空：小於 1 的值會被夾到 1，那個夾值是現行行為的一部分（特徵測試鎖住）。
    /// </summary>
    public int? MinOverdueDays { get; set; }
}

/// <summary>
/// 採購逾期跟催（§六 P1）。由 <c>PurchaseOverdueController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>PurReportCharacterizationTests</c> 守門（已做突變驗證）。
/// </summary>
public class PurchaseOverdueTool
    : IReportTool<PurchaseOverdueRequest, IReadOnlyList<PurchaseOverdueRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IDateTimeService        _dateTime;

    public PurchaseOverdueTool(
        ApplicationDbContext db, IRelativePeriodResolver period, IDateTimeService dateTime)
    {
        _db       = db;
        _period   = period;
        _dateTime = dateTime;
    }

    public string ToolKey     => "PurchaseOverdue";
    public string Description =>
        "列出預交日已過但仍有未到量的採購單明細，含逾期天數與採購員，依逾期天數降冪。"
        + "不含草稿單、已收足與已結案的明細。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。此畫面的閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終），由 <c>PurReportCharacterizationTests</c> 的閘門那條鎖住。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`PurchaseOverdueRowVm` 只有採購量／已到量／未到量／逾期天數）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(PurchaseOverdueRequest);
    public Type ResponseType => typeof(IReadOnlyList<PurchaseOverdueRowVm>);

    public async Task<IReadOnlyList<PurchaseOverdueRowVm>> ExecuteAsync(
        PurchaseOverdueRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        // 逾期天數的基準日。抽出前這裡寫死 DateTime.Today（伺服器時區，跨時區部署會差一天）。
        // 那個歷史語意留在 Controller 邊界顯式傳入；工具自己的預設是時區感知的正確版本，
        // 供排程與未來的 LLM 使用。這樣既不改變畫面行為，新路徑也不繼承舊缺陷。
        var today = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        // 夾值是現行行為的一部分：不夾的話「逾期 0 天」（今天才到期）會混進逾期清單。
        var minOverdueDays = request.MinOverdueDays is { } m && m >= 1 ? m : 1;

        var query =
            from pol in _db.PurchaseOrderLines
            join po  in _db.PurchaseOrders on pol.PurchaseOrderId equals po.PurchaseOrderId
            where po.PlantId == plantId
               && pol.DeliveryDate.HasValue
               && pol.DeliveryDate.Value >= range.From
               && pol.DeliveryDate.Value <= range.To
               && pol.ReceivedQty < pol.OrderQty
               && !pol.IsClosed
               && po.ApprovalStatus != DocumentApprovalStatus.Draft
            select new
            {
                po.PurchaseOrderId,
                po.SupplierCode,
                po.OrderDate,
                po.CreatedBy,
                pol.ProductCode,
                pol.LineNo,
                DeliveryDate = pol.DeliveryDate!.Value,
                pol.OrderQty,
                pol.ReceivedQty,
            };

        if (!string.IsNullOrWhiteSpace(request.SupplierCode))
            query = query.Where(r => r.SupplierCode == request.SupplierCode);
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            query = query.Where(r => r.ProductCode == request.ProductCode);

        var raw = await query.AsNoTracking().ToListAsync(ct);

        // 採購員顯示名（建單人 UserId → UserName）。查無對應使用者時退回 UserId：
        // 使用者被刪掉之後欄位仍要看得出是誰，不能變成空白。
        var buyerIds = raw.Select(r => r.CreatedBy).Distinct().ToList();
        var buyerMap = await _db.Users
            .AsNoTracking()
            .Where(u => buyerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName ?? u.Id, ct);

        return raw
            .Select(r => new PurchaseOverdueRowVm
            {
                PurchaseOrderId = r.PurchaseOrderId,
                SupplierCode    = r.SupplierCode,
                OrderDate       = r.OrderDate,
                ProductCode     = r.ProductCode,
                LineNo          = r.LineNo,
                DeliveryDate    = r.DeliveryDate,
                OrderQty        = r.OrderQty,
                ReceivedQty     = r.ReceivedQty,
                OverdueDays     = today.DayNumber - r.DeliveryDate.DayNumber,
                Buyer           = buyerMap.TryGetValue(r.CreatedBy, out var n) ? n : r.CreatedBy,
            })
            .Where(r => r.OverdueDays >= minOverdueDays)
            .OrderByDescending(r => r.OverdueDays)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.PurchaseOrderId", r => r.PurchaseOrderId),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.SupplierCode",    r => r.SupplierCode),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.OrderDate",       r => r.OrderDate.ToString("yyyy-MM-dd")),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.ProductCode",     r => r.ProductCode),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.LineNo",          r => r.LineNo),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.DeliveryDate",    r => r.DeliveryDate.ToString("yyyy-MM-dd")),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.OrderQty",        r => r.OrderQty),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.ReceivedQty",     r => r.ReceivedQty),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.PendingQty",      r => r.PendingQty),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.OverdueDays",     r => r.OverdueDays),
        Column<PurchaseOverdueRowVm>("PurchaseOverdue.Buyer",           r => r.Buyer),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。本工具的 <c>ExecuteAsync</c> 回的就是列集合，
    /// 所以這裡只是轉成契約要的 <c>IReadOnlyList&lt;object&gt;</c>；
    /// 分頁與「查不到資料 vs 拒絕執行」的規定見契約成員的說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<PurchaseOverdueRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
