using System.Security.Claims;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>
/// 多維度銷售毛利分析的請求。彙總維度走基底的 <c>GroupBy</c>（Customer／Product／Month），
/// **不另立 <c>Dimension</c> 欄位**——同一個概念在請求上只該有一個名字，
/// 兩個欄位並存時「哪一個贏」會變成每個呼叫端各自的猜測。
/// </summary>
public class MarginAnalysisRequest : ReportRequestBase { }

/// <summary>
/// 多維度銷售毛利分析（RPT 批次 2）。查詢邏輯在 <see cref="IMarginAnalysisService"/>，
/// 本工具只做期間解算、維度轉接與欄位宣告。
///
/// <para>⚠️ 維度未指定時傳 <c>"Product"</c>，與畫面 <c>Export</c> 的預設值相同
/// （service 端另有 <c>Normalize</c> 會把無法辨識的值收斂掉，這裡不重複那層判斷）。</para>
///
/// <para>⚠️ 欄頭固定是「維度」而不隨 <c>GroupBy</c> 變——這點**與 <c>SalesRanking</c> 相反**，
/// 因為畫面現行輸出就是固定字樣。照抄現況；要改成隨維度變是另一個決定。</para>
/// </summary>
public class MarginAnalysisTool
    : IReportTool<MarginAnalysisRequest, MarginAnalysisReport>, IReportToolDescriptor
{
    /// <summary>維度未指定時的預設，照抄畫面 <c>Export</c> 的預設參數值。</summary>
    internal const string DefaultDimension = "Product";

    private readonly IMarginAnalysisService _service;
    private readonly IRelativePeriodResolver _period;

    public MarginAnalysisTool(IMarginAnalysisService service, IRelativePeriodResolver period)
    {
        _service = service;
        _period  = period;
    }

    public string ToolKey     => "MarginAnalysis";
    public string Description => "依客戶／品號／月份三選一的維度彙總期間內的營收、銷貨成本、毛利與毛利率。金額為本幣。";

    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（營收／成本／毛利）→ §八「機密」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(MarginAnalysisRequest);
    public Type ResponseType => typeof(MarginAnalysisReport);

    public async Task<MarginAnalysisReport> ExecuteAsync(
        MarginAnalysisRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range = await _period.ResolveAsync(request, ct: ct);
        return await _service.GetReportAsync(
            range.From, range.To, request.PlantId ?? "",
            string.IsNullOrWhiteSpace(request.GroupBy) ? DefaultDimension : request.GroupBy,
            ct);
    }

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<MarginAnalysisRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();

    /// <summary>匯出欄位：逐欄照抄畫面現行輸出。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<MarginRow>("MarginAnalysis.DimensionName",   r => r.DimensionName),
        Column<MarginRow>("MarginAnalysis.Revenue",         r => r.Revenue),
        Column<MarginRow>("MarginAnalysis.Cost",            r => r.Cost),
        Column<MarginRow>("MarginAnalysis.GrossProfit",     r => r.GrossProfit),
        Column<MarginRow>("MarginAnalysis.GrossMarginRate", r => r.GrossMarginRate),
        Column<MarginRow>("MarginAnalysis.ShipCount",       r => r.ShipCount),
    ];
}
