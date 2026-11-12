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

/// <summary>採購價格比較的請求。期間比對的是**進貨日**。</summary>
public class PurchasePriceRequest : ReportRequestBase
{
    public string? ProductCode  { get; set; }
    public string? SupplierCode { get; set; }
}

/// <summary>
/// 採購價格比較：實際進貨單價對上 <c>VendorPriceList</c> 的標準價。
/// 由 <c>PurchasePriceController.BuildQueryAsync</c> 抽出，行為與抽出前相同。
///
/// <para>委外那一側的對應物是 <see cref="OutsourcePriceTool"/>（收貨加工費 vs 委外報價）。
/// 兩支刻意各自獨立而不是合成一支：資料來源、查價規則（4-level vs 單層有效期）
/// 與使用者心中的問題都不同，合起來只會得到一支兩邊都不好用的報表。</para>
/// </summary>
public class PurchasePriceTool
    : IReportTool<PurchasePriceRequest, IReadOnlyList<PurchasePriceRowVm>>, IReportToolDescriptor
{
    private readonly ApplicationDbContext    _db;
    private readonly IRelativePeriodResolver _period;
    private readonly IDateTimeService        _dateTime;
    private readonly ICurrencyService        _currency;

    public PurchasePriceTool(
        ApplicationDbContext db, IRelativePeriodResolver period, IDateTimeService dateTime,
        ICurrencyService currency)
    {
        _db       = db;
        _period   = period;
        _dateTime = dateTime;
        _currency = currency;
    }

    public string ToolKey     => "PurchasePrice";
    public string Description =>
        "採購價格比較：逐筆列出進貨明細的實際單價，對上供應商報價（VendorPriceList）"
        + "的現行標準價，並算出差異與差異率。含金額。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>。</summary>
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>單價與差異都在輸出裡 → 屬 §八「含金額＝機密」。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 <c>IRelativePeriodResolver</c> 解算進貨日區間。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(PurchasePriceRequest);
    public Type ResponseType => typeof(IReadOnlyList<PurchasePriceRowVm>);

    public async Task<IReadOnlyList<PurchasePriceRowVm>> ExecuteAsync(
        PurchasePriceRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        var receiptQuery =
            from grl in _db.GoodsReceiptLines
            join gr in _db.GoodsReceipts on grl.GoodsReceiptId equals gr.GoodsReceiptId
            where gr.PlantId == plantId
               && gr.Status == DocStatus.Posted
               && gr.ReceiptDate >= range.From
               && gr.ReceiptDate <= range.To
            select new
            {
                grl.ProductCode,
                gr.SupplierCode,
                gr.ReceiptDate,
                gr.GoodsReceiptId,
                grl.UnitPrice,
                grl.CurrencyCode,
            };

        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            receiptQuery = receiptQuery.Where(r => r.ProductCode == request.ProductCode);
        if (!string.IsNullOrWhiteSpace(request.SupplierCode))
            receiptQuery = receiptQuery.Where(r => r.SupplierCode == request.SupplierCode);

        var raw = await receiptQuery.AsNoTracking().ToListAsync(ct);
        if (raw.Count == 0) return [];

        // 標準價一次撈回（避免逐列查）。⚠️ 取的是**現行有效**的報價，不是各筆進貨當日
        // 的——這是抽出前的行為，刻意照抄：改成逐筆按進貨日查是另一個口徑變更，
        // 不該夾帶在「把查詢搬進工具」這一步裡。
        var productCodes  = raw.Select(r => r.ProductCode).Distinct().ToList();
        var supplierCodes = raw.Select(r => r.SupplierCode).Distinct().ToList();
        var today         = request.AsOfDate ?? _dateTime.GetLocalDate(request.TimeZoneId);

        var stdPrices = await _db.VendorPriceLists.AsNoTracking()
            .Where(v => productCodes.Contains(v.ProductCode)
                     && supplierCodes.Contains(v.SupplierCode)
                     && v.IsActive
                     && v.ValidFrom <= today
                     && (v.ValidTo == null || v.ValidTo.Value >= today))
            .ToListAsync(ct);

        // 幣別正規化：明細沒填幣別＝**未指定＝本位幣**，不是「另一種幣別」。
        // 與 OutsourcePriceTool 同一個口徑（2026-09-23 那批的第一個缺陷）。
        var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";

        // 🐞 2026-09-24 修：鍵原本是 (SupplierCode, ProductCode)，**幣別被丟掉**，
        // 再以 `.First()` 任取一筆。同一組廠商×品號只要有兩種幣別的議定價，
        // 就會拿外幣報價去比本幣進貨——dev 庫的 F001／MAF1103 正是這個形狀
        // （NTD 187.45 與 USD 10.00 並存），比出來的差異率是四位數而完全沒有訊號。
        // 之所以三週沒踩到：`VendorPriceLists` 一直是 0 筆，這段程式從來沒有東西可比。
        // 同時把 `.First()` 改成「取最晚生效的那一筆」——現行有效的報價可能不只一筆
        // （唯一索引是含 ValidFrom 的四欄），未排序的 First() 由資料庫回傳順序決定，
        // 那不是口徑是運氣。取最晚生效＝VendorPriceList 自己文件寫的「最接近有效的議定單價」。
        var priceMap = stdPrices
            .GroupBy(v => (v.SupplierCode, v.ProductCode, Currency: Normalize(v.CurrencyCode, baseCurrency)))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.ValidFrom).First().UnitPrice);

        return raw
            .Select(r =>
            {
                priceMap.TryGetValue(
                    (r.SupplierCode, r.ProductCode, Normalize(r.CurrencyCode, baseCurrency)),
                    out var stdPrice);
                return new PurchasePriceRowVm
                {
                    ProductCode       = r.ProductCode,
                    SupplierCode      = r.SupplierCode,
                    ReceiptDate       = r.ReceiptDate,
                    GoodsReceiptId    = r.GoodsReceiptId,
                    ActualUnitPrice   = r.UnitPrice,
                    CurrencyCode      = r.CurrencyCode,
                    StandardUnitPrice = stdPrice == 0 ? null : stdPrice,
                };
            })
            .OrderBy(r => r.ProductCode)
            .ThenBy(r => r.SupplierCode)
            .ThenBy(r => r.ReceiptDate)
            .ToList();
    }

    /// <summary>
    /// 空／<c>null</c> 幣別一律視為本位幣。**「沒填」與「另一種幣別」是兩件事**：
    /// 本幣單據習慣不標幣別，拿字串直接比會把它們判成不同幣別而整批比不到價。
    /// 與 <see cref="OutsourcePriceTool"/> 的同名方法同一個口徑。
    /// </summary>
    private static string Normalize(string? currencyCode, string baseCurrency)
        => string.IsNullOrWhiteSpace(currencyCode) ? baseCurrency : currencyCode;

    /// <summary>匯出欄位（§16.1 S-5）。逐欄照抄畫面現行的匯出輸出，不增不刪不改順序。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<PurchasePriceRowVm>("PurchasePrice.ProductCode",       r => r.ProductCode),
        Column<PurchasePriceRowVm>("PurchasePrice.SupplierCode",      r => r.SupplierCode),
        Column<PurchasePriceRowVm>("PurchasePrice.ReceiptDate",       r => r.ReceiptDate.ToString("yyyy-MM-dd")),
        Column<PurchasePriceRowVm>("PurchasePrice.GoodsReceiptId",    r => r.GoodsReceiptId),
        Column<PurchasePriceRowVm>("PurchasePrice.ActualUnitPrice",   r => r.ActualUnitPrice),
        Column<PurchasePriceRowVm>("PurchasePrice.CurrencyCode",      r => r.CurrencyCode),
        Column<PurchasePriceRowVm>("PurchasePrice.StandardUnitPrice", r => r.StandardUnitPrice),
        Column<PurchasePriceRowVm>("PurchasePrice.PriceDiff",         r => r.PriceDiff),
        Column<PurchasePriceRowVm>("PurchasePrice.PriceDiffPct",      r => r.PriceDiffPct),
    ];

    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<PurchasePriceRequest>(ToolKey), user, ct))
           .Cast<object>().ToList();
}
