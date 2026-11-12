using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;
using SmartFactory.MES.Modules.MFG.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;

namespace SmartFactory.MES.Modules.MFG.Reporting;

/// <summary>MRP 達成率的請求。**無期間概念**：看的是展開單本身的內容。</summary>
public class MrpAchievementRequest : ReportRequestBase
{
    /// <summary>限定某一次 MRP 展開。<c>null</c> ＝ 全部已執行的展開單。</summary>
    public string? MrpRunId { get; set; }

    /// <summary>品號，**精確比對**。</summary>
    public string? ProductCode { get; set; }
}

/// <summary>
/// MRP 達成率（§六 M8）。由 <c>MrpAchievementController.BuildQueryAsync</c> 抽出。
/// 行為與抽出前完全相同，由 <c>MfgReportCharacterizationTests</c> 守門（已做突變驗證）。
/// </summary>
public class MrpAchievementTool
    : IReportTool<MrpAchievementRequest, IReadOnlyList<MrpAchievementRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext _db;

    public MrpAchievementTool(ApplicationDbContext db) => _db = db;

    public string ToolKey     => "MrpAchievement";
    public string Description =>
        "列出已執行 MRP 展開的各需求列，實際供給量為庫存＋採購＋委外＋自製四項之和，"
        + "並算出達成率與短缺量，依需求日與品號升冪。只看狀態為已執行的展開單。";

    /// <summary>
    /// 照抄畫面現況的 <c>CanRead</c>。由 <c>MfgReportCharacterizationTests</c> 的閘門那條鎖住。
    /// </summary>
    // 顯式介面實作（而非 public 屬性）：理由同其他工具，見 OrderOverdueTool。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>不含金額（`MrpAchievementRowVm` 只有數量、比率與驗證訊息）→ 屬 §八「內部」級，可進自然語言目錄。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>看的是當下快照，沒有期間概念——實作完全不碰 IRelativePeriodResolver。顯式實作的理由同 <c>Permission</c>：避開 R5b 對值型別屬性的掃描。</summary>
    bool IReportToolDescriptor.RequiresPeriod => false;

    public Type RequestType  => typeof(MrpAchievementRequest);
    public Type ResponseType => typeof(IReadOnlyList<MrpAchievementRowVm>);

    public async Task<IReadOnlyList<MrpAchievementRowVm>> ExecuteAsync(
        MrpAchievementRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";

        // ⚠️ 品號用**左外聯**：主檔還沒建的品號仍要列出（單位為 null）。
        // 改成內聯會讓那批品號從缺料報表上整批消失——而那正是最該被看到的一批。
        var query =
            from mrl in _db.MrpRunLines
            join mr in _db.MrpRuns on mrl.MrpRunId equals mr.MrpRunId
            join p in _db.Products on mrl.ProductCode equals p.ProductCode into pJoin
            from p in pJoin.DefaultIfEmpty()
            where mr.PlantId == plantId
               && mr.Status == MrpRunStatus.Executed
            select new
            {
                mrl.MrpRunId,
                mrl.ProductCode,
                UomCode = p != null ? p.StockUom : null,
                mrl.RequiredDate,
                mrl.RequiredQty,
                // 四項相加。少加任何一項都會讓達成率偏低而看起來像缺料。
                ActualQty = mrl.StockQty + mrl.PurchaseQty + mrl.OutsourceQty + mrl.MakeQty,
                mrl.ValidationError,
            };

        if (!string.IsNullOrWhiteSpace(request.MrpRunId))
            query = query.Where(r => r.MrpRunId == request.MrpRunId);
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            query = query.Where(r => r.ProductCode == request.ProductCode);

        var raw = await query.AsNoTracking().ToListAsync(ct);

        return raw
            .Select(r => new MrpAchievementRowVm
            {
                MrpRunId        = r.MrpRunId,
                ProductCode     = r.ProductCode,
                UomCode         = r.UomCode,
                RequiredDate    = r.RequiredDate,
                RequiredQty     = r.RequiredQty,
                ActualQty       = r.ActualQty,
                ValidationError = r.ValidationError,
            })
            .OrderBy(r => r.RequiredDate)
            .ThenBy(r => r.ProductCode)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。**逐欄照抄該報表畫面現行的匯出輸出**，不增不刪不改順序：
    /// 這一步的目的是讓排程匯出跟上畫面，不是順手重排欄位。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<MrpAchievementRowVm>("MrpAchievement.MrpRunId",        r => r.MrpRunId),
        Column<MrpAchievementRowVm>("MrpAchievement.ProductCode",     r => r.ProductCode),
        Column<MrpAchievementRowVm>("MrpAchievement.RequiredDate",    r => r.RequiredDate.ToString("yyyy-MM-dd")),
        Column<MrpAchievementRowVm>("MrpAchievement.RequiredQty",     r => r.RequiredQty),
        Column<MrpAchievementRowVm>("MrpAchievement.ActualQty",       r => r.ActualQty),
        Column<MrpAchievementRowVm>("MrpAchievement.AchievementRate", r => r.AchievementRate),
        Column<MrpAchievementRowVm>("MrpAchievement.ShortfallQty",    r => r.ShortfallQty),
        Column<MrpAchievementRowVm>("MrpAchievement.ValidationError", r => r.ValidationError),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列。本工具的 <c>ExecuteAsync</c> 回的就是列集合，
    /// 所以這裡只是轉成契約要的 <c>IReadOnlyList&lt;object&gt;</c>；
    /// 分頁與「查不到資料 vs 拒絕執行」的規定見契約成員的說明。
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<MrpAchievementRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
