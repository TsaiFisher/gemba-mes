using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.QCR.Services
{
    public interface IQcrReturnService
    {
        /// <summary>
        /// 由不合格品單（NCR，處置=Return）自動建立採購退貨草稿單（TypeCode=PF, Draft），回傳退貨單號。
        /// 追溯鏈：NCR.SourceId → InspectionOrder(IQC) → GoodsReceipt。僅適用 IQC 進料來源；
        /// 非 IQC 來源或找不到來源進貨單時擲 <see cref="InvalidOperationException"/>。
        /// 退貨單以草稿建立，後續金額/庫存沖銷由採購模組正常確認過帳。
        /// </summary>
        Task<string> CreateDraftReturnFromNcrAsync(
            NonConformanceReport ncr, string plantId, string userId, CancellationToken ct = default);
    }
}
