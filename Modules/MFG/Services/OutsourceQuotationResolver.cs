using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG.Services;

/// <summary>
/// 委外報價的 4-level 特殊度查價（<c>OutsourceQuotation</c> 類別註解／pur-domain.md）。
///
/// <para><b>為什麼要有這個服務</b>：同一段邏輯原本有兩份，而且**兩份行為不同**。
/// 2026-09-22 的「先過濾再排序」只修了委外採購單自動產生那一支
/// （<c>OutsourcePurchaseOrderController</c>），畫面上自動帶入單價的
/// <c>OutsourceQuotationController.LookupPrice</c> 仍是舊的「只排序不過濾」——
/// 於是同一組（廠商、品號、途程、工序）在**畫面帶入**與**批次產生**會得到不同的單價，
/// 而兩個答案都不會報錯。收斂成單一來源之後，這件事不再靠記得同時改兩個地方。</para>
///
/// <para>做成服務而不是靜態方法，是為了讓 <c>OutsourcePriceTool</c> 這類新呼叫端
/// 能以注入取得它；<see cref="Resolve"/> 本身是純函式，不碰資料庫。</para>
/// </summary>
public interface IOutsourceQuotationResolver
{
    /// <summary>
    /// 載入某廠商在 <paramref name="asOf"/> 當日有效的全部報價，供 <see cref="Resolve"/> 逐筆挑選。
    ///
    /// <para>批次場景（一張委外單多行）請只呼叫一次，再對每一行呼叫 <see cref="Resolve"/>——
    /// 逐行查資料庫在明細多的單上是 N+1。</para>
    /// </summary>
    Task<IReadOnlyList<OutsourceQuotation>> LoadActiveAsync(
        string supplierCode, DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// 從候選報價中取最精確的一筆；沒有任何一筆適用時回 <c>null</c>。
    ///
    /// <para><b>兩段是分開的，不能只靠排序</b>：先剔除不適用的報價，再按特殊度取最精確的。
    /// 只用 <c>OrderByDescending</c> 的話，在「清單裡只剩不適用的那一筆」時
    /// （廠商只報過某幾個品號，現場最常見的狀態）<c>FirstOrDefault</c> 照樣回傳它
    /// ——委外單於是帶進一個別的零件、別的途程的加工費，而那是一個
    /// 看起來完全正常的金額，一路快照到收貨明細、加工費暫估與 AP。</para>
    ///
    /// <para>空字串與 <c>null</c> 一律視為「不限」：畫面沒填時 model binding 給的是 <c>""</c>。</para>
    /// </summary>
    OutsourceQuotation? Resolve(
        IEnumerable<OutsourceQuotation> candidates,
        string? productCode, string? routingId, int? operationSeq);

    /// <summary>
    /// 載入一批廠商在 <paramref name="from"/>～<paramref name="to"/> 期間內**任何一天**有效過的報價。
    ///
    /// <para>報表場景用：每一列收貨有自己的收貨日，逐列查資料庫是 N+1。呼叫端先用這個方法
    /// 一次撈回整段期間的候選，再對每一列用 <see cref="IsEffectiveOn"/> 篩出該日有效的，
    /// 最後交給 <see cref="Resolve"/>。日期有效性的判定因此**只有一份**，不會和
    /// <see cref="LoadActiveAsync"/> 各說各話。</para>
    /// </summary>
    Task<IReadOnlyList<OutsourceQuotation>> LoadEffectiveInRangeAsync(
        IReadOnlyCollection<string> supplierCodes, DateOnly from, DateOnly to,
        CancellationToken ct = default);

    /// <summary>某筆報價在 <paramref name="on"/> 當日是否有效。日期有效性的單一判定點。</summary>
    bool IsEffectiveOn(OutsourceQuotation quotation, DateOnly on);
}

/// <inheritdoc />
public class OutsourceQuotationResolver : IOutsourceQuotationResolver
{
    private readonly ApplicationDbContext _db;

    public OutsourceQuotationResolver(ApplicationDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutsourceQuotation>> LoadActiveAsync(
        string supplierCode, DateOnly asOf, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(supplierCode)) return [];

        return await _db.OutsourceQuotations.AsNoTracking()
            .Where(q => q.SupplierCode == supplierCode && q.IsActive
                     && q.EffectiveFrom <= asOf
                     && (q.EffectiveTo == null || q.EffectiveTo >= asOf))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public OutsourceQuotation? Resolve(
        IEnumerable<OutsourceQuotation> candidates,
        string? productCode, string? routingId, int? operationSeq)
        => candidates
            .Where(q => string.IsNullOrEmpty(q.ProductCode) || q.ProductCode == productCode)
            .Where(q => string.IsNullOrEmpty(q.RoutingId)   || q.RoutingId   == routingId)
            .Where(q => q.OperationSeq == null              || q.OperationSeq == operationSeq)
            .OrderByDescending(q => (!string.IsNullOrEmpty(q.ProductCode) ? 4 : 0)
                                  + (!string.IsNullOrEmpty(q.RoutingId)   ? 2 : 0)
                                  + (q.OperationSeq != null               ? 1 : 0))
            .FirstOrDefault();

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutsourceQuotation>> LoadEffectiveInRangeAsync(
        IReadOnlyCollection<string> supplierCodes, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        if (supplierCodes.Count == 0) return [];

        return await _db.OutsourceQuotations.AsNoTracking()
            .Where(q => supplierCodes.Contains(q.SupplierCode) && q.IsActive
                     && q.EffectiveFrom <= to
                     && (q.EffectiveTo == null || q.EffectiveTo >= from))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public bool IsEffectiveOn(OutsourceQuotation quotation, DateOnly on)
        => quotation.IsActive
        && quotation.EffectiveFrom <= on
        && (quotation.EffectiveTo == null || quotation.EffectiveTo >= on);
}
