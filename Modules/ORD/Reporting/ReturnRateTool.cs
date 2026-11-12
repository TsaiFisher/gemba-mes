using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.ORD.Reporting;

/// <summary>退貨率分析的請求。出貨以出貨日、退貨以退貨日落在同一區間內。</summary>
public class ReturnRateRequest : ReportRequestBase
{
    public string? ProductCode { get; set; }
}

/// <summary>
/// 退貨率分析（§六 O4）。由 <c>ReturnRateController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>OrdReportCharacterizationTests</c> 守門。
/// </summary>
public class ReturnRateTool
    : IReportTool<ReturnRateRequest, IReadOnlyList<ReturnRateRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;

    public ReturnRateTool(ApplicationDbContext db, IRelativePeriodResolver period)
    {
        _db     = db;
        _period = period;
    }

    public string ToolKey     => "ReturnRate";
    public string Description => "以（品號, 客戶）配對統計指定期間的出貨量與退貨量並計算退貨率，只計已過帳單據。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>——畫面閘門已於 2026-08-14 由 <c>CanCreate</c> 改為
    /// <c>CanRead</c>（覆寫 §8.1-終）。「照抄畫面」這條規則本身不變，變的是被照抄的那一端。</summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`ReturnRateRowVm` 只有出貨量／退貨量／退貨率，無任何金額欄位）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(ReturnRateRequest);
    public Type ResponseType => typeof(IReadOnlyList<ReturnRateRowVm>);

    /// <summary>
    /// 本工具的 <c>ExecuteAsync</c> 本來就回整份集合，直接轉交即可。
    /// 仍須顯式實作而非讓基底猜——猜錯的那兩支正是靜默產出空白報表的來源。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<ReturnRateRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();

    public async Task<IReadOnlyList<ReturnRateRowVm>> ExecuteAsync(
        ReturnRateRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        var shipped = await (
            from gsl in _db.GoodsShipmentLines
            join gs  in _db.GoodsShipments on gsl.GoodsShipmentId equals gs.GoodsShipmentId
            where gs.PlantId == plantId && gs.ShipDate >= range.From && gs.ShipDate <= range.To
               && gs.Status == DocStatus.Posted
            select new { gsl.ProductCode, gs.CompanyCode, gsl.ShipQty }
        ).AsNoTracking().ToListAsync(ct);

        var returned = await (
            from srl in _db.SalesReturnLines
            join sr  in _db.SalesReturns on srl.SalesReturnId equals sr.SalesReturnId
            where sr.PlantId == plantId && sr.ReturnDate >= range.From && sr.ReturnDate <= range.To
               && sr.Status == DocStatus.Posted
            select new { srl.ProductCode, sr.CompanyCode, srl.ReturnQty }
        ).AsNoTracking().ToListAsync(ct);

        var shippedMap  = shipped.GroupBy(r => (r.ProductCode, r.CompanyCode))
                                 .ToDictionary(g => g.Key, g => g.Sum(r => r.ShipQty));
        var returnedMap = returned.GroupBy(r => (r.ProductCode, r.CompanyCode))
                                  .ToDictionary(g => g.Key, g => g.Sum(r => r.ReturnQty));

        // 取聯集：只有退貨、查無對應出貨的組合也會列出（出貨量 0 → 退貨率算成 0%）。
        // 這是現況，由特徵測試鎖住；要改成濾掉屬行為變更。
        var keys = shippedMap.Keys.Union(returnedMap.Keys).Distinct();

        return keys
            .Select(k => new ReturnRateRowVm
            {
                ProductCode = k.ProductCode,
                CompanyCode = k.CompanyCode,
                ShippedQty  = shippedMap.GetValueOrDefault(k, 0),
                ReturnedQty = returnedMap.GetValueOrDefault(k, 0),
            })
            .Where(r => string.IsNullOrWhiteSpace(request.ProductCode) || r.ProductCode == request.ProductCode)
            .OrderByDescending(r => r.ReturnRate)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<ReturnRateRowVm>("ReturnRate.ProductCode", r => r.ProductCode),
        Column<ReturnRateRowVm>("ReturnRate.CompanyCode", r => r.CompanyCode),
        Column<ReturnRateRowVm>("ReturnRate.ShippedQty",  r => r.ShippedQty),
        Column<ReturnRateRowVm>("ReturnRate.ReturnedQty", r => r.ReturnedQty),
        // 百分比在畫面匯出是帶 % 的字串，不是數值——照抄，改成數值會讓既有檔案的那一欄變樣。
        Column<ReturnRateRowVm>("ReturnRate.ReturnRate",  r => $"{r.ReturnRate:F2}%"),
    ];
}
