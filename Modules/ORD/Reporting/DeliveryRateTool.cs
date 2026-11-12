using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Reporting;

/// <summary>訂單交期達成率的請求。期間比對的是**訂單預交日**（2026-09-21 起；原為出貨日）。</summary>
public class DeliveryRateRequest : ReportRequestBase
{
    public string? CustomerCode { get; set; }

    /// <summary>
    /// 判斷「已逾期未出」的基準日。<c>null</c>＝取廠別當地今日。
    /// 與 <c>OrderOverdueRequest.AsOfDate</c> 同一個語意，兩張表才對得起來。
    /// </summary>
    public DateOnly? AsOfDate { get; set; }
}

/// <summary>
/// 訂單交期達成率（§六 O4）。由 <c>DeliveryRateController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>OrdReportCharacterizationTests</c> 守門（已做突變驗證）。
/// </summary>
public class DeliveryRateTool
    : IReportTool<DeliveryRateRequest, IReadOnlyList<DeliveryRateRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;

    private readonly IDateTimeService _dateTime;

    public DeliveryRateTool(ApplicationDbContext db, IRelativePeriodResolver period, IDateTimeService dateTime)
    {
        _db       = db;
        _period   = period;
        _dateTime = dateTime;
    }

    public string ToolKey     => "DeliveryRate";
    public string Description =>
        "依客戶統計交期達成率。母體是預交日落在查詢期間的訂單明細，每行只計一次："
        + "已出清者比對出齊當日與預交日（當日算準時）；已過預交日但尚未出清者計為遲交；"
        + "尚未到期者不列入。依達成率降冪。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。此畫面的閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終），由 <c>OrdReportCharacterizationTests</c> 的閘門那條鎖住。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：理由同其他工具，見 OrderOverdueTool。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`DeliveryRateRowVm` 只有筆數與達成率）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(DeliveryRateRequest);
    public Type ResponseType => typeof(IReadOnlyList<DeliveryRateRowVm>);

    public async Task<IReadOnlyList<DeliveryRateRowVm>> ExecuteAsync(
        DeliveryRateRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";
        var asOf    = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        // 母體＝**預交日**落在期間的訂單明細，不是期間內的出貨。
        //
        // ⚠️ 2026-09-21 口徑變更。舊版從已過帳的出貨明細出發，於是「已經過了預交日、
        // 貨還沒出」的訂單完全不在分母裡——那正是這張報表最該回答的一種情況，
        // 而且方向固定：出得越晚越不會被算到。訂單逾期跟催表列得出來的單，這裡看不到。
        //
        // 順帶解掉舊版另一個問題：用出貨日當期間基準時，「預交日在期間內、實際在期間後
        // 才出」的單兩邊都落不到而靜默消失。預交日是唯一不重不漏的母體定義。
        var lineQuery =
            from sol in _db.SalesOrderLines
            join so in _db.SalesOrders on sol.SalesOrderId equals so.SalesOrderId
            where so.PlantId == plantId
               && so.ApprovalStatus != DocumentApprovalStatus.Draft   // 對齊訂單逾期跟催的母體
               && sol.DeliveryDate.HasValue
               && sol.DeliveryDate.Value >= range.From
               && sol.DeliveryDate.Value <= range.To
            select new
            {
                so.CompanyCode,
                sol.SalesOrderId,
                sol.LineNo,
                sol.OrderQty,
                sol.ShippedQty,
                DueDate = sol.DeliveryDate!.Value,
            };

        if (!string.IsNullOrWhiteSpace(request.CustomerCode))
            lineQuery = lineQuery.Where(r => r.CompanyCode == request.CustomerCode);

        var lines = await lineQuery.AsNoTracking().ToListAsync(ct);
        if (lines.Count == 0) return [];

        // 出齊日＝該行最後一次有效出貨的日期。分批出貨時要看**出齊**那一天：
        // 只看第一次出貨，會讓「首批準時、尾批拖很久」被判成準時。
        var soIds = lines.Select(l => l.SalesOrderId).Distinct().ToList();
        var lastShip = (await (
                from gsl in _db.GoodsShipmentLines
                join gs in _db.GoodsShipments on gsl.GoodsShipmentId equals gs.GoodsShipmentId
                where gs.Status == DocStatus.Posted
                   && gsl.SalesOrderId != null
                   && soIds.Contains(gsl.SalesOrderId)
                select new { gsl.SalesOrderId, gsl.SoLineNo, gs.ShipDate })
                .AsNoTracking().ToListAsync(ct))
            .Where(r => r.SoLineNo.HasValue)
            .GroupBy(r => (r.SalesOrderId!, r.SoLineNo!.Value))
            .ToDictionary(g => g.Key, g => g.Max(x => x.ShipDate));

        var byCustomer = new Dictionary<string, (int Total, int OnTime, int Late)>();
        foreach (var l in lines)
        {
            bool onTime, late;
            if (l.ShippedQty >= l.OrderQty)
            {
                // 出清了卻找不到出貨紀錄（人工改量、資料補寫）：拿不出出貨日期就無從判斷，
                // 列入會是憑空的「準時」，故跳過——與「未到期」同屬尚無結果。
                if (!lastShip.TryGetValue((l.SalesOrderId, l.LineNo), out var shipDate)) continue;
                onTime = shipDate <= l.DueDate;   // 當天算準時
                late   = !onTime;
            }
            else if (l.DueDate < asOf)
            {
                onTime = false;
                late   = true;                     // 已逾期未出清
            }
            else
            {
                continue;                          // 還沒到期＝尚無結果
            }

            byCustomer.TryGetValue(l.CompanyCode, out var acc);
            byCustomer[l.CompanyCode] =
                (acc.Total + 1, acc.OnTime + (onTime ? 1 : 0), acc.Late + (late ? 1 : 0));
        }

        return byCustomer
            .Select(kv => new DeliveryRateRowVm
            {
                CompanyCode = kv.Key,
                TotalLines  = kv.Value.Total,
                OnTimeLines = kv.Value.OnTime,
                LateLines   = kv.Value.Late,
            })
            .OrderByDescending(r => r.OnTimeRate)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<DeliveryRateRowVm>("DeliveryRate.CompanyCode", r => r.CompanyCode),
        Column<DeliveryRateRowVm>("DeliveryRate.TotalLines",  r => r.TotalLines),
        Column<DeliveryRateRowVm>("DeliveryRate.OnTimeLines", r => r.OnTimeLines),
        Column<DeliveryRateRowVm>("DeliveryRate.LateLines",   r => r.LateLines),
        Column<DeliveryRateRowVm>("DeliveryRate.OnTimeRate",  r => $"{r.OnTimeRate:F2}%"),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。本工具的 <c>ExecuteAsync</c> 回的就是列集合，
    /// 所以這裡只是轉成契約要的 <c>IReadOnlyList&lt;object&gt;</c>；
    /// 分頁與「查不到資料 vs 拒絕執行」的規定見契約成員的說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<DeliveryRateRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
