using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.PUR.Services;

/// <summary>
/// 採購單的簽核摘要。
///
/// <para>⚠️ <b>在地化用 <c>ProjectZSharedResource</c> 不是 Core 的 <c>SharedResource</c></b>：
/// PUR 模組的 Controller 一律注入前者（見 <c>PurchaseOrderController</c>）。
/// 用錯來源不會編譯失敗、也不會擲例外——<c>IStringLocalizer</c> 查不到鍵時**回傳鍵名本身**，
/// 於是簽核人會在手機上看到一排 <c>SupplierCode</c> 這種英文代碼。</para>
///
/// <para><b>金額取表頭的 <c>TotalAmount</c>，而它已經是本幣</b>
/// （<c>PurchaseOrder.TotalAmount</c> 的註解：「本位幣總金額（各明細 AmountLocal 加總）。
/// 明細可為不同幣別，故表頭一律以本幣合計」）。所以這裡**不做任何換算**，
/// 幣別欄位給的是本位幣代碼而不是某一行的交易幣——明細跨幣別時，
/// 拿其中一行的幣別去標整張單的金額是錯的。</para>
/// </summary>
public sealed class PurchaseOrderApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "PurchaseOrder";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;
    private readonly ICurrencyService _currency;

    public PurchaseOrderApprovalSummary(
        IStringLocalizer<ProjectZSharedResource> localizer,
        ICurrencyService currency)
    {
        _l = localizer;
        _currency = currency;
    }

    public async Task<ApprovalSummary?> GetAsync(ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        // ⚠️ 不加 IgnoreQueryFilters：查不到就是查不到。全域廠別／資料權限過濾擋掉的單，
        //    簽核人本來就不該在手機上看到內容——那正是過濾要防的事。
        var po = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PurchaseOrderId == documentId, ct);
        if (po == null) return null;

        var lines = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.PurchaseOrderId == documentId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        var supplierName = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == po.SupplierCode)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["SupplierCode"], supplierName is null
                ? po.SupplierCode
                : $"{po.SupplierCode} — {supplierName}"),
        };
        // 貿易條件擺在備註之前：簽核者要決定的正是「這些條件下誰付運費關稅」，
        // 那是金額之外最影響這張單成本的一格。
        if (!string.IsNullOrWhiteSpace(po.TradeTermCode))
            rows.Add(new(_l["TradeTerm"], po.TradeTermCode));
        if (!string.IsNullOrWhiteSpace(po.DeliveryAddress))
            rows.Add(new(_l["DeliveryAddress"], po.DeliveryAddress));
        if (!string.IsNullOrWhiteSpace(po.Remarks))
            rows.Add(new(_l["Remarks"], po.Remarks));

        return new ApprovalSummary(
            DocumentId: po.PurchaseOrderId,
            PlantId: po.PlantId,
            Counterparty: supplierName ?? po.SupplierCode,
            AmountLocal: po.TotalAmount,
            CurrencyCode: await _currency.GetBaseCurrencyAsync(),
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines)
                .Select(l => new ApprovalSummaryLine(l.ProductCode, l.OrderQty, l.UomCode, l.AmountLocal))
                .ToList(),
            TotalLineCount: lines.Count);
    }
}

/// <summary>
/// 請購單的簽核摘要。
///
/// <para>⚠️ <b>請購單沒有任何金額欄位</b>——<c>PurchaseRequest</c> 上沒有 TotalAmount，
/// 明細只有數量沒有單價（採購價要到採購單才決定）。所以 <c>AmountLocal</c> 回 <c>null</c>，
/// 那是「這張單沒有金額概念」，**不是金額 0**。
/// 手機端必須把兩者顯示成不同的東西，否則「金額 0 元的採購單」與請購單在畫面上長得一樣。</para>
/// </summary>
public sealed class PurchaseRequestApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "PurchaseRequest";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;

    public PurchaseRequestApprovalSummary(IStringLocalizer<ProjectZSharedResource> localizer) => _l = localizer;

    public async Task<ApprovalSummary?> GetAsync(ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        var pr = await db.PurchaseRequests.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PurchaseRequestId == documentId, ct);
        if (pr == null) return null;

        var lines = await db.PurchaseRequestLines.AsNoTracking()
            .Where(l => l.PurchaseRequestId == documentId)
            .OrderBy(l => l.LineNo)
            .ToListAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["RequestedBy"], pr.RequestedBy),
        };
        if (!string.IsNullOrWhiteSpace(pr.Remarks))
            rows.Add(new(_l["Remarks"], pr.Remarks));

        return new ApprovalSummary(
            DocumentId: pr.PurchaseRequestId,
            PlantId: pr.PlantId,
            Counterparty: null,          // 請購沒有往來對象——供應商要到採購單才決定
            AmountLocal: null,           // 見類別註解：沒有金額概念，不是 0
            CurrencyCode: null,
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines)
                .Select(l => new ApprovalSummaryLine(l.ProductCode, l.RequestQty, l.UomCode, null))
                .ToList(),
            TotalLineCount: lines.Count);
    }
}


