using SmartFactory.MES.Models;
using SmartFactory.MES.Modules.INV.Models;
using SmartFactory.MES.Modules.MFG.Models;

namespace SmartFactory.MES.Modules._Shared
{
    // 四大單據（採購單／進貨單／工單／出貨單）的狀態守衛。
    //
    // 這些條件原本以 if 鏈散在各 controller 的 action 裡，與權限檢查、DB 讀取、
    // localizer 取字混在一起，於是只能靠整合測試碰得到，也無法窮舉。
    // 抽成純函式後：
    //   1. 可以用 [Theory] 把「狀態 × 旗標」整張矩陣掃完（見 Tests/StateMachines）；
    //   2. 同一份判斷成為唯一事實來源——controller 改了會直接把矩陣打紅，
    //      而不是像測試裡自己再抄一份條件那樣兩邊各自漂移。
    //
    // 刻意留在 controller 的部分：權限、找不到單據、以及需要查 DB 的事實
    // （已上架量、已退貨量、派工回報量……）。那些事實以 bool 參數傳進來，
    // 守衛本身完全不碰 DB，因此可以無 fixture 直接測。
    //
    // 檢查順序即訊息優先順序：回傳第一個不通過的原因，controller 再把原因映射到
    // 原有的 resx key，訊息與重構前逐字相同。

    // ── 採購單 ────────────────────────────────────────────────────────────────

    /// <summary>採購單守衛結果。<see cref="Allowed"/> 以外皆為拒絕原因。</summary>
    public enum PurchaseOrderGuard
    {
        Allowed = 0,
        /// <summary>審核中，不得修改</summary>
        PendingApproval,
        /// <summary>已有進貨記錄（ReceivedQty &gt; 0），不得刪除</summary>
        HasReceipts,
        /// <summary>尚未核准，不得結案</summary>
        NeedsApproval,
    }

    /// <summary>
    /// 採購單狀態機（PUR / pur-domain.md §3）。
    /// 採購單沒有 <see cref="DocStatus"/>，其生命週期完全由簽核狀態
    /// 加上明細層的 <c>IsClosed</c> / <c>ReceivedQty</c> 決定。
    /// </summary>
    public static class PurchaseOrderStateMachine
    {
        /// <summary>修改：審核中不得改內容（送審後改單等於改掉簽核人看過的東西）。</summary>
        public static PurchaseOrderGuard CanEdit(DocumentApprovalStatus approval)
            => approval == DocumentApprovalStatus.PendingApproval
                ? PurchaseOrderGuard.PendingApproval
                : PurchaseOrderGuard.Allowed;

        /// <summary>
        /// 刪除：只要有任何一行進過貨就不得刪除——進貨單的明細指回採購明細，
        /// 採購單先消失會讓進貨單的來源憑空不見。
        /// 審核中同樣不得刪除，否則簽核申請會指向一張已不存在的單。
        /// </summary>
        public static PurchaseOrderGuard CanDelete(DocumentApprovalStatus approval, bool hasReceipts)
        {
            if (hasReceipts) return PurchaseOrderGuard.HasReceipts;
            if (approval == DocumentApprovalStatus.PendingApproval) return PurchaseOrderGuard.PendingApproval;
            return PurchaseOrderGuard.Allowed;
        }

        /// <summary>結案：未核准的採購單沒有在途供給可言，不該結案。</summary>
        public static PurchaseOrderGuard CanClose(DocumentApprovalStatus approval)
            => approval == DocumentApprovalStatus.Approved
                ? PurchaseOrderGuard.Allowed
                : PurchaseOrderGuard.NeedsApproval;
    }

    // ── 進貨單 ────────────────────────────────────────────────────────────────

    /// <summary>進貨單守衛結果。</summary>
    public enum GoodsReceiptGuard
    {
        Allowed = 0,
        /// <summary>已上架（QtyPutAway &gt; 0），倉庫已經動過</summary>
        WarehouseStarted,
        /// <summary>已有退貨（ReturnedQty &gt; 0）</summary>
        HasReturns,
        /// <summary>非草稿</summary>
        NotDraft,
        /// <summary>審核中</summary>
        PendingApproval,
        /// <summary>尚未核准</summary>
        NeedsApproval,
        /// <summary>沒有明細</summary>
        NoLines,
        /// <summary>非已確認</summary>
        NotConfirmed,
        /// <summary>非已過帳</summary>
        NotPosted,
    }

