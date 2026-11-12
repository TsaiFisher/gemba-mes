using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.PUR.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Reporting;

/// <summary>供應商交期達成率的請求。期間比對的是**預交日**，不是到料日。</summary>
public class SupplierDeliveryRateRequest : ReportRequestBase
{
    public string? SupplierCode { get; set; }

    /// <summary>來源：<c>All</c>（採購＋委外）／<c>PO</c>（僅採購進貨）／<c>OSP</c>（僅委外收貨）。</summary>
    public string? Source { get; set; }
}

/// <summary>
/// 供應商交期達成率（pur-domain.md §5.3／§5.5）。
///
/// <para>事實建構本來就已經抽在 <see cref="IOtdFactService"/>（與供應商評分卡共用口徑），
/// 彙總在 <see cref="OtdAggregator"/>，所以這支工具只是把 Controller 的組裝那一段
/// 搬過來——查詢邏輯沒有被複製，畫面、排程與常用查詢三條路吃的是同一份事實。</para>
///
/// <para>⚠️ <b>母體是「這段期間應該交的單」</b>（2026-09-21 口徑變更）：已逾預交日仍未交清的
/// 算遲交，逾期天數以今日計。這與「這段期間到了料的單」不同，數字會比舊口徑低。</para>
/// </summary>
public class SupplierDeliveryRateTool
    : IReportTool<SupplierDeliveryRateRequest, SupplierDeliveryRateVm>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IOtdFactService         _otdFacts;
    private readonly IDateTimeService        _dateTime;

    public SupplierDeliveryRateTool(
        ApplicationDbContext db, IRelativePeriodResolver period,
        IOtdFactService otdFacts, IDateTimeService dateTime)
    {
        _db       = db;
        _period   = period;
        _otdFacts = otdFacts;
        _dateTime = dateTime;
    }

    public string ToolKey     => "SupplierDeliveryRate";
    public string Description =>
        "供應商交期達成率：以預交日落在期間內的採購／委外明細為母體，計算準時率、"
        + "平均與最大逾期天數，並給出月趨勢。已過預交日仍未交清者算遲交。不含金額。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>。</summary>
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>輸出只有筆數、天數與百分比，沒有任何金額欄位 → 屬 §八「內部」級。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => false;

    /// <summary>實作呼叫 <c>IRelativePeriodResolver</c> 解算預交日區間。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(SupplierDeliveryRateRequest);
    public Type ResponseType => typeof(SupplierDeliveryRateVm);

    public async Task<SupplierDeliveryRateVm> ExecuteAsync(
        SupplierDeliveryRateRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        // 「今天」必須是廠別當地日（R22）：逾期天數以它為基準，用伺服器時區會差一天。
        var asOf = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        var facts = await _otdFacts.BuildFactsAsync(
            range.From, range.To, request.SupplierCode,
            string.IsNullOrWhiteSpace(request.Source) ? "All" : request.Source,
            plantId, asOf);

        var (months, suppliers) = OtdAggregator.Compute(facts);

        var codes = suppliers.Select(s => s.SupplierCode).Distinct().ToList();
        var nameMap = await _db.Companies.AsNoTracking()
            .Where(c => codes.Contains(c.CompanyCode))
            .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName, ct);
        foreach (var s in suppliers)
            if (nameMap.TryGetValue(s.SupplierCode, out var n)) s.SupplierName = n;

        return new SupplierDeliveryRateVm { MonthRows = months, SupplierRows = suppliers };
    }

    /// <summary>匯出欄位（§16.1 S-5）。逐欄照抄畫面現行的供應商表匯出，不增不刪不改順序。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.SupplierCode",   r => r.SupplierCode),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.SupplierName",   r => r.SupplierName),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.TotalLines",     r => r.TotalLines),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.OnTimeLines",    r => r.OnTimeLines),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.LateLines",      r => r.LateLines),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.OnTimeRate",     r => r.OnTimeRate),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.AvgOverdueDays", r => r.AvgOverdueDays),
        Column<SupplierDeliveryRateRowVm>("SupplierDeliveryRate.MaxOverdueDays", r => r.MaxOverdueDays),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列＝供應商表（畫面的主表）。
    ///
    /// <para>⚠️ 回應是包裝型別（月趨勢＋供應商表），讓 Runner 自己攤平會產出零筆的空檔案——
    /// 契約註解點名的那個形狀。</para>
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<SupplierDeliveryRateRequest>(ToolKey), user, ct))
           .SupplierRows.Cast<object>().ToList();
}
