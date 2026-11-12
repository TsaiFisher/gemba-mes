using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Reporting;

/// <summary>訂單逾期跟催的請求。期間比對的是**預交日**，不是訂單日。</summary>
public class OrderOverdueRequest : ReportRequestBase
{
    public string? CustomerCode { get; set; }
    public string? ProductCode  { get; set; }
}

/// <summary>
/// 訂單逾期跟催（§六 O3）。由 <c>OrderOverdueController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>OrdReportCharacterizationTests</c> 守門。
/// </summary>
public class OrderOverdueTool
    : IReportTool<OrderOverdueRequest, IReadOnlyList<OrderOverdueRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IDateTimeService        _dateTime;

    public OrderOverdueTool(
        ApplicationDbContext db, IRelativePeriodResolver period, IDateTimeService dateTime)
    {
        _db       = db;
        _period   = period;
        _dateTime = dateTime;
    }

    public string ToolKey     => "OrderOverdue";
    public string Description => "列出預交日已過但仍有未出量的銷售訂單明細，含逾期天數，依逾期天數降冪。不含草稿單。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。畫面閘門已於 2026-08-14 由 <c>CanCreate</c> 改為 <c>CanRead</c>
    /// （覆寫 §8.1-終）；「照抄畫面、不套統一慣例」這條規則本身不變，變的是被照抄的那一端。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`OrderOverdueRowVm` 只有數量與逾期天數，無任何金額欄位）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(OrderOverdueRequest);
    public Type ResponseType => typeof(IReadOnlyList<OrderOverdueRowVm>);

    /// <summary>
    /// 本工具的 <c>ExecuteAsync</c> 本來就回整份集合，直接轉交即可。
    /// 仍須顯式實作而非讓基底猜——猜錯的那兩支正是靜默產出空白報表的來源。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<OrderOverdueRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();

    public async Task<IReadOnlyList<OrderOverdueRowVm>> ExecuteAsync(
        OrderOverdueRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        // 逾期天數的基準日。抽出前這裡寫死 DateTime.Today（伺服器時區，跨時區部署會差一天）。
        // 那個歷史語意留在 Controller 邊界顯式傳入；工具自己的預設是時區感知的正確版本，
        // 供排程與未來的 LLM 使用。這樣既不改變畫面行為，新路徑也不繼承舊缺陷。
        var today = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        var query =
            from sol in _db.SalesOrderLines
            join so  in _db.SalesOrders on sol.SalesOrderId equals so.SalesOrderId
            where so.PlantId == plantId
               && sol.DeliveryDate.HasValue
               && sol.DeliveryDate.Value >= range.From
               && sol.DeliveryDate.Value <= range.To
               && sol.ShippedQty < sol.OrderQty
               && so.ApprovalStatus != DocumentApprovalStatus.Draft
            select new
            {
                so.SalesOrderId,
                so.CompanyCode,
                so.OrderDate,
                sol.ProductCode,
                sol.LineNo,
                DeliveryDate = sol.DeliveryDate!.Value,
                sol.OrderQty,
                sol.ShippedQty,
            };

        if (!string.IsNullOrWhiteSpace(request.CustomerCode))
            query = query.Where(r => r.CompanyCode == request.CustomerCode);
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            query = query.Where(r => r.ProductCode == request.ProductCode);

        var raw = await query.AsNoTracking().ToListAsync(ct);

        return raw
            .Select(r => new OrderOverdueRowVm
            {
                SalesOrderId = r.SalesOrderId,
                CompanyCode  = r.CompanyCode,
                OrderDate    = r.OrderDate,
                ProductCode  = r.ProductCode,
                LineNo       = r.LineNo,
                DeliveryDate = r.DeliveryDate,
                OrderQty     = r.OrderQty,
                ShippedQty   = r.ShippedQty,
                OverdueDays  = today.DayNumber - r.DeliveryDate.DayNumber,
            })
            .Where(r => r.OverdueDays > 0)
            .OrderByDescending(r => r.OverdueDays)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<OrderOverdueRowVm>("OrderOverdue.SalesOrderId", r => r.SalesOrderId),
        Column<OrderOverdueRowVm>("OrderOverdue.CompanyCode",  r => r.CompanyCode),
        Column<OrderOverdueRowVm>("OrderOverdue.OrderDate",    r => r.OrderDate.ToString("yyyy-MM-dd")),
        Column<OrderOverdueRowVm>("OrderOverdue.ProductCode",  r => r.ProductCode),
        Column<OrderOverdueRowVm>("OrderOverdue.LineNo",       r => r.LineNo),
        Column<OrderOverdueRowVm>("OrderOverdue.DeliveryDate", r => r.DeliveryDate.ToString("yyyy-MM-dd")),
        Column<OrderOverdueRowVm>("OrderOverdue.OrderQty",     r => r.OrderQty),
        Column<OrderOverdueRowVm>("OrderOverdue.ShippedQty",   r => r.ShippedQty),
        Column<OrderOverdueRowVm>("OrderOverdue.PendingQty",   r => r.PendingQty),
        Column<OrderOverdueRowVm>("OrderOverdue.OverdueDays",  r => r.OverdueDays),
    ];
}
