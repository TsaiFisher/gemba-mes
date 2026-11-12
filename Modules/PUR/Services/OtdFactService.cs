using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.PUR.Services;

/// <summary>
/// OTD 事實來源（pur-domain.md §5.3/§6.2 共用口徑）。
///
/// <para><b>2026-09-21 口徑變更：母體從「已收到的貨」改成「這段期間應該交的單」。</b></para>
///
/// <para>舊版只看已過帳的收貨，於是「已經過了預交日、貨還沒來」的單**完全不在分母裡**——
/// 供應商拖得越久，分母越乾淨，達成率越好看。現場實際撞到的形狀是：採購逾期跟催表列出
/// 某廠商兩筆逾期，交期達成率卻顯示 100%。兩張表都沒算錯，是母體互斥：一張只看收到的，
/// 另一張只看沒收到的。</para>
///
/// <para>新母體以**預交日**落在查詢期間為準，每張單（行）只會出現一次，三種結果：</para>
/// <list type="bullet">
///   <item>已交清 → 以最後一次收貨日判斷準時與否</item>
///   <item>未交清且已過預交日 → <b>算遲交</b>，遲延天數以 <c>asOf</c> 計</item>
///   <item>未交清但還沒到期 → 不列入（尚無結果，列入會稀釋分母）</item>
/// </list>
///
/// <para>⚠️ 用收貨日當期間基準的話，「預交日在期間內、實際在期間後才收」的單會兩邊都
/// 落不到而靜默消失。改用預交日正是為了讓母體不重不漏。</para>
/// </summary>
public interface IOtdFactService
{
    /// <param name="plantId">非 null＝廠內報表（明確廠別過濾）；null＝全公司彙總（供應商評分卡，跨廠 IgnoreQueryFilters）</param>
    /// <param name="source">All / PO / OSP</param>
    /// <param name="asOf">「今天」，用來判斷未交的單是否已逾期並計算遲延天數。必須是廠別當地日（R22）。</param>
    Task<List<OtdFact>> BuildFactsAsync(
        DateOnly start, DateOnly end, string? supplierCode, string source, string? plantId, DateOnly asOf);
}

public class OtdFactService : IOtdFactService
{
    private readonly ApplicationDbContext _context;

    public OtdFactService(ApplicationDbContext context) => _context = context;

    public async Task<List<OtdFact>> BuildFactsAsync(
        DateOnly start, DateOnly end, string? supplierCode, string source, string? plantId, DateOnly asOf)
    {
        var facts = new List<OtdFact>();

        if (source is "All" or "PO")
            facts.AddRange(await BuildPoFactsAsync(start, end, supplierCode, plantId, asOf));

        if (source is "All" or "OSP")
            facts.AddRange(await BuildOspFactsAsync(start, end, supplierCode, plantId, asOf));

        return facts;
    }

    // ── 採購側：以採購明細行為粒度 ──────────────────────────────────────
    private async Task<List<OtdFact>> BuildPoFactsAsync(
        DateOnly start, DateOnly end, string? supplierCode, string? plantId, DateOnly asOf)
    {
        var lineQuery =
            from pol in _context.PurchaseOrderLines.IgnoreQueryFilters()
            join po in _context.PurchaseOrders.IgnoreQueryFilters()
                on pol.PurchaseOrderId equals po.PurchaseOrderId
            where (plantId == null || po.PlantId == plantId)
               && po.ApprovalStatus != DocumentApprovalStatus.Draft   // 對齊採購逾期跟催的母體
               && pol.DeliveryDate.HasValue
               && pol.DeliveryDate.Value >= start
               && pol.DeliveryDate.Value <= end
            select new
            {
                po.SupplierCode,
                pol.PurchaseOrderId,
                pol.LineNo,
                pol.OrderQty,
                pol.ReceivedQty,
                DueDate = pol.DeliveryDate!.Value,
            };

        if (!string.IsNullOrWhiteSpace(supplierCode))
            lineQuery = lineQuery.Where(r => r.SupplierCode == supplierCode);

        var lines = await lineQuery.AsNoTracking().ToListAsync();
        if (lines.Count == 0) return [];

        // 交清日＝該行最後一次有效收貨的日期。分批收貨時，準時與否要看**收齊**的那一天，
        // 只看第一次收貨會讓「首批準時、尾批拖三個月」被判成準時。
        var poIds = lines.Select(l => l.PurchaseOrderId).Distinct().ToList();
        var lastReceipt = (await (
                from grl in _context.GoodsReceiptLines.IgnoreQueryFilters()
                join gr in _context.GoodsReceipts.IgnoreQueryFilters()
                    on grl.GoodsReceiptId equals gr.GoodsReceiptId
                where gr.Status == DocStatus.Posted
                   && grl.PurchaseOrderId != null
                   && poIds.Contains(grl.PurchaseOrderId)
                select new { grl.PurchaseOrderId, grl.PoLineNo, gr.ReceiptDate })
                .AsNoTracking().ToListAsync())
            .Where(r => r.PoLineNo.HasValue)
            .GroupBy(r => (r.PurchaseOrderId!, r.PoLineNo!.Value))
            .ToDictionary(g => g.Key, g => g.Max(x => x.ReceiptDate));

        var facts = new List<OtdFact>();
        foreach (var l in lines)
        {
            var settled = l.ReceivedQty >= l.OrderQty;
            if (settled)
            {
                // 交清了卻找不到收貨紀錄（人工改量、資料補寫）：拿不出交貨日期就無從判斷準時，
                // 列入會是憑空的「準時」，故跳過——與「未到期」同樣屬於「尚無結果」。
                if (!lastReceipt.TryGetValue((l.PurchaseOrderId, l.LineNo), out var receiptDate)) continue;
                facts.Add(new OtdFact(l.SupplierCode, receiptDate, l.DueDate, asOf));
            }
            else if (l.DueDate < asOf)
            {
                facts.Add(new OtdFact(l.SupplierCode, null, l.DueDate, asOf));
            }
            // 未交清且還沒到期 → 尚無結果，不列入
        }
        return facts;
    }

