using System.Security.Claims;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>損益表的請求。期間由基底表達，轉成會計期別見 <see cref="FiscalPeriodMapper"/>。</summary>
public class ProfitLossRequest : ReportRequestBase { }

/// <summary>
/// 損益表（RPT 批次 2）。查詢在 <see cref="IProfitLossService"/>。
///
/// <para>⚠️ <b>期間語意與其他報表不同，排程設定時要留意</b>：本報表以**會計期間**為粒度。
/// 相對期間「昨天」不會得到「昨天的損益」，而是**昨天所屬那一整期**的損益——
/// 因為損益表本來就沒有「單日」這個粒度。要按期出表請用
/// <c>RelativePeriod.LastFiscalPeriod</c>（會計期間），而不是 <c>LastMonth</c>（日曆月）：
/// 兩者在會計行事曆等於日曆月時剛好相同，不同時只有數字會不一樣，畫面上看不出來。</para>
///
/// <para>⚠️ 回傳是包裝型別（八個彙總欄位 ＋ Lines），<c>GetExportRowsAsync</c> 顯式取 <c>Lines</c>。
/// 彙總欄位（營收、毛利、營業利益、稅後淨利…）**不進匯出**——畫面匯出現行就只輸出 Lines。</para>
/// </summary>
public class ProfitLossTool
    : IReportTool<ProfitLossRequest, ProfitLossReport>, IReportToolDescriptor
{
    private readonly IProfitLossService     _service;
    private readonly IRelativePeriodResolver _period;
    private readonly FiscalPeriodMapper     _fiscal;

    public ProfitLossTool(
        IProfitLossService service, IRelativePeriodResolver period, FiscalPeriodMapper fiscal)
    {
        _service = service;
        _period  = period;
        _fiscal  = fiscal;
    }

    public string ToolKey     => "ProfitLoss";
    public string Description => "會計期間區間的損益表，逐科目列出代碼、名稱、分類與金額。金額為本幣。";

    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（每一列都是科目金額）→ §八「機密」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(ProfitLossRequest);
    public Type ResponseType => typeof(ProfitLossReport);

    public async Task<ProfitLossReport> ExecuteAsync(
        ProfitLossRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range = await _period.ResolveAsync(request, ct: ct);
        var (from, to) = await _fiscal.RangeAsync(range, ct);
        return await _service.GetReportAsync(from, to, request.PlantId ?? "", ct);
    }

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<ProfitLossRequest>(ToolKey), user, ct))
           .Lines.Cast<object>().ToList();

    /// <summary>匯出欄位：逐欄照抄畫面現行輸出。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<ProfitLossLine>("ProfitLoss.AccountCode", l => l.AccountCode),
        Column<ProfitLossLine>("ProfitLoss.AccountName", l => l.AccountName),
        Column<ProfitLossLine>("ProfitLoss.Category",    l => l.Category),
        Column<ProfitLossLine>("ProfitLoss.Amount",      l => l.Amount),
    ];
}
