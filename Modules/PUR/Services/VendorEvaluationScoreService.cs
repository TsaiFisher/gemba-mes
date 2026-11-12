using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Services;

/// <summary>
/// 自動分數的計算依據（透明化：UI 顯示進貨量/不良量/到料筆數）。
/// Quality/Delivery 為 null＝期間無資料（UI 以滿分 100 帶入，存檔時同樣視為 100）。
/// </summary>
public sealed record VendorScoreBasis(
    decimal ReceiptQty, decimal NgQty, decimal? QualityScore,
    int OtdTotalLines, int OtdOnTimeLines, decimal? DeliveryScore);

/// <summary>
/// 供應商評分卡自動分數（pur-domain.md §6.2，全公司跨廠彙總）：
/// QualityScore＝(1 − 不良量/過帳進貨量)×100。不良量＝NCR（SourceType=GoodsReceipt、非作廢）
/// ＋ 未開立 NCR 的 IQC 判退量（2026-09-21 補；原本只認 NCR，「驗退了但沒開 NCR」會得到滿分）；
/// DeliveryScore＝OTD Rate×100（口徑同 §5.3 交期達成率報表，PO＋委外雙來源）。
/// </summary>
public interface IVendorEvaluationScoreService
{
    Task<VendorScoreBasis> ComputeAsync(string supplierCode, int year, int? quarter);
    Task<SrmWeights> GetWeightsAsync();
}

public class VendorEvaluationScoreService : IVendorEvaluationScoreService
{
    private readonly ApplicationDbContext _context;
    private readonly IOtdFactService      _otdFacts;
    private readonly ISystemParamService  _systemParam;
    private readonly IDateTimeService     _dateTime;

    public VendorEvaluationScoreService(
        ApplicationDbContext context, IOtdFactService otdFacts, ISystemParamService systemParam,
        IDateTimeService dateTime)
    {
        _context     = context;
        _otdFacts    = otdFacts;
        _systemParam = systemParam;
        _dateTime    = dateTime;
    }

    public async Task<VendorScoreBasis> ComputeAsync(string supplierCode, int year, int? quarter)
    {
        var (periodFrom, periodTo) = VendorEvaluationCalculator.PeriodOf(year, quarter);

        // 進貨量：期間內已過帳進貨明細（全公司評核 → 跨廠 IgnoreQueryFilters）
        var receipts = await (
            from gr  in _context.GoodsReceipts.IgnoreQueryFilters()
            join grl in _context.GoodsReceiptLines on gr.GoodsReceiptId equals grl.GoodsReceiptId
            where gr.SupplierCode == supplierCode
               && gr.Status == DocStatus.Posted
               && gr.ReceiptDate >= periodFrom
               && gr.ReceiptDate <= periodTo
            select new { gr.GoodsReceiptId, grl.ReceiptQty })
            .AsNoTracking().ToListAsync();

        var receiptQty = receipts.Sum(r => r.ReceiptQty);

        // 不良量：兩個來源相加，依進貨單號歸屬到期間內的進貨。
        //
        // ⚠️ 2026-09-21 之前只認 NCR，於是「IQC 判退了但沒開 NCR」＝品質滿分——
        // 而開不開 NCR 是人工判斷，越是小額、當場退貨處理掉的不良，越不會留下 NCR。
        // 檢驗單的 FailQty 是抽驗判退數，本來就是可查證的事實，沒有理由不算。
        decimal ngQty = 0;
        if (receipts.Count > 0)
        {
            var grIds = receipts.Select(r => r.GoodsReceiptId).Distinct().ToList();

            var ncrQty = await _context.NonConformanceReports.IgnoreQueryFilters()
                .Where(n => n.SourceType == "GoodsReceipt"
                         && n.SourceId != null && grIds.Contains(n.SourceId)
                         && n.NcrStatus != NcrStatus.Void)
                .SumAsync(n => (decimal?)n.Qty) ?? 0;

            // 只取已開立 NCR 之外的檢驗單，否則同一批不良會被算兩次
            // （IQC 判退 → 開 NCR 是常見流程，兩邊都留了紀錄）。
            var ncrSourceIds = await _context.NonConformanceReports.IgnoreQueryFilters()
                .Where(n => n.SourceType == "GoodsReceipt"
                         && n.SourceId != null && grIds.Contains(n.SourceId)
                         && n.NcrStatus != NcrStatus.Void)
                .Select(n => n.SourceId!)
                .Distinct()
                .ToListAsync();

            var iqcQty = await _context.InspectionOrders.IgnoreQueryFilters()
                .Where(i => i.InspectionType == "IQC"
                         && i.Status == "Fail"
                         && grIds.Contains(i.SourceDocId)
                         && !ncrSourceIds.Contains(i.SourceDocId))
                .SumAsync(i => (decimal?)i.FailQty) ?? 0;

            ngQty = ncrQty + iqcQty;
        }

        // 交期：口徑同 §5.3（PO＋委外），plantId=null＝全公司。
        // asOf 走廠別當地日：評核已結束的期間時它只影響「至今仍未交清」那一類的遲延天數，
        // 但評核當期（期間尚未結束）時，它決定哪些未交單已經算逾期。
        var asOf  = _dateTime.GetLocalDate();
        var facts = await _otdFacts.BuildFactsAsync(periodFrom, periodTo, supplierCode, "All", plantId: null, asOf);
        int otdTotal  = facts.Count;
        int otdOnTime = facts.Count(f => f.IsOnTime);

        return new VendorScoreBasis(
            receiptQty, ngQty, VendorEvaluationCalculator.QualityScore(receiptQty, ngQty),
            otdTotal, otdOnTime, VendorEvaluationCalculator.DeliveryScore(otdTotal, otdOnTime));
    }

    public async Task<SrmWeights> GetWeightsAsync()
    {
        var d = SrmWeights.Default;
        return new SrmWeights(
            await ReadAsync(ProjectZSystemParamKeys.SrmWeightQuality,  d.Quality),
            await ReadAsync(ProjectZSystemParamKeys.SrmWeightDelivery, d.Delivery),
            await ReadAsync(ProjectZSystemParamKeys.SrmWeightPrice,    d.Price),
            await ReadAsync(ProjectZSystemParamKeys.SrmWeightService,  d.Service));
    }

    private async Task<decimal> ReadAsync(string key, decimal fallback)
    {
        var raw = await _systemParam.GetAsync(key);
        return decimal.TryParse(raw, out var v) && v >= 0 ? v : fallback;
    }
}
