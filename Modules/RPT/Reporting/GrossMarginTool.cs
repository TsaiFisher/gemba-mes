using System.Security.Claims;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>
/// 毛利分析的請求。期間由基底的 <c>Period</c>／<c>AbsolutePeriod</c> 表達，本工具不另立欄位。
/// </summary>
public class GrossMarginRequest : ReportRequestBase { }

/// <summary>
/// 毛利分析（RPT 批次 2）。與前十一支不同的是**查詢邏輯早就在 service 層**
/// （<see cref="IGrossMarginService"/>），所以這支工具不搬查詢，只做三件事：
/// 把相對期間解算成絕對區間、把 service 的回應接上排程契約、宣告匯出欄位。
///
/// <para>⚠️ <b>回傳的是包裝型別</b>（<see cref="GrossMarginReport"/> 帶 Rows 與四個合計），
/// 不是列集合。這正是 <c>GetExportRowsAsync</c> 存在的理由——2026-09-14 Wave R 實測時，
/// 由 runner 猜「回應是不是 IEnumerable」的版本會讓這種形狀**靜默產出空白報表**。
/// 本工具顯式回 <c>report.Rows</c>。</para>
///
/// <para>⚠️ <b>合計不進匯出</b>：畫面匯出現行就只輸出 Rows（四個 Total 只顯示在頁面上），
/// 照抄現況不增欄。要加合計列是另一個決定，不該混在「接進排程」這一步裡順手做。</para>
/// </summary>
public class GrossMarginTool
    : IReportTool<GrossMarginRequest, GrossMarginReport>, IReportToolDescriptor
{
    private readonly IGrossMarginService   _service;
    private readonly IRelativePeriodResolver _period;

    public GrossMarginTool(IGrossMarginService service, IRelativePeriodResolver period)
    {
        _service = service;
        _period  = period;
    }

    public string ToolKey     => "GrossMargin";
    public string Description => "依品號列出期間內的出貨金額、成本、毛利與毛利率，含出貨次數。金額為本幣。";

    /// <summary>照抄畫面現況：<c>Index</c> 的閘門是 <c>CanRead</c>（機密分級改由 MenuPermission 資料承載）。</summary>
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（出貨金額／成本／毛利）→ §八「機密」級，第一版不進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(GrossMarginRequest);
    public Type ResponseType => typeof(GrossMarginReport);

    public async Task<GrossMarginReport> ExecuteAsync(
        GrossMarginRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range = await _period.ResolveAsync(request, ct: ct);
        return await _service.GetReportAsync(range.From, range.To, request.PlantId ?? "", ct);
    }

    /// <summary>
    /// 包裝型別 → 列集合。**必須顯式取 <c>Rows</c>**，見類別說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<GrossMarginRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();

    /// <summary>匯出欄位：**逐欄照抄畫面現行輸出**，不增不刪不改順序。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<GrossMarginRow>("GrossMargin.ProductCode",     r => r.ProductCode),
        Column<GrossMarginRow>("GrossMargin.Revenue",         r => r.Revenue),
        Column<GrossMarginRow>("GrossMargin.Cost",            r => r.Cost),
        Column<GrossMarginRow>("GrossMargin.GrossProfit",     r => r.GrossProfit),
        Column<GrossMarginRow>("GrossMargin.GrossMarginRate", r => r.GrossMarginRate),
        Column<GrossMarginRow>("GrossMargin.ShipCount",       r => r.ShipCount),
    ];
}
