using System.Security.Claims;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>委外加工對帳與損耗分析的請求。期間由基底表達。</summary>
public class OutsourcePerformanceRequest : ReportRequestBase { }

/// <summary>
/// 委外加工對帳與損耗分析（RPT 批次 2）。查詢在 <see cref="IOutsourcePerformanceService"/>。
///
/// <para>⚠️ 回傳是包裝型別（Rows ＋ 七個合計），<c>GetExportRowsAsync</c> 顯式取 <c>Rows</c>。</para>
/// </summary>
public class OutsourcePerformanceTool
    : IReportTool<OutsourcePerformanceRequest, OutsourcePerformanceReport>, IReportToolDescriptor
{
    private readonly IOutsourcePerformanceService _service;
    private readonly IRelativePeriodResolver       _period;

    public OutsourcePerformanceTool(
        IOutsourcePerformanceService service, IRelativePeriodResolver period)
    {
        _service = service;
        _period  = period;
    }

    public string ToolKey     => "OutsourcePerformance";
    public string Description => "依委外廠商彙總期間內的收貨筆數、準時率、收回成品量、加工費與超耗扣款。金額為本幣。";

    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（加工費／超耗扣款）→ §八「機密」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(OutsourcePerformanceRequest);
    public Type ResponseType => typeof(OutsourcePerformanceReport);

    public async Task<OutsourcePerformanceReport> ExecuteAsync(
        OutsourcePerformanceRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range = await _period.ResolveAsync(request, ct: ct);
        return await _service.GetReportAsync(range.From, range.To, request.PlantId ?? "", ct);
    }

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<OutsourcePerformanceRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();

    /// <summary>匯出欄位：逐欄照抄畫面現行輸出。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<OutsourcePerformanceRow>("OutsourcePerformance.SupplierCode",     r => r.SupplierCode),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.SupplierName",     r => r.SupplierName),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.ReceiptCount",     r => r.ReceiptCount),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.OnTimeRate",       r => r.OnTimeRate),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.ReceivedQty",      r => r.ReceivedQty),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.ProcessingFee",    r => r.ProcessingFee),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.ScrapPenalty",     r => r.ScrapPenalty),
        Column<OutsourcePerformanceRow>("OutsourcePerformance.ScrapPenaltyRate", r => r.ScrapPenaltyRate),
    ];
}