/// <summary>
/// 進貨單與進貨退出的簽核摘要。
///
/// <para><b>兩者共用同一份實作</b>：欄位結構完全一致（表頭 <c>SupplierCode</c> ＋
/// 本幣 <c>TotalAmount</c>、明細品號／數量／<c>AmountLocal</c>），差別只在數量欄位的名字
/// （<c>ReceiptQty</c> vs <c>ReturnQty</c>）。抄成兩份的下場是其中一份日後被改、另一份沒有——
/// 而簽核人看到的差異不會有人發現，因為兩張單本來就長得不一樣。</para>
///
/// <para>⚠️ <b>金額取表頭的 <c>TotalAmount</c>，它已經是本幣</b>（實體註解：
/// 「本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計」），
/// 所以這裡不做任何換算，幣別欄位給的是**本位幣代碼**——明細跨幣別時，拿其中一行的
/// 交易幣去標整張單的金額是錯的。</para>
/// </summary>
public abstract class SupplierDocumentApprovalSummaryBase : IApprovalSummaryProvider
{
    public abstract string DocumentType { get; }

    private readonly IStringLocalizer<ProjectZSharedResource> _l;
    private readonly ICurrencyService _currency;

    protected SupplierDocumentApprovalSummaryBase(
        IStringLocalizer<ProjectZSharedResource> localizer, ICurrencyService currency)
    {
        _l = localizer;
        _currency = currency;
    }

    /// <summary>取表頭。查不到回 null（已刪除，或被全域廠別／資料權限過濾擋掉）。</summary>
    protected abstract Task<SupplierDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct);

    /// <summary>取明細，已依行號排序。</summary>
    protected abstract Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct);

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        var head = await LoadHeaderAsync(db, documentId, ct);
        if (head == null) return null;

        var lines = await LoadLinesAsync(db, documentId, ct);

        var supplierName = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == head.SupplierCode)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["SupplierCode"], supplierName is null
                ? head.SupplierCode
                : $"{head.SupplierCode} — {supplierName}"),
        };
        if (!string.IsNullOrWhiteSpace(head.Remarks))
            rows.Add(new(_l["Remarks"], head.Remarks));

        return new ApprovalSummary(
            DocumentId: head.DocumentId,
            PlantId: head.PlantId,
            Counterparty: supplierName ?? head.SupplierCode,
            AmountLocal: head.TotalAmount,
            CurrencyCode: await _currency.GetBaseCurrencyAsync(),
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines).ToList(),
            TotalLineCount: lines.Count);
    }

    /// <summary>兩張單共用的表頭投影。</summary>
    protected sealed record SupplierDocHeader(
        string DocumentId, string SupplierCode, string? PlantId, decimal TotalAmount, string? Remarks);
}

public sealed class GoodsReceiptApprovalSummary : SupplierDocumentApprovalSummaryBase
{
    public override string DocumentType => "GoodsReceipt";

    public GoodsReceiptApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<SupplierDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsReceipts.AsNoTracking()
            .Where(g => g.GoodsReceiptId == documentId)
            .Select(g => new SupplierDocHeader(
                g.GoodsReceiptId, g.SupplierCode, g.PlantId, g.TotalAmount, g.Remarks))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsReceiptLines.AsNoTracking()
            .Where(l => l.GoodsReceiptId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.ReceiptQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}

public sealed class GoodsReturnApprovalSummary : SupplierDocumentApprovalSummaryBase
{
    public override string DocumentType => "GoodsReturn";

    public GoodsReturnApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<SupplierDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsReturns.AsNoTracking()
            .Where(g => g.GoodsReturnId == documentId)
            .Select(g => new SupplierDocHeader(
                g.GoodsReturnId, g.SupplierCode, g.PlantId, g.TotalAmount, g.Remarks))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsReturnLines.AsNoTracking()
            .Where(l => l.GoodsReturnId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.ReturnQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}


/// <summary>
/// 委外收貨的簽核摘要。結構與進貨單完全相同，所以沿用同一個基底。
///
/// <para>⚠️ <b>金額仍是表頭的 <c>TotalAmount</c>（本幣）</b>。委外發票有「超耗扣款以負項
/// 扣減」的規則，但那發生在**發票**上不是收貨單上；這裡不做任何加工，
/// 摘要顯示的就是這張收貨單自己的合計。</para>
/// </summary>
public sealed class OutsourceReceiptApprovalSummary : SupplierDocumentApprovalSummaryBase
{
    public override string DocumentType => "OutsourceReceipt";