    /// <summary>
    /// 進貨單狀態機（PUR / pur-domain.md §4）。
    /// 狀態流向：Draft →（確認）→ Confirmed →（上架）→ Posted →（作廢）→ Void。
    /// 簡化模式下確認一步到 Posted（不經 Confirmed）。
    /// </summary>
    public static class GoodsReceiptStateMachine
    {
        /// <summary>修改：審核中不得改內容。</summary>
        public static GoodsReceiptGuard CanEdit(DocumentApprovalStatus approval)
            => approval == DocumentApprovalStatus.PendingApproval
                ? GoodsReceiptGuard.PendingApproval
                : GoodsReceiptGuard.Allowed;

        /// <summary>
        /// 刪除。前兩項（倉庫已動、已退貨）先判，因為那兩個訊息比「僅草稿可刪除」
        /// 更能告訴使用者卡在哪裡；後兩項是狀態層的底線。
        ///
        /// 審核中不得刪除是必要的一格：單據刪掉之後簽核申請還留著，
        /// 簽核人點進去只會看到一張不存在的單。
        /// </summary>
        public static GoodsReceiptGuard CanDelete(
            DocStatus status, DocumentApprovalStatus approval, bool hasPutAway, bool hasReturns)
        {
            if (hasPutAway) return GoodsReceiptGuard.WarehouseStarted;
            if (hasReturns) return GoodsReceiptGuard.HasReturns;
            if (status != DocStatus.Draft) return GoodsReceiptGuard.NotDraft;
            if (approval == DocumentApprovalStatus.PendingApproval) return GoodsReceiptGuard.PendingApproval;
            return GoodsReceiptGuard.Allowed;
        }

        /// <summary>確認：Draft 且已核准且有明細。</summary>
        public static GoodsReceiptGuard CanConfirm(
            DocStatus status, DocumentApprovalStatus approval, bool hasLines)
        {
            if (status != DocStatus.Draft) return GoodsReceiptGuard.NotDraft;
            if (approval != DocumentApprovalStatus.Approved) return GoodsReceiptGuard.NeedsApproval;
            if (!hasLines) return GoodsReceiptGuard.NoLines;
            return GoodsReceiptGuard.Allowed;
        }

        /// <summary>取消確認：僅 Confirmed 可退回草稿，且倉庫尚未開始上架。</summary>
        public static GoodsReceiptGuard CanCancelConfirm(DocStatus status, bool hasPutAway)
        {
            if (status != DocStatus.Confirmed) return GoodsReceiptGuard.NotConfirmed;
            if (hasPutAway) return GoodsReceiptGuard.WarehouseStarted;
            return GoodsReceiptGuard.Allowed;
        }

        /// <summary>作廢：僅已過帳可作廢（作廢＝以反向異動沖銷，沒過帳就沒有東西可沖）。</summary>
        public static GoodsReceiptGuard CanVoid(DocStatus status)
            => status == DocStatus.Posted ? GoodsReceiptGuard.Allowed : GoodsReceiptGuard.NotPosted;
    }

    // ── 工單 ──────────────────────────────────────────────────────────────────

    /// <summary>工單守衛結果。</summary>
    public enum WorkOrderGuard
    {
        Allowed = 0,
        /// <summary>非已規劃，不可釋出</summary>
        NotPlanned,
        /// <summary>非審核中，無從撤回</summary>
        NotPending,
        /// <summary>已結案或已作廢，不可再作廢</summary>
        CannotVoid,
        /// <summary>已有派工回報（QtyDone &gt; 0）</summary>
        HasProduction,
        /// <summary>有已領未退的料（IssuedQty − ReturnedQty &gt; 0）</summary>
        HasIssuedMaterial,
    }

    /// <summary>
    /// 工單狀態機（MFG / mfg-domain.md §4.2）。
    /// 狀態流向：Planned →（釋出）→ Released →（開工）→ InProgress →
    ///           （完工）→ Completed →（入庫）→ Closed；任一未開工階段可 Void。
    /// </summary>
    public static class WorkOrderStateMachine
    {
        /// <summary>釋出：僅 Planned。已釋出者重複釋出會重排產能、重算需求。</summary>
        public static WorkOrderGuard CanRelease(WorkOrderStatus status)
            => status == WorkOrderStatus.Planned ? WorkOrderGuard.Allowed : WorkOrderGuard.NotPlanned;

        /// <summary>撤回：僅審核中可撤回（撤回後退回 Planned 可修正再送審）。</summary>
        public static WorkOrderGuard CanWithdraw(DocumentApprovalStatus approval)
            => approval == DocumentApprovalStatus.PendingApproval
                ? WorkOrderGuard.Allowed
                : WorkOrderGuard.NotPending;

