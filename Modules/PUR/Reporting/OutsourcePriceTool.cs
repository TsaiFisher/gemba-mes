using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Services;
using SmartFactory.MES.Modules.PUR.ViewModels;
using SmartFactory.MES.Modules.RPS.Contracts;
using static SmartFactory.MES.Modules.RPS.Contracts.ReportExportColumnFactory;
using SmartFactory.MES.Modules.RPS.Services;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Reporting;

/// <summary>委外價格分析的請求。期間比對的是**收貨日**。</summary>
public class OutsourcePriceRequest : ReportRequestBase
{
    public string? SupplierCode { get; set; }
    public string? ProductCode  { get; set; }
}

/// <summary>
/// 委外價格趨勢與比價分析。
///
/// <para><b>它補的缺口</b>：委外在交期面早就被既有報表涵蓋（交期達成率有 <c>source="OSP"</c>
/// 分支、供應商評分卡納入委外、委外進度自己有逾期跟催），金額面也在 2026-09-21 把
/// 採購金額分析改成含委外加工費。**唯獨價格面沒有任何東西**——採購有
/// <c>PurchasePrice</c>（進貨單價 vs <c>VendorPriceList</c>），委外有
/// <c>OutsourceQuotation</c> 報價主檔卻沒有對應的趨勢與比價。</para>
///
/// <para><b>兩個維度</b>：月趨勢（加權平均單價，本幣）與比價明細（實際 vs 應適用報價）。
/// 前者回答「加工費在漲嗎」，後者回答「這一筆是照哪張報價收的、差多少」。</para>
/// </summary>
public class OutsourcePriceTool
    : IReportTool<OutsourcePriceRequest, OutsourcePriceVm>, IReportToolDescriptor
{
    private readonly ApplicationDbContext        _db;
    private readonly IRelativePeriodResolver     _period;
    private readonly IOutsourceQuotationResolver _quotations;
    private readonly ICurrencyService            _currency;

    public OutsourcePriceTool(
        ApplicationDbContext db, IRelativePeriodResolver period,
        IOutsourceQuotationResolver quotations, ICurrencyService currency)
    {
        _db         = db;
        _period     = period;
        _quotations = quotations;
        _currency   = currency;
    }

    public string ToolKey     => "OutsourcePrice";
    public string Description =>
        "委外加工費的月趨勢與比價：逐筆列出委外收貨的實際加工費單價，對上當時應適用的"
        + "委外報價（4-level 查價），並以本幣加權平均呈現月走勢。含金額。";

    /// <summary>照抄畫面現況的 <c>CanRead</c>（<c>OutsourcePriceController</c>）。</summary>
    // 顯式介面實作的理由同其他工具：避開 R5b 對 Modules.* 下 public class 值型別屬性的掃描。
    ReportToolPermission IReportToolDescriptor.Permission => ReportToolPermission.CanRead;

    /// <summary>單價與差異金額都在輸出裡 → 屬 §八「含金額＝機密」，第一版排除在自然語言目錄外。</summary>
    bool IReportToolDescriptor.ContainsMonetaryValues => true;

    /// <summary>實作呼叫 <c>IRelativePeriodResolver</c> 解算收貨日區間。</summary>
    bool IReportToolDescriptor.RequiresPeriod => true;

    public Type RequestType  => typeof(OutsourcePriceRequest);
    public Type ResponseType => typeof(OutsourcePriceVm);

    public async Task<OutsourcePriceVm> ExecuteAsync(
        OutsourcePriceRequest request, ClaimsPrincipal? user, CancellationToken ct = default)
    {
        var range   = await _period.ResolveAsync(request, ct: ct);
        var plantId = request.PlantId ?? "";

        var query =
            from orl in _db.OutsourceReceiptLines
            join or_ in _db.OutsourceReceipts on orl.OutsourceReceiptId equals or_.OutsourceReceiptId
            join opo in _db.OutsourcePurchaseOrders
                on or_.OutsourcePurchaseOrderId equals opo.OutsourcePurchaseOrderId into opoJoin
            from opo in opoJoin.DefaultIfEmpty()
            // ⚠️ 委外收貨的口徑是 **Confirmed 以上**，不是只有 Posted——對齊採購金額分析
            // 與委外對帳報表（OutsourcePerformance）。只取 Posted 的話，同一批收貨在
            // 採購金額分析看得到、在這張表看不到，而兩邊都不會報錯。
            where or_.PlantId == plantId
               && (or_.Status == DocStatus.Confirmed || or_.Status == DocStatus.Posted)
               && or_.ReceiptDate >= range.From
               && or_.ReceiptDate <= range.To
            select new
            {
                or_.OutsourceReceiptId,
                orl.LineNo,
                or_.SupplierCode,
                or_.ReceiptDate,
                orl.ProductCode,
                orl.UnitCost,
                orl.ReceiptQty,
                LineCurrency = orl.CurrencyCode ?? or_.CurrencyCode,
                orl.ExchangeRate,
                orl.OpoLineNo,
                OpoId = opo != null ? opo.OutsourcePurchaseOrderId : null,
            };

        if (!string.IsNullOrWhiteSpace(request.SupplierCode))
            query = query.Where(r => r.SupplierCode == request.SupplierCode);
        if (!string.IsNullOrWhiteSpace(request.ProductCode))
            query = query.Where(r => r.ProductCode == request.ProductCode);

        var raw = await query.AsNoTracking().ToListAsync(ct);
        if (raw.Count == 0) return new OutsourcePriceVm();

        // 途程／工序：4-level 查價要它們才選得到最精確的那一筆報價。委外採購單明細帶著
        // 來源工單與工序，所以這裡回頭把它們補上——拿不到就退化成「廠商＋品號」那一層，
        // 這與建單時「該工序沒報價就用品號層」的行為一致，不是另一套規則。
        var opoIds = raw.Where(r => r.OpoId != null).Select(r => r.OpoId!).Distinct().ToList();
        var opoLines = await _db.OutsourcePurchaseOrderLines.AsNoTracking()
            .Where(l => opoIds.Contains(l.OutsourcePurchaseOrderId))
            .Select(l => new { l.OutsourcePurchaseOrderId, l.LineNo, l.OperationSeq, l.SourceWorkOrderId })
            .ToListAsync(ct);

        var woIds = opoLines.Where(l => l.SourceWorkOrderId != null)
                            .Select(l => l.SourceWorkOrderId!).Distinct().ToList();
        var woRouting = await _db.WorkOrders.AsNoTracking()
            .Where(w => woIds.Contains(w.WorkOrderId))
            .ToDictionaryAsync(w => w.WorkOrderId, w => w.RoutingId, ct);

        var opoLineMap = opoLines.ToDictionary(
            l => (l.OutsourcePurchaseOrderId, l.LineNo),
            l => (l.OperationSeq,
                  RoutingId: l.SourceWorkOrderId != null && woRouting.TryGetValue(l.SourceWorkOrderId, out var rt)
                             ? rt : null));

        var supplierCodes = raw.Select(r => r.SupplierCode).Distinct().ToList();
        var quotations    = await _quotations.LoadEffectiveInRangeAsync(
            supplierCodes, range.From, range.To, ct);
        var quotesBySupplier = quotations.GroupBy(q => q.SupplierCode)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 幣別正規化：明細沒填幣別＝**未指定＝本位幣**，不是「另一種幣別」。
        // 不正規化的話，沒填幣別的單（本幣單、舊單）會全部落進「幣別不同 → 無法比價」，
        // 而它們其實該比出差異。實測：開發庫 4 筆本幣委外收貨全被判成不可比。
        var baseCurrency = await _currency.GetBaseCurrencyAsync() ?? "";

        var supplierNames = await _db.Companies.AsNoTracking()
            .Where(c => supplierCodes.Contains(c.CompanyCode))
            .ToDictionaryAsync(c => c.CompanyCode, c => c.CompanyName, ct);

        var rows = new List<OutsourcePriceRowVm>(raw.Count);
        foreach (var r in raw)
        {
            int? opSeq = null; string? routingId = null;
            if (r.OpoId != null && r.OpoLineNo is { } oln
                && opoLineMap.TryGetValue((r.OpoId, oln), out var link))
                (opSeq, routingId) = (link.OperationSeq, link.RoutingId);

            // 該筆收貨當日有效的報價才算數——期間內換過價的品號，用期末那張去比
            // 會把「照舊價收的那幾筆」全部算成差異。
            var candidates = quotesBySupplier.TryGetValue(r.SupplierCode, out var qs)
                ? qs.Where(q => _quotations.IsEffectiveOn(q, r.ReceiptDate))
                : Enumerable.Empty<Modules.MFG.Models.OutsourceQuotation>();

            var quote = _quotations.Resolve(candidates, r.ProductCode, routingId, opSeq);

            rows.Add(new OutsourcePriceRowVm
            {
                ProductCode        = r.ProductCode,
                SupplierCode       = r.SupplierCode,
                SupplierName       = supplierNames.TryGetValue(r.SupplierCode, out var n) ? n : "",
                ReceiptDate        = r.ReceiptDate,
                OutsourceReceiptId = r.OutsourceReceiptId,
                LineNo             = r.LineNo,
                ActualUnitPrice    = r.UnitCost,
                CurrencyCode       = Normalize(r.LineCurrency, baseCurrency),
                QuotedUnitPrice    = quote?.UnitPrice,
                QuotedCurrencyCode = quote is null ? null : Normalize(quote.CurrencyCode, baseCurrency),
                QuotationId        = quote?.QuotationId,
            });
        }

        // 月趨勢：單價換算本幣後**以收貨量加權**平均（見 OutsourcePriceMonthRowVm 的註解）。
        var monthRows = raw
            .GroupBy(r => r.ReceiptDate.ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var qty = g.Sum(x => x.ReceiptQty);
                var localPrices = g.Select(x => x.UnitCost * (x.ExchangeRate == 0 ? 1 : x.ExchangeRate)).ToList();
                return new OutsourcePriceMonthRowVm
                {
                    YearMonth    = g.Key,
                    ReceiptLines = g.Count(),
                    // 權數為 0（整月都是零量收貨）時退回簡單平均，而不是除以零。
                    AvgUnitPriceLocal = qty == 0
                        ? Math.Round(localPrices.Average(), 4)
                        : Math.Round(g.Sum(x => x.UnitCost * (x.ExchangeRate == 0 ? 1 : x.ExchangeRate) * x.ReceiptQty) / qty, 4),
                    MinUnitPriceLocal = Math.Round(localPrices.Min(), 4),
                    MaxUnitPriceLocal = Math.Round(localPrices.Max(), 4),
                };
            })
            .ToList();

        return new OutsourcePriceVm
        {
            MonthRows = monthRows,
            Rows = rows
                .OrderBy(r => r.ProductCode)
                .ThenBy(r => r.SupplierCode)
                .ThenBy(r => r.ReceiptDate)
                .ToList(),
        };
    }

    /// <summary>
    /// 空／<c>null</c> 幣別一律視為本位幣。**「沒填」與「另一種幣別」是兩件事**：
    /// 前者是本幣單沒有特別標註，後者才需要換算；混為一談會讓可比的列被判成不可比。
    /// </summary>
    private static string Normalize(string? currencyCode, string baseCurrency)
        => string.IsNullOrWhiteSpace(currencyCode) ? baseCurrency : currencyCode;

    /// <summary>匯出欄位（§16.1 S-5）。逐欄照抄畫面現行的匯出輸出，不增不刪不改順序。</summary>
    public IReadOnlyList<ReportExportColumn> GetExportColumns(ReportRequestBase request) =>
    [
        Column<OutsourcePriceRowVm>("OutsourcePrice.ProductCode",     r => r.ProductCode),
        Column<OutsourcePriceRowVm>("OutsourcePrice.SupplierCode",    r => r.SupplierCode),
        Column<OutsourcePriceRowVm>("OutsourcePrice.SupplierName",    r => r.SupplierName),
        Column<OutsourcePriceRowVm>("OutsourcePrice.ReceiptDate",     r => r.ReceiptDate.ToString("yyyy-MM-dd")),
        Column<OutsourcePriceRowVm>("OutsourcePrice.ReceiptId",       r => r.OutsourceReceiptId),
        Column<OutsourcePriceRowVm>("OutsourcePrice.ActualUnitPrice", r => r.ActualUnitPrice),
        Column<OutsourcePriceRowVm>("OutsourcePrice.CurrencyCode",    r => r.CurrencyCode),
        Column<OutsourcePriceRowVm>("OutsourcePrice.QuotedUnitPrice", r => r.QuotedUnitPrice),
        Column<OutsourcePriceRowVm>("OutsourcePrice.QuotationId",     r => r.QuotationId),
        Column<OutsourcePriceRowVm>("OutsourcePrice.PriceDiff",       r => r.PriceDiff),
        Column<OutsourcePriceRowVm>("OutsourcePrice.PriceDiffPct",    r => r.PriceDiffPct),
    ];

    /// <summary>
    /// 匯出／排程用的整份資料列＝比價明細。
    ///
    /// <para>⚠️ 本工具的 <c>ExecuteAsync</c> 回的是**包裝型別**（月趨勢＋明細），不是集合——
    /// 契約註解點名的 <c>InventoryValuationResult</c> 那個形狀。直接讓 Runner 攤平會得到
    /// 一份格式正確但零筆的檔案，所以這裡明確指向 <c>Rows</c>。</para>
    /// </summary>
    async Task<IReadOnlyList<object>> IReportToolDescriptor.GetExportRowsAsync(
        ReportRequestBase request, ClaimsPrincipal? user, CancellationToken ct)
        => (await ExecuteAsync(request.AsToolRequest<OutsourcePriceRequest>(ToolKey), user, ct))
           .Rows.Cast<object>().ToList();
}