    public OutsourceReceiptApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c)
        : base(l, c) { }

    protected override async Task<SupplierDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.OutsourceReceipts.AsNoTracking()
            .Where(o => o.OutsourceReceiptId == documentId)
            .Select(o => new SupplierDocHeader(
                o.OutsourceReceiptId, o.SupplierCode, o.PlantId, o.TotalAmount, o.Remarks))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.OutsourceReceiptLines.AsNoTracking()
            .Where(l => l.OutsourceReceiptId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.ReceiptQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}


/// <summary>
/// 供應商評鑑的簽核摘要。
///
/// <para>⚠️ <b>這一張沒有金額，內容是分數。</b>評鑑要簽核人判斷的是「這個評分結果
/// 該不該成立」——尤其 <c>Fail</c> 會把供應商資格改成 Suspended（§PUR Phase G+，
/// 核准才回寫資格狀態）。把 <c>TotalScore</c> 塞進 <c>AmountLocal</c> 是**錯的**：
/// 那個欄位在畫面上會被格式化成金額（帶幣別、千分位），而 78 分不是 78 元。
/// 所以 <c>AmountLocal</c> 回 <c>null</c>，分數走 <c>Rows</c>。</para>
///
/// <para>⚠️ <b><c>documentId</c> 是 <c>VendorEvaluation.Id</c>（int）字串化</b>
/// ——與 <c>VendorEvaluationApprovalSync</c> 同一個約定。parse 不成功時回 <c>null</c>
/// （＝畫面顯示「這張單已不存在」），**不可以讓它拋例外**：那會讓整支摘要 API 回 500，
/// 而症狀看起來像手機簽核整個壞掉。</para>
///
/// <para><b>沒有明細。</b>評鑑是單一張評分卡，四個構面已經在 Rows 裡；
/// 硬湊一組明細只會讓畫面多一個空區塊。</para>
/// </summary>
public sealed class VendorEvaluationApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "VendorEvaluation";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;

    public VendorEvaluationApprovalSummary(IStringLocalizer<ProjectZSharedResource> localizer) => _l = localizer;

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        if (!int.TryParse(documentId, out var id)) return null;

        var ev = await db.Set<Models.VendorEvaluation>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev == null) return null;

        var supplierName = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == ev.SupplierCode)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);

        // 期間：年度，季別有值才加註（年度評鑑的 EvaluationQuarter 是 null）。
        var period = ev.EvaluationQuarter is int q
            ? $"{ev.EvaluationYear} Q{q}"
            : ev.EvaluationYear.ToString();

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["SupplierCode"], supplierName is null
                ? ev.SupplierCode
                : $"{ev.SupplierCode} — {supplierName}"),
            new(_l["ColPeriod"],   period),
            new(_l["ColQuality"],  ev.QualityScore.ToString("0.##")),
            new(_l["ColDelivery"], ev.DeliveryScore.ToString("0.##")),
            new(_l["ColPrice"],    ev.PriceScore.ToString("0.##")),
            new(_l["ColService"],  ev.ServiceScore.ToString("0.##")),
            new(_l["ColTotal"],    ev.TotalScore.ToString("0.##")),
            new(_l["ColResult"],   _l[ResultKey(ev.ResultStatus)]),
        };

        // Fail 一定要顯示改善措施——那是簽核人判斷「這個不合格結論成不成立」的關鍵，
        // 而它在 Fail 時是必填欄位。
        if (!string.IsNullOrWhiteSpace(ev.FollowUpAction))
            rows.Add(new(_l["FollowUpAction"], ev.FollowUpAction));
        if (!string.IsNullOrWhiteSpace(ev.Remarks))
            rows.Add(new(_l["Remarks"], ev.Remarks));

        return new ApprovalSummary(
            DocumentId: documentId,
            // ⚠️ 評鑑是**全域主檔，刻意不是 IPlantEntity**（實體註解：「供應商評核全公司共用」），
            //    所以沒有 PlantId 可給。這裡的 null 是「這張單沒有廠別概念」，不是漏填——
            //    畫面上那一列會整個不顯示，而那是對的。
            PlantId: null,
            Counterparty: supplierName ?? ev.SupplierCode,
            AmountLocal: null,     // 見類別註解：分數不是金額
            CurrencyCode: null,
            Rows: rows,
            Lines: Array.Empty<ApprovalSummaryLine>(),
            TotalLineCount: 0);
    }

    private static string ResultKey(Models.EvaluationResult r) => r switch
    {
        Models.EvaluationResult.Pass        => "EvalResultPass",
        Models.EvaluationResult.Conditional => "EvalResultConditional",
        _                                   => "EvalResultFail",
    };
}
