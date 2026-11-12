using System.Security.Claims;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>產品邊際貢獻分析的請求。期間由基底表達。</summary>
public class ContributionMarginRequest : ReportRequestBase { }

/// <summary>
/// 產品邊際貢獻分析（RPT 批次 2）。查詢在 <see cref="IContributionMarginService"/>。
///
/// <para>⚠️ <b>變動成本分類一律用系統參數的預設值</b>（<c>policyOverride: null</c>）。
/// 畫面上那三個核取方塊（材料／人工／製費算不算變動）是**互動式覆寫**，
/// 屬於「這一次查詢想怎麼看」；排程是無人看管的定期產出，沒有人在那一刻做這個選擇。
/// 把三個布林值存進排程設定看似更彈性，實際是讓同一份報表在畫面與信箱裡採用不同的成本分類
/// 而沒有任何標示——要調整分類請改系統參數，那裡改一次全站一致。</para>
///
/// <para>⚠️ 回傳是包裝型別，<c>GetExportRowsAsync</c> 顯式取 <c>Rows</c>。</para>
/// </summary>
public class ContributionMarginTool
    : IReportTool<ContributionMarginRequest, ContributionMarginReport>, IReportToolDescriptor
{
    private readonly IContributionMarginService _service;
    private readonly IRelativePeriodResolver    _period;

    public ContributionMarginTool(
        IContributionMarginService service, IRelativePeriodResolver period)
    {
        _service = service;
        _period  = period;
    }

    public string ToolKey     => "ContributionMargin";
    public string Description => "依品號列出期間內的營收、銷貨成本、變動成本與邊際貢獻率。變動成本分類取系統參數預設。金額為本幣。";

    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（營收／成本／邊際貢獻）→ §八「機密」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(ContributionMarginRequest);
    public Type ResponseType => typeof(ContributionMarginReport);

    public async Task<ContributionMarginReport> ExecuteAsync(
        ContributionMarginRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range = await _period.ResolveAsync(request, ct: ct);
        return await _service.BuildAsync(
            range.From, range.To, request.PlantId ?? "", policyOverride: null, ct);
    }

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<ContributionMarginRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();

    /// <summary>
    /// 匯出欄位：逐欄照抄畫面現行輸出。
    /// 變動占比那一欄畫面是 <c>Round(VariableRatio * 100, 2)</c>（比率→百分比），照抄。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<ContributionMarginRow>("ContributionMargin.ProductCode",       r => r.ProductCode),
        Column<ContributionMarginRow>("ContributionMargin.ProductName",       r => r.ProductName),
        Column<ContributionMarginRow>("ContributionMargin.Revenue",           r => r.Revenue),
        Column<ContributionMarginRow>("ContributionMargin.Cost",              r => r.Cost),
        Column<ContributionMarginRow>("ContributionMargin.VariableRatio",     r => Math.Round(r.VariableRatio * 100m, 2)),
        Column<ContributionMarginRow>("ContributionMargin.VariableCost",      r => r.VariableCost),
        Column<ContributionMarginRow>("ContributionMargin.ContributionMargin",r => r.ContributionMargin),
        Column<ContributionMarginRow>("ContributionMargin.ContributionRate",  r => r.ContributionRate),
    ];
}
