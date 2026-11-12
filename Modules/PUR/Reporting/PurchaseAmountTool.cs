using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Reporting;

/// <summary>採購金額分析的請求。期間是**年度**——畫面選一個年、看 12 個月。</summary>
public class PurchaseAmountRequest : ReportRequestBase
{
    /// <summary>分組維度：<c>Supplier</c>（預設）或 <c>Product</c>。</summary>
    public string? GroupByDimension { get; set; }

    /// <summary>來源：<c>All</c>（採購＋委外）／<c>PO</c>（僅採購進貨）／<c>OSP</c>（僅委外收貨）。</summary>
    public string? Source { get; set; }
}

/// <summary>
/// 採購金額分析（pur-domain.md §5）。由 <c>PurchaseAmountController.BuildQueryAsync</c> 抽出，
/// 行為與抽出前完全相同（<c>PurReportCharacterizationTests</c> 守門）。
///
/// <para><b>金額一律取 <c>AmountLocal</c></b>（存檔時已以當日匯率換算並鎖定），不在這裡乘匯率：
/// 早期寫法是 <c>TotalAmount × ExchangeRate</c>，而 <c>TotalAmount</c> 當時存的已是本幣，
/// 外幣單據因此被重複換算。</para>
///
/// <para><b>含委外加工費</b>（2026-09-21）：在此之前只讀 <c>GoodsReceiptLines</c>，
/// <c>Outsource</c> 出現 0 次——採購總支出被系統性低估，而金額越大的委外廠越看不見。</para>
/// </summary>
public class PurchaseAmountTool
    : IReportTool<PurchaseAmountRequest, IReadOnlyList<PurchaseAmountRowVm>>, IReportToolDescriptor
{
    /// <summary>來源別。與供應商交期達成率（§5.3）用同一組值，兩張表才對得起來。</summary>
    public const string SourceAll = "All";
    public const string SourcePo  = "PO";
    public const string SourceOsp = "OSP";

    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IDateTimeService        _dateTime;

    public PurchaseAmountTool(
        ApplicationDbContext db, IRelativePeriodResolver period, IDateTimeService dateTime)
    {
        _db       = db;
        _period   = period;
        _dateTime = dateTime;
    }

    public string ToolKey     => "PurchaseAmount";
    public string Description =>
        "採購金額分析：指定年度，依供應商或品號分組，逐月彙總本幣採購金額與進貨筆數。"
        + "統計含委外加工費。含金額。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>。</summary>
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary><c>TotalAmount</c> 是本幣採購金額 → 屬 §八「含金額＝機密」。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 <c>IRelativePeriodResolver</c> 解算年度。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(PurchaseAmountRequest);
    public Type ResponseType => typeof(IReadOnlyList<PurchaseAmountRowVm>);

    /// <summary>
    /// 一行金額事實。採購進貨與委外收貨的欄位語意一對一（本幣金額已於存檔時以當日匯率鎖定），
    /// 所以兩邊投影成同一個型別後在記憶體合併，而不是寫兩套分組。
    /// </summary>
    /// record class 而非 record struct：EF 的 AsNoTracking 限定參考型別。
    private sealed record AmountFact(
        string GroupKey, int Year, int Month, decimal AmountLocal, string DocId);

    public async Task<IReadOnlyList<PurchaseAmountRowVm>> ExecuteAsync(
        PurchaseAmountRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var plantId = request.PlantId ?? "";
        var groupBy = string.IsNullOrWhiteSpace(request.GroupByDimension) ? "Supplier" : request.GroupByDimension;
        var source  = string.IsNullOrWhiteSpace(request.Source) ? SourceAll : request.Source;

        // 年度的來源：相對期間解算後取迄日的年份；兩者都沒給時退回今年
        // （＝抽出前 Controller 的 `year ?? Now.Year`）。
        int refYear;
        if (request.Period is not null || request.AbsolutePeriod is not null)
            refYear = (await _period.ResolveAsync(request, ct: ct)).To.Year;
        else
            refYear = _dateTime.Now.Year;

        var start = new DateOnly(refYear, 1, 1);
        var end   = new DateOnly(refYear, 12, 31);
        var byProduct = groupBy == "Product";

        var raw = new List<AmountFact>();

        if (source is SourceAll or SourcePo)
        {
            raw.AddRange(await (
                from grl in _db.GoodsReceiptLines
                join gr in _db.GoodsReceipts on grl.GoodsReceiptId equals gr.GoodsReceiptId
                where gr.PlantId == plantId
                   && gr.Status == DocStatus.Posted
                   && gr.ReceiptDate >= start
                   && gr.ReceiptDate <= end
                select new AmountFact(
                    byProduct ? grl.ProductCode : gr.SupplierCode,
                    gr.ReceiptDate.Year, gr.ReceiptDate.Month,
                    grl.AmountLocal, gr.GoodsReceiptId))
                .AsNoTracking().ToListAsync(ct));
        }

        // ⚠️ 委外收貨的口徑是 Confirmed 以上（不只 Posted），對齊委外對帳報表
        // （OutsourcePerformance）。兩邊取不同狀態會系統性各取一半。
        if (source is SourceAll or SourceOsp)
        {
            raw.AddRange(await (
                from orl in _db.OutsourceReceiptLines
                join or_ in _db.OutsourceReceipts on orl.OutsourceReceiptId equals or_.OutsourceReceiptId
                where or_.PlantId == plantId
                   && (or_.Status == DocStatus.Confirmed || or_.Status == DocStatus.Posted)
                   && or_.ReceiptDate >= start
                   && or_.ReceiptDate <= end
                select new AmountFact(
                    byProduct ? orl.ProductCode : or_.SupplierCode,
                    or_.ReceiptDate.Year, or_.ReceiptDate.Month,
                    orl.AmountLocal, or_.OutsourceReceiptId))
                .AsNoTracking().ToListAsync(ct));
        }

        return raw
            .GroupBy(f => new { f.GroupKey, f.Year, f.Month })
            .Select(g => new PurchaseAmountRowVm
            {
                GroupKey     = g.Key.GroupKey,
                Year         = g.Key.Year,
                Month        = g.Key.Month,
                TotalAmount  = g.Sum(x => x.AmountLocal),
                // 單號去重：一張進貨單有多行明細，筆數算的是單不是行。
                ReceiptCount = g.Select(x => x.DocId).Distinct().Count(),
            })
            .OrderBy(r => r.GroupKey).ThenBy(r => r.Year).ThenBy(r => r.Month)
            .ToList();
    }

    /// <summary>
    /// 匯出欄位（§16.1 S-5）。第一欄的標題隨 <c>GroupByDimension</c> 在「供應商」與「品號」
    /// 之間切換——這正是 <c>GetExportColumns</c> 做成方法而非屬性的理由。
    /// </summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request)
    {
        var byProduct = request is PurchaseAmountRequest { GroupByDimension: "Product" };
        return
        [
            Column<PurchaseAmountRowVm>(
                byProduct ? "PurchaseAmount.GroupKeyProduct" : "PurchaseAmount.GroupKeySupplier",
                r => r.GroupKey),
            Column<PurchaseAmountRowVm>("PurchaseAmount.YearMonth",    r => r.YearMonth),
            Column<PurchaseAmountRowVm>("PurchaseAmount.TotalAmount",  r => r.TotalAmount),
            Column<PurchaseAmountRowVm>("PurchaseAmount.ReceiptCount", r => r.ReceiptCount),
        ];
    }

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<PurchaseAmountRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
