using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.QCR.Services;

/// <summary>
/// 不合格報告（NCR）的簽核摘要。
///
/// <para>⚠️ <b>這一張沒有金額，判斷依據是「不良什麼、多少、怎麼處置」。</b>
/// NCR 上沒有任何金額欄位——處置的財務後果發生在**下游單據**上
/// （<c>ScrapDocId</c> 報廢單、<c>ReworkWorkOrderId</c> 重工工單、
/// <c>GoodsReturnId</c> 採購退貨），而那些單各自另有簽核。
/// 在這裡湊一個金額出來，等於把下游的決定提前塞進這一張。</para>
///
/// <para><b>處置方式（<c>Disposition</c>）是這張單最關鍵的一欄</b>：報廢、重工、
/// 退貨、特採的後果完全不同，而它決定了會產生哪一張下游單據。
/// 所以它排在根因與矯正措施之前——簽核人先看「要怎麼處理」，再看「為什麼」。</para>
///
/// <para><b>沒有明細。</b>NCR 是單一品項的不合格記錄（品號、數量、批號都在表頭），
/// 硬湊一組明細只會讓畫面多一個空區塊。</para>
/// </summary>
public sealed class NcrApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "NonConformanceReport";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;

    public NcrApprovalSummary(IStringLocalizer<ProjectZSharedResource> localizer) => _l = localizer;

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        var ncr = await db.NonConformanceReports.AsNoTracking()
            .FirstOrDefaultAsync(n => n.NcrId == documentId, ct);
        if (ncr == null) return null;

        var productName = await db.Products.AsNoTracking()
            .Where(p => p.ProductCode == ncr.ProductCode)
            .Select(p => p.ProductName)
            .FirstOrDefaultAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["ProductCode"], productName is null
                ? ncr.ProductCode
                : $"{ncr.ProductCode} — {productName}"),
            new(_l["Qty"], ncr.Qty.ToString("0.####")
                           + (string.IsNullOrWhiteSpace(ncr.UomCode) ? "" : " " + ncr.UomCode)),

            // 處置排在根因之前：簽核人先要知道「要怎麼處理」，那才是他在核准的東西。
            new(_l["Disposition"], ncr.Disposition),
        };

        if (!string.IsNullOrWhiteSpace(ncr.LotNo))
            rows.Add(new(_l["LotNo"], ncr.LotNo));
        if (!string.IsNullOrWhiteSpace(ncr.DefectCode))
            rows.Add(new(_l["DefectCode"], ncr.DefectCode));
        if (!string.IsNullOrWhiteSpace(ncr.RootCause))
            rows.Add(new(_l["RootCause"], ncr.RootCause));
        if (!string.IsNullOrWhiteSpace(ncr.CorrectiveAction))
            rows.Add(new(_l["CorrectiveAction"], ncr.CorrectiveAction));

        return new ApprovalSummary(
            DocumentId: ncr.NcrId,
            PlantId: ncr.PlantId,
            Counterparty: productName ?? ncr.ProductCode,  // NCR 沒有往來對象，放產品當識別
            AmountLocal: null,   // 見類別註解：沒有金額概念，不是 0
            CurrencyCode: null,
            Rows: rows,
            Lines: Array.Empty<ApprovalSummaryLine>(),
            TotalLineCount: 0);
    }
}
