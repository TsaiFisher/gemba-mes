using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.ORD.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;

namespace SmartFactory.MES.Modules.ORD.Reporting;

/// <summary>銷售排行的請求。<c>GroupBy</c> 取 <c>"Product"</c>，其餘一律視為依客戶。</summary>
public class SalesRankingRequest : ReportRequestBase;

/// <summary>
/// 銷售排行（§六 O2）。由 <c>SalesRankingController</c> 的私有 <c>BuildQueryAsync</c> 抽出，
/// 供畫面、排程、未來的 LLM 共用同一份邏輯——**不得複製，否則必然漂移**（§5.4）。
///
/// 行為與抽出前完全相同，由 <c>OrdReportCharacterizationTests</c> 守門。
/// </summary>
public class SalesRankingTool
    : IReportTool<SalesRankingRequest, IReadOnlyList<SalesRankingRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;

    public SalesRankingTool(ApplicationDbContext db, IRelativePeriodResolver period)
    {
        _db     = db;
        _period = period;
    }

    public string ToolKey     => "SalesRanking";
    public string Description => "依客戶或品項彙總指定期間的銷售金額、訂單數與明細筆數，依金額降冪排名。不含草稿單。";

    /// <summary>含金額＝機密，動詞 <c>CanRead</c>，分級由 MenuPermission 資料承載（§14 決議 #2）。</summary>
    // 顯式介面實作（而非 public 屬性）：R5b 掃所有 Modules.* 下 public class 的
    // 非可空實值型別屬性，要求補雙語驗證資源鍵。那條規則針對的是表單欄位，
    // 而本類別是服務、Permission 也不會出現在任何 {0} 驗證訊息裡。顯式實作讓它
    // 只透過 IReportToolDescriptor 取用，既不觸發規則也不必為它造一個假的欄位名鍵。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>
    /// 含金額（`TotalAmount`（銷售金額））→ 依 §十三 Phase 2 **預設不進自然語言目錄**。
    /// 這不影響畫面與排程：有權限的人照樣開得了報表，差別只在能不能用一句話問到它。
    /// 要放行請開 <c>Features:QaAssistant:AllowMonetaryTools</c>（見 <c>QaToolSchemaBuilder</c>）。
    /// </summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 IRelativePeriodResolver 解算查詢區間。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(SalesRankingRequest);
    public Type ResponseType => typeof(IReadOnlyList<SalesRankingRowVm>);

    /// <summary>
    /// 本工具的 <c>ExecuteAsync</c> 本來就回整份集合，直接轉交即可。
    /// 仍須顯式實作而非讓基底猜——猜錯的那兩支正是靜默產出空白報表的來源。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<SalesRankingRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();

    public async Task<IReadOnlyList<SalesRankingRowVm>> ExecuteAsync(
        SalesRankingRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        var query =
            from sol in _db.SalesOrderLines
            join so  in _db.SalesOrders on sol.SalesOrderId equals so.SalesOrderId
            where so.PlantId == plantId
               && so.OrderDate >= range.From && so.OrderDate <= range.To
               && so.ApprovalStatus != DocumentApprovalStatus.Draft
            select new { so.SalesOrderId, so.CompanyCode, sol.ProductCode, sol.LineAmount };

        var raw = await query.AsNoTracking().ToListAsync(ct);

        // 抽出前這裡是兩段一字不差的 Select 複製，只差分組鍵；合併成一處，彙總邏輯不再有兩份。
        var grouped = request.GroupBy == "Product"
            ? raw.GroupBy(r => r.ProductCode)
            : raw.GroupBy(r => r.CompanyCode);

        return grouped
            .Select(g => new SalesRankingRowVm
            {
                GroupKey    = g.Key,
                TotalAmount = g.Sum(r => r.LineAmount),
                OrderCount  = g.Select(r => r.SalesOrderId).Distinct().Count(),
                LineCount   = g.Count(),
            })
            .OrderByDescending(r => r.TotalAmount)
            .Select((r, i) => { r.Rank = i + 1; return r; })
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄畫面現行的匯出輸出**。
    ///
    /// <para>第二欄的欄頭隨 <c>GroupBy</c> 變——這正是 <c>GetExportColumns</c> 做成
    /// 方法而不是屬性的原因。判定沿用本工具分群的同一條規則（<c>"Product"</c> 以外
    /// 一律視為依客戶），兩處若各自判斷就會出現「欄頭寫客戶、內容是品號」。</para>
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<SalesRankingRowVm>("SalesRanking.Rank", r => r.Rank),
        Column<SalesRankingRowVm>(
            request.GroupBy == "Product"
                ? "SalesRanking.GroupKeyProduct"
                : "SalesRanking.GroupKeyCustomer",
            r => r.GroupKey),
        Column<SalesRankingRowVm>("SalesRanking.TotalAmount", r => r.TotalAmount),
        Column<SalesRankingRowVm>("SalesRanking.OrderCount",  r => r.OrderCount),
        Column<SalesRankingRowVm>("SalesRanking.LineCount",   r => r.LineCount),
    ];
}