    // ── 委外側：以委外採購單為粒度 ──────────────────────────────────────
    // 委外收貨沒有行級的已收量（OutsourceProgress 是靠「單號＋品號」彙總後依 LineNo 分攤），
    // 硬拆到行級只會把分攤誤差帶進達成率。單頭粒度對「這張委外單交了沒」已經足夠。
    private async Task<List<OtdFact>> BuildOspFactsAsync(
        DateOnly start, DateOnly end, string? supplierCode, string? plantId, DateOnly asOf)
    {
        var opoQuery =
            from opo in _context.OutsourcePurchaseOrders.IgnoreQueryFilters()
            where (plantId == null || opo.PlantId == plantId)
               && opo.Status != OutsourcePoStatus.Draft
               && opo.Status != OutsourcePoStatus.Void
               && opo.ExpectedDeliveryDate.HasValue
               && opo.ExpectedDeliveryDate.Value >= start
               && opo.ExpectedDeliveryDate.Value <= end
            select new
            {
                opo.OutsourcePurchaseOrderId,
                opo.SupplierCode,
                DueDate = opo.ExpectedDeliveryDate!.Value,
            };

        if (!string.IsNullOrWhiteSpace(supplierCode))
            opoQuery = opoQuery.Where(r => r.SupplierCode == supplierCode);

        var opos = await opoQuery.AsNoTracking().ToListAsync();
        if (opos.Count == 0) return [];

        var opoIds = opos.Select(o => o.OutsourcePurchaseOrderId).ToList();

        // 訂購量：拆單行以 SplitQty 為準（同 OutsourceProgress 的口徑）
        var orderedQty = (await _context.OutsourcePurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => opoIds.Contains(l.OutsourcePurchaseOrderId))
                .Select(l => new { l.OutsourcePurchaseOrderId, l.Qty, l.SplitQty })
                .AsNoTracking().ToListAsync())
            .GroupBy(l => l.OutsourcePurchaseOrderId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.SplitQty > 0 ? x.SplitQty : x.Qty));

        // 收貨量與最後收貨日。Confirmed 即廠商已交貨（對齊 OutsourceProgress／OutsourcePerformance）。
        var receipts = (await (
                from orl in _context.OutsourceReceiptLines.IgnoreQueryFilters()
                join or in _context.OutsourceReceipts.IgnoreQueryFilters()
                    on orl.OutsourceReceiptId equals or.OutsourceReceiptId
                where opoIds.Contains(or.OutsourcePurchaseOrderId)
                   && (or.Status == DocStatus.Confirmed || or.Status == DocStatus.Posted)
                select new { or.OutsourcePurchaseOrderId, orl.ReceiptQty, or.ReceiptDate })
                .AsNoTracking().ToListAsync())
            .GroupBy(r => r.OutsourcePurchaseOrderId)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.ReceiptQty), Last: g.Max(x => x.ReceiptDate)));

        var facts = new List<OtdFact>();
        foreach (var o in opos)
        {
            orderedQty.TryGetValue(o.OutsourcePurchaseOrderId, out var ordered);
            var hasReceipt = receipts.TryGetValue(o.OutsourcePurchaseOrderId, out var r);

            // 沒有明細行（ordered = 0）的委外單不成立，跳過而不是當成「零量已交清」。
            if (ordered <= 0) continue;

            if (hasReceipt && r.Qty >= ordered)
                facts.Add(new OtdFact(o.SupplierCode, r.Last, o.DueDate, asOf));
            else if (o.DueDate < asOf)
                facts.Add(new OtdFact(o.SupplierCode, null, o.DueDate, asOf));
        }
        return facts;
    }
}
