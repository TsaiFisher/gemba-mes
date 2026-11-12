using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules._Shared;
using SmartFactory.MES.Services;

namespace SmartFactory.MES.Modules.MFG.Services;

/// <summary>
/// 工單的簽核摘要。
///
/// <para><b>金額用「標準料＋工＋費」，因為那正是決定要不要送審的那個數字。</b>
/// <c>WorkOrderReleaseService</c> 投產時算出 <c>(mat, lab, ovh)</c>，
/// 把 <c>mat + lab + ovh</c> 拿去和 <c>mfg_wo_approval_threshold</c> 比，
/// 超過門檻才送審（§18.2 快速通道）。摘要顯示別的數字——例如只顯示料成本、
/// 或顯示實際成本——會讓簽核人看到的金額**與這張單為什麼會出現在他清單裡**對不起來，
/// 而那種不一致沒有任何跡象可循。</para>
///
/// <para>⚠️ <b>刻意用實體上已落地的 <c>Standard*Cost</c> 三欄，不重算。</b>
/// 那三欄是投產當下寫進去的**成本快照**；重算會用到當下的工作中心費率，
/// 而費率是會變的——簽核人明天看到的金額就會與昨天送審時不同。</para>
///
/// <para>⚠️ <b>幣別給本位幣。</b>標準成本本來就是以本幣計算的（費率與料件標準成本都是），
/// 沒有交易幣的概念。</para>
///
/// <para><b>明細是投料清單而不是工序</b>：簽核人要判斷的是「這張單要投多少料、值多少錢」。
/// 工序資訊（站別、工時）對「準不准做」這個決定幫助不大，而且會把摘要撐爆——
/// 一張工單的工序數與投料項數是兩個量級。要看完整內容就開桌機連結。</para>
/// </summary>
public sealed class WorkOrderApprovalSummary : IApprovalSummaryProvider
{
    public string DocumentType => "WorkOrder";

    private readonly IStringLocalizer<ProjectZSharedResource> _l;
    private readonly ICurrencyService _currency;

    public WorkOrderApprovalSummary(
        IStringLocalizer<ProjectZSharedResource> localizer, ICurrencyService currency)
    {
        _l = localizer;
        _currency = currency;
    }

    public async Task<ApprovalSummary?> GetAsync(
        ApplicationDbContext db, string documentId, CancellationToken ct = default)
    {
        // 不加 IgnoreQueryFilters：查不到就是查不到。全域廠別／資料權限過濾擋掉的單，
        // 簽核人本來就不該在手機上看到內容。
        var wo = await db.WorkOrders.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkOrderId == documentId, ct);
        if (wo == null) return null;

        var productName = await db.Products.AsNoTracking()
            .Where(p => p.ProductCode == wo.ProductCode)
            .Select(p => p.ProductName)
            .FirstOrDefaultAsync(ct);

        // WorkOrderMaterial 自己就帶 WorkOrderId 與 OperationSeq，不必繞 navigation——
        // 經由 m.Operation!.WorkOrderId 走會多一個 join，而且 InMemory 供應者
        // 對未載入的導覽屬性行為與 PG 不同，測試會給出誤導的結果。
        var materials = await db.WorkOrderMaterials.AsNoTracking()
            .Where(m => m.WorkOrderId == documentId)
            .OrderBy(m => m.OperationSeq).ThenBy(m => m.BomSeq)
            .Select(m => new { m.ProductCode, m.RequiredQty, m.UomCode, m.UnitCost })
            .ToListAsync(ct);

        var rows = new List<ApprovalSummaryRow>
        {
            new(_l["ProductCode"], productName is null
                ? wo.ProductCode
                : $"{wo.ProductCode} — {productName}"),
            new(_l["Qty"], wo.Quantity.ToString("0.####")),
        };

        return new ApprovalSummary(
            DocumentId: wo.WorkOrderId,
            PlantId: wo.PlantId,
            Counterparty: productName ?? wo.ProductCode,   // 工單沒有往來對象，這裡放產品當識別
            AmountLocal: wo.StandardMaterialCost + wo.StandardLaborCost + wo.StandardOverheadCost,
            CurrencyCode: await _currency.GetBaseCurrencyAsync(),
            Rows: rows,
            Lines: materials.Take(ApprovalSummary.MaxLines)
                // 明細金額＝需求量 × 標準單價。工單的料成本就是這樣加總出來的，
                // 所以明細與表頭的料成本是同一個口徑（表頭還多了工與費）。
                .Select(m => new ApprovalSummaryLine(
                    m.ProductCode, m.RequiredQty, m.UomCode, m.RequiredQty * m.UnitCost))
                .ToList(),
            TotalLineCount: materials.Count);
    }
}
