using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.INV.Models
{
    /// <summary>預留來源類型（指定庫存綁定到哪種需求，mfg-domain.md §2 IQ / §10-6）</summary>
    public enum ReservationSourceType
    {
        MrpRunLine = 0,           // MRP 訂單需求列（成品層）
        WorkOrderMaterial = 1,    // 工單工序用料（半成品/原料層）
        Dispatch = 2,             // 派工（領料階段細鎖，預留供未來使用）
        MaterialStagingOrder = 3  // 備料單確認時鎖定（inventory.md §11-4）
    }

    /// <summary>預留狀態</summary>
    public enum ReservationStatus
    {
        Active = 0,    // 預留中，扣減可用量
        Released = 1,  // 已釋放（需求取消、MRP 重跑、提早投產重算）
        Consumed = 2   // 已實際領用（發料過帳後轉此）
    }

    /// <summary>
    /// 預留來源類別（mfg-domain.md §24.4 / §24.7）：區分「在手」與「供給端在途」兩類庫存來源。
    /// OnHand＝對既有在手庫存的軟/硬預留；Supply＝對已核准未結案 PO／委外在途量的 peg。
    /// </summary>
    public enum ReservationCategory
    {
        OnHand = 0,   // 在手庫存預留（既有行為，預設）
        Supply = 1    // 供給端在途 peg（PO／委外在途，投產確認在途時建立，到貨入庫時轉 OnHand／沖銷）
    }

    /// <summary>
    /// 庫存預留（指定庫存，舊系統 IQ）— 把現有庫存綁定到某個需求，
    /// 其他需求查詢可用庫存時扣除已預留量（mfg-domain.md §10-6「實體鎖定」）。
    ///
    /// 與既有 INV 機制的整合：本表為「預留明細帳」，廠級可用量
    /// = Σ Inventory.QtyOnHand − Σ Active 預留量。MRP 展開時於廠級（不綁儲位）建立軟預留；
    /// 真正綁儲位的硬預留（Inventory.QtyReserved）由 Phase D/F 發料過帳階段處理。
    /// </summary>
    public class InventoryReservation : IPlantEntity
    {
        public int Id { get; set; }

        public string? PlantId { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>預留量（庫存單位，對齊 Inventory.QtyOnHand 精度）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ReservedQty { get; set; }

        public ReservationSourceType SourceType { get; set; }

        // ── 供給端在途 peg（mfg-domain.md §24.4 / §24.8）──────────────────────────
        /// <summary>預留來源類別：OnHand（在手，預設）或 Supply（PO／委外在途 peg）。</summary>
        public ReservationCategory Category { get; set; } = ReservationCategory.OnHand;

        /// <summary>供給來源單據型別（Category=Supply 時填：PurchaseOrder / OutsourceWorkOrder）。</summary>
        [MaxLength(20)]
        public string? SupplyDocType { get; set; }

        /// <summary>供給來源單號（Category=Supply 時填：PurchaseOrderId / WorkOrderId）。</summary>
        [MaxLength(40)]
        public string? SupplyDocId { get; set; }

        /// <summary>供給來源明細行號（Category=Supply 時填：PurchaseOrderLine.LineNo）。</summary>
        public int? SupplyLineNo { get; set; }

        /// <summary>在途預計到貨日（Category=Supply 時填，取 PO／委外行 DeliveryDate；§24.4 為估計值）。</summary>
        public DateTime? ExpectedArrivalDate { get; set; }

        /// <summary>來源單號（MrpRunId / WorkOrderId / DispatchId）</summary>
        [Required, MaxLength(40)]
        public string SourceDocId { get; set; } = string.Empty;

        /// <summary>來源明細序（MrpRunLine.LineSeq 或 WorkOrderOperation.OperationSeq；無則 null）</summary>
        public int? SourceLineSeq { get; set; }

        /// <summary>需求時間點（用於 MRP 依時序判斷可用庫存）</summary>
        public DateTime RequiredDate { get; set; }

        public ReservationStatus Status { get; set; } = ReservationStatus.Active;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
