using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.FIN.Services;

/// <summary>
/// 應付／應收發票的簽核摘要。
///
/// <para>🚨 <b>發票的金額語意與採購／訂單那六張<u>相反</u>，這是本檔存在的主要理由。</b></para>
///
/// <list type="bullet">
/// <item>採購單／訂單／進貨／出貨：表頭 <c>TotalAmount</c> 的註解是
///   「<b>本位幣</b>總金額（各明細 AmountLocal 加總）」——表頭已經是本幣。</item>
/// <item>發票：<c>TotalAmount</c> 是「含稅總額（<b>交易幣</b>）」，
///   <c>AmountLocal</c> 才是「本位幣金額（TotalAmount × ExchangeRate）」。</item>
/// </list>
///
/// <para>所以照抄前面那個基底（<c>AmountLocal: head.TotalAmount</c>）在這裡是**錯的**：
/// 一張美金發票會把交易幣金額當成台幣顯示，數字小一個數量級以上，
/// 而畫面上完全看不出來——簽核人只會覺得「這張怎麼這麼便宜」。
/// 這正是把六張單抽成共用基底時**不能連發票一起抄**的原因。</para>
///
/// <para>⚠️ <b>明細也是交易幣</b>（<c>APInvoiceLine.LineAmount</c> 沒有 <c>AmountLocal</c> 對應欄），
/// 所以這裡用表頭的 <c>ExchangeRate</c> 換算成本幣，讓明細與表頭同一個口徑——
/// 混用會讓明細加總對不上表頭，而那看起來像資料錯了。</para>
///
/// <para><b>交易幣金額另外列在 <c>Rows</c> 裡</b>：外幣發票的簽核人通常記得的是
/// 「這張是 USD 1,200」而不是換算後的本幣數字，只給本幣會讓他對不上手上的單據。</para>
/// </summary>
public abstract class InvoiceApprovalSummaryBase : IApprovalSummaryProvider
{
    public abstract string DocumentType { get; }

    private readonly IStringLocalizer<ProjectZSharedResource> _l;
    private readonly ICurrencyService _currency;

    protected InvoiceApprovalSummaryBase(
        IStringLocalizer<ProjectZSharedResource> localizer, ICurrencyService currency)
    {
        _l = localizer;
        _currency = currency;
    }

    protected abstract Task<InvoiceHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct);

    protected abstract Task<List<InvoiceLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct);

    /// <summary>往來對象的欄位標籤鍵：應付是供應商、應收是客戶。</summary>
    protected abstract string CounterpartyLabelKey { get; }

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        var head = await LoadHeaderAsync(db, documentId, ct);
        if (head == null) return null;

        var lines = await LoadLinesAsync(db, documentId, ct);

        var partyName = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == head.CompanyCode)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(ct);

        var baseCurrency = await _currency.GetBaseCurrencyAsync();

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l[CounterpartyLabelKey], partyName is null
                ? head.CompanyCode
                : $"{head.CompanyCode} — {partyName}"),
        };

        // 外幣發票才列交易幣金額。本幣發票列了只是同一個數字寫兩次。
        if (!string.IsNullOrWhiteSpace(head.CurrencyCode)
            && !string.Equals(head.CurrencyCode, baseCurrency, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new(_l["TotalAmount"], $"{head.CurrencyCode} {head.TotalAmount:N2}"));
        }

        if (!string.IsNullOrWhiteSpace(head.Remarks))
            rows.Add(new(_l["Remarks"], head.Remarks));

        return new ApprovalSummary(
            DocumentId: head.DocumentId,
            PlantId: head.PlantId,
            Counterparty: partyName ?? head.CompanyCode,
            // ⚠️ AmountLocal 不是 TotalAmount——見類別註解。
            AmountLocal: head.AmountLocal,
            CurrencyCode: baseCurrency,
            Rows: rows,
            Lines: lines.Take(ApprovalSummary.MaxLines)
                // 明細是交易幣，乘上表頭匯率才與表頭同一個口徑。
                .Select(l => new ApprovalSummaryLine(
                    l.Description, l.Qty, l.UomCode, l.LineAmount * head.ExchangeRate))
                .ToList(),
            TotalLineCount: lines.Count);
    }

    protected sealed record InvoiceHeader(
        string DocumentId, string CompanyCode, string? PlantId,
        decimal TotalAmount, decimal AmountLocal, decimal ExchangeRate,
        string? CurrencyCode, string? Remarks);

    protected sealed record InvoiceLine(string Description, decimal Qty, string? UomCode, decimal LineAmount);
}

public sealed class APInvoiceApprovalSummary : InvoiceApprovalSummaryBase
{
    public override string DocumentType => "APInvoice";
    protected override string CounterpartyLabelKey => "SupplierCode";

    public APInvoiceApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<InvoiceHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.APInvoices.AsNoTracking()
            .Where(i => i.APInvoiceId == documentId)
            .Select(i => new InvoiceHeader(
                i.APInvoiceId, i.SupplierCode, i.PlantId,
                i.TotalAmount, i.AmountLocal, i.ExchangeRate, i.CurrencyCode, i.Remarks))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<InvoiceLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.APInvoiceLines.AsNoTracking()
            .Where(l => l.APInvoiceId == documentId)
            .OrderBy(l => l.LineNo)
            // 手開發票的明細可以沒有品號（非採購性費用），那時退回行號當識別，
            // 不是留空——空字串在畫面上是一列看不出是什麼的資料。
            .Select(l => new InvoiceLine(
                l.ProductCode ?? ("#" + l.LineNo), l.Qty, l.UomCode, l.LineAmount))
            .ToListAsync(ct);
}

public sealed class ARInvoiceApprovalSummary : InvoiceApprovalSummaryBase
{
    public override string DocumentType => "ARInvoice";
    protected override string CounterpartyLabelKey => "CustomerCode";

    public ARInvoiceApprovalSummary(IStringLocalizer<ProjectZSharedResource> l, ICurrencyService c) : base(l, c) { }

    protected override async Task<InvoiceHeader?> LoadHeaderAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.ARInvoices.AsNoTracking()
            .Where(i => i.ARInvoiceId == documentId)
            .Select(i => new InvoiceHeader(
                i.ARInvoiceId, i.CustomerCode, i.PlantId,
                i.TotalAmount, i.AmountLocal, i.ExchangeRate, i.CurrencyCode, i.Remarks))
            .FirstOrDefaultAsync(ct);

    protected override async Task<List<InvoiceLine>> LoadLinesAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct) =>
        await db.ARInvoiceLines.AsNoTracking()
            .Where(l => l.ARInvoiceId == documentId)
            .OrderBy(l => l.LineNo)
            .Select(l => new InvoiceLine(
                l.ProductCode ?? ("#" + l.LineNo), l.Qty, l.UomCode, l.LineAmount))
            .ToListAsync(ct);
}
