using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.ORD.Services;

/// <summary>
/// 訂單／出貨／銷貨退回的簽核摘要。
///
/// <para><b>三者共用同一份實作</b>：欄位結構完全一致（表頭 <c>CompanyCode</c>（客戶）
/// ＋本幣 <c>TotalAmount</c>、明細品號／數量／<c>AmountLocal</c>），差別只在數量欄位的名字
/// （<c>OrderQty</c> / <c>ShipQty</c> / <c>ReturnQty</c>）。
/// 抄成三份的下場是其中一份日後被改、其他沒有——而簽核人看到的差異不會有人發現，
/// 因為三張單本來就長得不一樣。</para>
///
/// <para>⚠️ <b>金額取表頭的 <c>TotalAmount</c>，它已經是本幣</b>（三個實體的註解逐字相同：
/// 「本位幣總金額（各明細 AmountLocal 加總）。明細可為不同幣別，故表頭一律以本幣合計」），
/// 所以這裡不做任何換算，幣別欄位給的是**本位幣代碼**——明細跨幣別時，
/// 拿其中一行的交易幣去標整張單的金額是錯的。</para>
///
/// <para>⚠️ <b>在地化用 <c>ProjectZSharedResource</c> 不是 Core 的 <c>SharedResource</c></b>：
/// ORD 模組的 Controller 一律注入前者（見 <c>SalesOrderController</c>），而
/// <c>CustomerCode</c> 這個鍵**只存在於 ProjectZSharedResource**。
/// 用錯來源不會編譯失敗、也不會擲例外——<c>IStringLocalizer</c> 查不到鍵時**回傳鍵名本身**，
/// 於是簽核人會在手機上看到一排 <c>CustomerCode</c> 這種英文代碼。</para>
///
/// <para>⚠️ <b><c>CompanyCode</c> 在這三張單上是「客戶」</b>，不是供應商。
/// <c>Companies</c> 是同一張表（以 <c>SupplierType</c> 區分 S／C），
/// 所以查名稱的寫法與 PUR 那邊一樣，但顯示標籤必須用 <c>CustomerCode</c>——
/// 對簽核人來說「供應商 A」與「客戶 A」是完全不同的一件事。</para>
/// </summary>
public abstract class CustomerDocumentApprovalSummaryBase : IApprovalSummaryProvider
{
    public abstract string DocumentType { get; }

    private readonly IStringLocalizer<ProjectZSharedResource> _l;
    private readonly ICurrencyService _currency;

    protected CustomerDocumentApprovalSummaryBase(
        IStringLocalizer<ProjectZSharedResource> localizer, ICurrencyService currency)
    {
        _l = localizer;
        _currency = currency;
    }

    /// <summary>取表頭。查不到回 null（已刪除，或被全域廠別／資料權限過濾擋掉）。</summary>
    protected abstract Task<CustomerDocHeader?> LoadHeaderAsync(
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

        var customerName = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == head.CompanyCode)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["CustomerCode"], customerName is null
                ? head.CompanyCode
                : $"{head.CompanyCode} — {customerName}"),
        };
        // 貿易條件擺在備註之前：簽核者要決定的正是「這些條件下誰付運費保險」，
        // 而它同時決定風險移轉時點＝收入認列時點。只有銷貨訂單帶值。
        if (!string.IsNullOrWhiteSpace(head.TradeTermCode))
            rows.Add(new(_l["TradeTerm"], head.TradeTermCode));
        if (!string.IsNullOrWhiteSpace(head.Remarks))
            rows.Add(new(_l["Remarks"], head.Remarks));

        return new ApprovalSummary(
            DocumentId: head.DocumentId,
            PlantId: head.PlantId,
            Counterparty: customerName ?? head.CompanyCode,
            AmountLocal: head.TotalAmount,
            CurrencyCode: await _currency.GetBaseCurrencyAsync(),
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines).ToList(),
            TotalLineCount: lines.Count);
    }

    /// <summary>
    /// 三張單共用的表頭投影。
    ///
    /// <para><c>TradeTermCode</c> **只有銷貨訂單會帶值**——貿易條件是訂單當下談定的契約條款，
    /// 出貨單與銷退單沿用訂單的條件、自己沒有這個欄位。給預設 null 而不是拆成兩個 record，
    /// 是因為另外兩張單傳 null 後摘要就不會出現這一列，行為自然正確。
    /// ⚠️ 刻意**不給預設值**：EF 的 expression tree 不接受選用參數（CS0854），
    /// 而必填也讓新增第四張單的人非得決定它有沒有貿易條件不可。</para>
    /// </summary>
    protected sealed record CustomerDocHeader(
        string DocumentId, string CompanyCode, string? PlantId, decimal TotalAmount, string? Remarks,
        string? TradeTermCode);
}

