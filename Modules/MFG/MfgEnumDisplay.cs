using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.BAS.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules.MFG
{
    /// <summary>
    /// MFG 列舉 → 使用者語系顯示字串（共用的 DocStatus 走 _Shared.DocEnumDisplay）。
    /// 查詢列下拉、清單徽章、唯讀表單、列印一律透過本類別取字，避免直接輸出 enum 名稱（英文）。
    /// resx key 全部寫成字面量（不動態串接），維持可靜態檢查。
    /// 對應 key 置於 Resources/ProjectZSharedResource.{zh-TW,en-US}.resx，
    /// 用字與既有各畫面 resx（WorkOrder/Dispatch/MachineStatusOrder Index）一致。
    /// </summary>
    public static class MfgEnumDisplay
    {
        public static string Text(IStringLocalizer l, WorkOrderStatus s) => s switch
        {
            WorkOrderStatus.Planned    => l["WorkOrderStatusPlanned"].Value,
            WorkOrderStatus.Released   => l["WorkOrderStatusReleased"].Value,
            WorkOrderStatus.InProgress => l["WorkOrderStatusInProgress"].Value,
            WorkOrderStatus.Completed  => l["WorkOrderStatusCompleted"].Value,
            WorkOrderStatus.Closed     => l["WorkOrderStatusClosed"].Value,
            WorkOrderStatus.Void       => l["WorkOrderStatusVoid"].Value,
            _                          => s.ToString()
        };

        public static string Text(IStringLocalizer l, DispatchStatus s) => s switch
        {
            DispatchStatus.Pending         => l["DispatchStatusPending"].Value,
            DispatchStatus.Scheduled       => l["DispatchStatusScheduled"].Value,
            DispatchStatus.SetupInProgress => l["DispatchStatusSetupInProgress"].Value,
            DispatchStatus.ReadyToStart    => l["DispatchStatusReadyToStart"].Value,
            DispatchStatus.InProgress      => l["DispatchStatusInProgress"].Value,
            DispatchStatus.Closed          => l["DispatchStatusClosed"].Value,
            DispatchStatus.OnHold          => l["DispatchStatusOnHold"].Value,
            DispatchStatus.PendingFPI      => l["DispatchStatusPendingFPI"].Value,
            DispatchStatus.Cancelled       => l["DispatchStatusCancelled"].Value,
            _                              => s.ToString()
        };

        public static string Text(IStringLocalizer l, MaterialSlipStatus s) => s switch
        {
            MaterialSlipStatus.Draft     => l["MaterialSlipStatusDraft"].Value,
            MaterialSlipStatus.Pending   => l["MaterialSlipStatusPending"].Value,
            MaterialSlipStatus.Approved  => l["MaterialSlipStatusApproved"].Value,
            MaterialSlipStatus.Picking   => l["MaterialSlipStatusPicking"].Value,
            MaterialSlipStatus.Confirmed => l["MaterialSlipStatusConfirmed"].Value,
            MaterialSlipStatus.Posted    => l["MaterialSlipStatusPosted"].Value,
            MaterialSlipStatus.Closed    => l["MaterialSlipStatusClosed"].Value,
            MaterialSlipStatus.Cancelled => l["MaterialSlipStatusCancelled"].Value,
            _                            => s.ToString()
        };

        public static string Text(IStringLocalizer l, WorkOrderType t) => t switch
        {
            WorkOrderType.Normal    => l["WorkOrderTypeNormal"].Value,
            WorkOrderType.Rework    => l["WorkOrderTypeRework"].Value,
            WorkOrderType.Outsource => l["WorkOrderTypeOutsource"].Value,
            _                       => t.ToString()
        };

        public static string Text(IStringLocalizer l, CostStatus s) => s switch
        {
            CostStatus.Preliminary => l["CostStatusPreliminary"].Value,
            CostStatus.Final       => l["CostStatusFinal"].Value,
            _                      => s.ToString()
        };

        public static string Text(IStringLocalizer l, MachineStatus s) => s switch
        {
            MachineStatus.Available      => l["MachineStatusAvailable"].Value,
            MachineStatus.Standby        => l["MachineStatusStandby"].Value,
            MachineStatus.ExternalRepair => l["MachineStatusExternalRepair"].Value,
            MachineStatus.BreakDown      => l["MachineStatusBreakDown"].Value,
            MachineStatus.Modification   => l["MachineStatusModification"].Value,
            MachineStatus.Loaned         => l["MachineStatusLoaned"].Value,
            MachineStatus.Retired        => l["MachineStatusRetired"].Value,
            MachineStatus.Maintenance    => l["MachineStatusMaintenance"].Value,
            _                            => s.ToString()
        };

        /// <summary>機台異動事件：代碼為現場慣用語彙，故一律輸出「代碼 — 說明」。</summary>
        public static string Text(IStringLocalizer l, MachineEventType e) => $"{e} — " + e switch
        {
            MachineEventType.CI => l["MachineEventCI"].Value,
            MachineEventType.II => l["MachineEventII"].Value,
            MachineEventType.DI => l["MachineEventDI"].Value,
            MachineEventType.DO => l["MachineEventDO"].Value,
            MachineEventType.HI => l["MachineEventHI"].Value,
            MachineEventType.HO => l["MachineEventHO"].Value,
            MachineEventType.EO => l["MachineEventEO"].Value,
            MachineEventType.FO => l["MachineEventFO"].Value,
            MachineEventType.FI => l["MachineEventFI"].Value,
            MachineEventType.GO => l["MachineEventGO"].Value,
            MachineEventType.CO => l["MachineEventCO"].Value,
            _                   => e.ToString()
        };
    }
}