        /// <summary>
        /// 作廢：Closed / Void 不可再作廢；已回報產出不可作廢；
        /// 已領未退的料同樣擋住——料已離開倉庫而作廢後沒有任何單據會來承接它，
        /// 生產領料不入總帳，帳面完全安靜，只有倉庫少了東西且查無去向。
        /// </summary>
        public static WorkOrderGuard CanVoid(
            WorkOrderStatus status, bool hasProduction, bool hasIssuedMaterial)
        {
            if (status is WorkOrderStatus.Closed or WorkOrderStatus.Void) return WorkOrderGuard.CannotVoid;
            if (hasProduction) return WorkOrderGuard.HasProduction;
            if (hasIssuedMaterial) return WorkOrderGuard.HasIssuedMaterial;
            return WorkOrderGuard.Allowed;
        }
    }

    // ── 出貨單 ────────────────────────────────────────────────────────────────

    /// <summary>出貨單守衛結果。</summary>
    public enum GoodsShipmentGuard
    {
        Allowed = 0,
        /// <summary>已過帳，不得重複過帳</summary>
        AlreadyPosted,
        /// <summary>已作廢</summary>
        AlreadyVoided,
        /// <summary>尚未核准</summary>
        NeedsApproval,
        /// <summary>沒有明細</summary>
        NoLines,
        /// <summary>非已確認</summary>
        NotConfirmed,
        /// <summary>已開始揀料（QtyPutAway &gt; 0）</summary>
        HasPicking,
        /// <summary>非已過帳</summary>
        NotPosted,
        /// <summary>非草稿</summary>
        NotDraft,
        /// <summary>審核中</summary>
        PendingApproval,
    }

    /// <summary>
    /// 出貨單狀態機（ORD / ord-module.md §5）。
    /// 狀態流向與進貨單對稱：Draft →（確認）→ Confirmed →（揀料）→ Posted →（作廢）→ Void。
    /// </summary>
    public static class GoodsShipmentStateMachine
    {
        /// <summary>修改：審核中不得改內容。</summary>
        public static GoodsShipmentGuard CanEdit(DocumentApprovalStatus approval)
            => approval == DocumentApprovalStatus.PendingApproval
                ? GoodsShipmentGuard.PendingApproval
                : GoodsShipmentGuard.Allowed;

        /// <summary>
        /// 確認。與進貨單刻意不同的一格：出貨單允許在 Confirmed 再按一次確認
        /// （用於「先確認、後來才打開簡化模式」的部署——此時第二次確認才會過帳）。
        /// 重複過帳由 <c>InventoryPostingService</c> 的逐行冪等性檢查擋住，
        /// 不是靠這裡的狀態判斷。
        /// </summary>
        public static GoodsShipmentGuard CanConfirm(
            DocStatus status, DocumentApprovalStatus approval, bool hasLines)
        {
            if (status == DocStatus.Posted) return GoodsShipmentGuard.AlreadyPosted;
            if (status == DocStatus.Void) return GoodsShipmentGuard.AlreadyVoided;
            if (approval != DocumentApprovalStatus.Approved) return GoodsShipmentGuard.NeedsApproval;
            if (!hasLines) return GoodsShipmentGuard.NoLines;
            return GoodsShipmentGuard.Allowed;
        }

        /// <summary>取消確認：僅 Confirmed，且揀料尚未開始。</summary>
        public static GoodsShipmentGuard CanCancelConfirm(DocStatus status, bool hasPicking)
        {
            if (status != DocStatus.Confirmed) return GoodsShipmentGuard.NotConfirmed;
            if (hasPicking) return GoodsShipmentGuard.HasPicking;
            return GoodsShipmentGuard.Allowed;
        }

        /// <summary>作廢：僅已過帳可作廢。</summary>
        public static GoodsShipmentGuard CanVoid(DocStatus status)
            => status == DocStatus.Posted ? GoodsShipmentGuard.Allowed : GoodsShipmentGuard.NotPosted;

        /// <summary>刪除：僅草稿，且非審核中。</summary>
        public static GoodsShipmentGuard CanDelete(DocStatus status, DocumentApprovalStatus approval)
        {
            if (status != DocStatus.Draft) return GoodsShipmentGuard.NotDraft;
            if (approval == DocumentApprovalStatus.PendingApproval) return GoodsShipmentGuard.PendingApproval;
            return GoodsShipmentGuard.Allowed;
        }
    }
}
