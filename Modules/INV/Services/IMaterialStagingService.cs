using SmartFactory.MES.Modules.INV.Models;

namespace SmartFactory.MES.Modules.INV.Services
{
    /// <summary>
    /// 備料單業務服務 — 確認時鎖庫存、作廢時釋放預留（inventory.md §11-4）。
    /// </summary>
    public interface IMaterialStagingService
    {
        /// <summary>
        /// 確認備料單：對每一明細建立 InventoryReservation，可用量不足則拋例外（整批回滾）。
        /// 成功後 Status → Confirmed。
        /// </summary>
        Task ConfirmAsync(MaterialStagingOrder order, string confirmedBy, CancellationToken ct = default);

        /// <summary>
        /// 揀料完成：把明細料件由來源儲位實際移入備料倉／備料儲位（同廠調撥 WP/WI 一對異動），
        /// 回寫 PickedQty，Status Confirmed → Completed。
        /// <para>
        /// ⚠️ 預留**不在此釋放**：料只是換了儲位、仍在廠內未投入生產，那批鎖量要留到領料過帳
        /// （<c>ConsumeForWorkOrderAsync</c>）才等額退場，否則備料完成到領料之間會出現空窗，
        /// 別的工單可以把已備妥的料搶走。
        /// </para>
        /// </summary>
        Task StageAsync(MaterialStagingOrder order, string stagedBy, CancellationToken ct = default);

        /// <summary>
        /// 作廢備料單：釋放所有 Active InventoryReservation，Status → Void。
        /// 僅允許 Draft 或 Confirmed 狀態作廢。
        /// </summary>
        Task VoidAsync(MaterialStagingOrder order, CancellationToken ct = default);
    }
}
