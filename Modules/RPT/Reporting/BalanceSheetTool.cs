using System.Security.Claims;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.RPS.Contracts;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Modules.RPT.Models;
using SmartFactory.MES.Modules.RPT.Services;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.RPT.Reporting;

/// <summary>資產負債表的請求。期間由基底表達，取迄日所在的那一期（見 <see cref="FiscalPeriodMapper.AsOfAsync"/>）。</summary>
public class BalanceSheetRequest : ReportRequestBase { }

/// <summary>
/// 匯出用的一列：把分區的類別攤平到每一列上。
///
/// <para><b>為什麼要這個型別</b>：本報表的回應是 <c>Sections → Items</c> 兩層，
/// 而 <c>BalanceSheetItem</c> 自己**不知道它屬於哪一區**。匯出是一張平表，
/// 「類別」必須是每一列都有的欄位，所以在攤平的當下把它帶下來。
/// 畫面 <c>Export</c> 現行就是這樣做的（`SelectMany` 時把 `s.Category` 併進列）。</para>
/// </summary>
public sealed record BalanceSheetExportRow(string Category, BalanceSheetItem Item);

/// <summary>
/// 資產負債表（RPT 批次 2）。查詢在 <see cref="IBalanceSheetService"/>。
///
/// <para>⚠️ <b>時點報表</b>：service 收的是單一 <c>asOfPeriod</c> 而不是區間。
/// 相對期間解算出的區間會取**迄日**所在的那一期——「上個月」＝截至上月月底的餘額。
/// 用起日會得到月初，而那兩個數字都「像是」對的。</para>
///
/// <para>⚠️ <b>類別欄的值是在地化的</b>（資產／負債／股東權益 vs Assets／Liabilities／Equity），
/// 所以本工具注入欄頭資源——與 <c>WorkOrderProgressTool</c> 的「逾期＝是／Y」同一個做法。
/// 未知的類別碼原樣輸出，不猜也不丟掉：來源多一個分區時，匯出檔要看得出來多了什麼。</para>
/// </summary>
public class BalanceSheetTool
    : IReportTool<BalanceSheetRequest, BalanceSheetReport>, IReportToolDescriptor
{
    private readonly IBalanceSheetService   _service;
    private readonly IRelativePeriodResolver _period;
    private readonly FiscalPeriodMapper     _fiscal;
    private readonly IStringLocalizer<ProjectZReportHeaderResource> _headers;

    public BalanceSheetTool(
        IBalanceSheetService service,
        IRelativePeriodResolver period,
        FiscalPeriodMapper fiscal,
        IStringLocalizer<ProjectZReportHeaderResource> headers)
    {
        _service = service;
        _period  = period;
        _fiscal  = fiscal;
        _headers = headers;
    }

    public string ToolKey     => "BalanceSheet";
    public string Description => "截至某一會計期間的資產負債表，逐科目列出類別、代碼、名稱與餘額。金額為本幣。";

    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>含金額（科目餘額）→ §八「機密」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(BalanceSheetRequest);
    public Type ResponseType => typeof(BalanceSheetReport);

    public async Task<BalanceSheetReport> ExecuteAsync(
        BalanceSheetRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var asOf    = await _fiscal.AsOfAsync(range, ct);
        return await _service.GetReportAsync(asOf, request.PlantId ?? "", ct);
    }

    /// <summary>兩層結構攤平成一層，類別跟著每一列走。</summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
    {
        var report = await ExecuteAsync(request.AsToolRequest<BalanceSheetRequest>(ToolKey), user, ct);
        return report.Sections
            .SelectMany(s => s.Items.Select(i => (object)new BalanceSheetExportRow(s.Category, i)))
            .ToList();
    }

    /// <summary>匯出欄位：逐欄照抄畫面現行輸出（類別、科目代碼、科目名稱、餘額）。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<BalanceSheetExportRow>("BalanceSheet.Category",    r => CategoryLabel(r.Category)),
        Column<BalanceSheetExportRow>("BalanceSheet.AccountCode", r => r.Item.AccountCode),
        Column<BalanceSheetExportRow>("BalanceSheet.AccountName", r => r.Item.AccountName),
        Column<BalanceSheetExportRow>("BalanceSheet.Balance",     r => r.Item.Balance),
    ];

    /// <summary>
    /// 類別碼 → 在地化名稱。查不到鍵時**原樣回傳類別碼**，不回傳鍵本身：
    /// <c>IStringLocalizer</c> 查不到時會靜默回鍵（`BalanceSheet.CategoryAssets`），
    /// 那在匯出檔裡看起來只是「怪」，而原始碼至少還看得懂。
    /// </summary>
    private string CategoryLabel(string category)
    {
        var value = _headers[$"BalanceSheet.Category{category}"];
        return value.ResourceNotFound ? category : value.Value;
    }
}