public sealed class SalesOrderApprovalSummary : CustomerDocumentApprovalSummaryBase
{
    public override string DocumentType => "SalesOrder";

    public SalesOrderApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<CustomerDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.SalesOrders.AsNoTracking()
            .Where(o => o.SalesOrderId == documentId)
            .Select(o => new CustomerDocHeader(
                o.SalesOrderId, o.CompanyCode, o.PlantId, o.TotalAmount, o.Remarks, o.TradeTermCode))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.SalesOrderLines.AsNoTracking()
            .Where(l => l.SalesOrderId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.OrderQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}

public sealed class GoodsShipmentApprovalSummary : CustomerDocumentApprovalSummaryBase
{
    public override string DocumentType => "GoodsShipment";

    public GoodsShipmentApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<CustomerDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsShipments.AsNoTracking()
            .Where(g => g.GoodsShipmentId == documentId)
            .Select(g => new CustomerDocHeader(
                g.GoodsShipmentId, g.CompanyCode, g.PlantId, g.TotalAmount, g.Remarks,
                // 出貨單沒有自己的貿易條件，沿用訂單的。明寫 null 是因為 EF 的
                // expression tree 不接受選用參數（CS0854）——而明寫反而看得出這是刻意的。
                null))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.GoodsShipmentLines.AsNoTracking()
            .Where(l => l.GoodsShipmentId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.ShipQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}

public sealed class SalesReturnApprovalSummary : CustomerDocumentApprovalSummaryBase
{
    public override string DocumentType => "SalesReturn";

    public SalesReturnApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<CustomerDocHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.SalesReturns.AsNoTracking()
            .Where(r => r.SalesReturnId == documentId)
            .Select(r => new CustomerDocHeader(
                r.SalesReturnId, r.CompanyCode, r.PlantId, r.TotalAmount, r.Remarks,
                // 銷退單沿用原訂單的貿易條件，自己沒有這個欄位（同出貨單）。
                null))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<ApprovalSummaryLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.SalesReturnLines.AsNoTracking()
            .Where(l => l.SalesReturnId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.ReturnQty, l.UomCode, l.AmountLocal))
            .ToListAsync(ct);
}

/// <summary>
/// 內部訂單的簽核摘要。
///
/// <para>⚠️ <b>刻意不繼承上面那個基底</b>：內部訂單**沒有客戶、也沒有任何金額欄位**
/// （<c>InternalOrder</c> 上沒有 <c>CompanyCode</c>、沒有 <c>TotalAmount</c>，
/// 明細只有數量沒有單價）。硬套基底就得在表頭投影裡塞兩個假欄位，
/// 而那會讓「這張單沒有金額」與「金額是 0」再度分不出來——
/// 那正是 <c>ApprovalSummary.AmountLocal</c> 用可空型別要表達的區別。</para>
/// </summary>
public sealed class InternalOrderApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "InternalOrder";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;

    public InternalOrderApprovalSummary(IStringLocalizer<ProjectZSharedResource> localizer) => _l = localizer;

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        var order = await db.InternalOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.InternalOrderId == documentId, ct);
        if (order == null) return null;

        var lines = await db.InternalOrderLines.AsNoTracking()
            .Where(l => l.InternalOrderId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new ApprovalSummaryLine(l.ProductCode, l.OrderQty, l.UomCode, null))
            .ToListAsync(ct);

        var rows = new List<ApprovalSummaryRow>();
        if (!string.IsNullOrWhiteSpace(order.DepartmentCode))
            rows.Add(new(_l["DepartmentCode"], order.DepartmentCode));
        if (!string.IsNullOrWhiteSpace(order.Remarks))
            rows.Add(new(_l["Remarks"], order.Remarks));

        return new ApprovalSummary(
            DocumentId: order.InternalOrderId,
            PlantId: order.PlantId,
            Counterparty: null,      // 內部單據，沒有往來對象
            AmountLocal: null,       // 見類別註解：沒有金額概念，不是 0
            CurrencyCode: null,
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines).ToList(),
            TotalLineCount: lines.Count);
    }
}
