using Microsoft.Extensions.Localization;
using SmartFactory.MES.Modules.ACT.Models;

namespace SmartFactory.MES.Modules.ACT
{
    /// <summary>
    /// ACT（結帳／IFRS）列舉值與日記帳來源類型 → 使用者語系顯示字串。
    /// 查詢列下拉、清單徽章、唯讀表單一律透過本類別取字，避免直接輸出 enum 名稱（英文）。
    /// resx key 全部寫成字面量（不動態串接），維持可靜態檢查。
    /// 對應 key 置於 Resources/ProjectZSharedResource.{zh-TW,en-US}.resx。
    /// </summary>
    public static class ActEnumDisplay
    {
        public static string Text(IStringLocalizer l, PeriodStatus s) => s switch
        {
            PeriodStatus.Open     => l["PeriodStatusOpen"].Value,
            PeriodStatus.Closing  => l["PeriodStatusClosing"].Value,
            PeriodStatus.Closed   => l["PeriodStatusClosed"].Value,
            PeriodStatus.Reopened => l["PeriodStatusReopened"].Value,
            _                     => s.ToString()
        };

        public static string Text(IStringLocalizer l, JournalEntryType t) => t switch
        {
            JournalEntryType.Automatic  => l["JournalEntryTypeAutomatic"].Value,
            JournalEntryType.Manual     => l["JournalEntryTypeManual"].Value,
            JournalEntryType.Adjustment => l["JournalEntryTypeAdjustment"].Value,
            _                           => t.ToString()
        };

        public static string Text(IStringLocalizer l, ChecklistItemStatus s) => s switch
        {
            ChecklistItemStatus.Pending    => l["ChecklistItemStatusPending"].Value,
            ChecklistItemStatus.InProgress => l["ChecklistItemStatusInProgress"].Value,
            ChecklistItemStatus.Completed  => l["ChecklistItemStatusCompleted"].Value,
            ChecklistItemStatus.Skipped    => l["ChecklistItemStatusSkipped"].Value,
            _                              => s.ToString()
        };

        /// <summary>
        /// 日記帳來源類型（自由字串，由各服務於過帳時寫入）→ 顯示字串。
        /// 未列於下方對照的來源類型直接回傳原字串，確保新增來源不會顯示空白。
        /// </summary>
        public static string SourceDocTypeText(IStringLocalizer l, string? docType) => docType switch
        {
            "APInvoice"           => l["SourceDocTypeAPInvoice"].Value,
            "APInvoiceReturn"     => l["SourceDocTypeAPInvoiceReturn"].Value,
            "APPayment"           => l["SourceDocTypeAPPayment"].Value,
            "ARReceipt"           => l["SourceDocTypeARReceipt"].Value,
            "WorkOrderCompletion" => l["SourceDocTypeWorkOrderCompletion"].Value,
            "ActualRateReest"     => l["SourceDocTypeActualRateReest"].Value,
            "LandedCostAllocation" => l["SourceDocTypeLandedCostAllocation"].Value,
            "LandedCostAllocation-Void" => l["SourceDocTypeLandedCostAllocationVoid"].Value,
            "INV"                 => l["SourceDocTypeInv"].Value,
            "Manual"              => l["SourceDocTypeManual"].Value,
            "ManualReversal"      => l["SourceDocTypeManualReversal"].Value,
            _                     => docType ?? string.Empty
        };
    }
}
