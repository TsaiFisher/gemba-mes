using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 發退料單類型（7 大類，涵蓋廠內/委外/重工三條路徑）。
    /// DocNo 前綴：MI/MR/MX/OI/OR/RI/RX
    /// </summary>
    public enum MaterialSlipCategory
    {
        InternalIssue       = 0,  // MI — 廠內發料（Push，工單釋出後系統自動建立）
        InternalRequisition = 1,  // MR — 廠內領料（Pull，生產單位按需申請，需審核）
        InternalReturn      = 2,  // MX — 廠內退料（超領退倉）
        OutsourceIssue      = 3,  // OI — 委外發料（揀料送廠）
        OutsourceReturn     = 4,  // OR — 委外退料（廠商退回用料）
        ReworkIssue         = 5,  // RI — 重工發料（廠內或委外重工均適用）
        ReworkReturn        = 6   // RX — 重工退料
    }

    /// <summary>發退料單狀態流程</summary>
    public enum MaterialSlipStatus
    {
        Draft     = 0,  // 草稿（可修改）
        Pending   = 1,  // 待審核（僅 InternalRequisition 走審核流程）
        Approved  = 2,  // 已核准
        Picking   = 3,  // 揀料中（倉庫已接單）
        Confirmed = 4,  // 倉庫確認揀料完成
        Posted    = 5,  // 庫存已過帳（StockMovement 已建立）
        Closed    = 6,  // 結案
        Cancelled = 7   // 作廢
    }

    /// <summary>
    /// 統一發退料單主檔（mfg-domain.md §17.3）。
    /// 取代原有 MaterialIssueSlip / MaterialRequisition / OutsourceMaterialDoc / ReworkMaterialDoc
    /// 四個分散實體，以 Category 欄位區分業務路徑，共用同一倉庫作業流程。
    /// </summary>
    public class MaterialSlip : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string SlipId { get; set; } = string.Empty;

        /// <summary>採番單號（依 Category DocType 產生前綴）</summary>
        [Required, MaxLength(30)]
        public string SlipNo { get; set; } = string.Empty;

        public MaterialSlipCategory Category { get; set; }
        public MaterialSlipStatus Status { get; set; } = MaterialSlipStatus.Draft;

        public DateOnly SlipDate { get; set; }

        /// <summary>來源單據類型："WorkOrder" | "OutsourcePurchaseOrder"</summary>
        [Required, MaxLength(30)]
        public string SourceDocType { get; set; } = string.Empty;

        /// <summary>來源單據 ID（WorkOrderId 或 OutsourcePurchaseOrderId）</summary>
        [Required, MaxLength(40)]
        public string SourceDocId { get; set; } = string.Empty;

        /// <summary>委外/重工委外時的目標廠商代號</summary>
        [MaxLength(20)]
        public string? TargetSupplierCode { get; set; }

        [MaxLength(450)]
        public string? RequestedBy { get; set; }

        public DateTime? RequestedAt { get; set; }

        [MaxLength(450)]
        public string? ApprovedBy { get; set; }

        public DateTime? ApprovedAt { get; set; }

        [MaxLength(450)]
        public string? IssuedBy { get; set; }

        public DateTime? IssuedAt { get; set; }

        [MaxLength(500)]
        public string? Remarks { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// 防發退料過帳 Issue 並發雙重過帳：兩並發都讀到 Confirmed → 各自過帳一次（庫存扣兩次），
        /// 收尾 UPDATE 帶 WHERE xmin → 後到者拋 DbUpdateConcurrencyException → 整段交易回滾。
        /// </summary>
        public uint xmin { get; set; }

        public IList<MaterialSlipLine> Lines { get; set; } = new List<MaterialSlipLine>();
    }

    /// <summary>統一發退料單明細</summary>
    public class MaterialSlipLine
    {
        [Required, MaxLength(40)]
        public string SlipId { get; set; } = string.Empty;

        public int LineNo { get; set; }

        /// <summary>來源明細 ID（WorkOrderMaterial.BomSeq 等，依 SourceDocType 而異）</summary>
        [MaxLength(40)]
        public string? SourceLineId { get; set; }

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>申請/需求量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal RequestedQty { get; set; }

        /// <summary>核准量（InternalRequisition 審核後填入）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal? ApprovedQty { get; set; }

        /// <summary>倉庫實際確認發出量</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ConfirmedQty { get; set; }

        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        /// <summary>發料來源倉庫</summary>
        [MaxLength(20)]
        public string? FromWarehouseCode { get; set; }

        /// <summary>發料來源儲位</summary>
        [MaxLength(20)]
        public string? FromLocationCode { get; set; }

        /// <summary>目標倉庫（退料入庫或委外進料上架時使用）</summary>
        [MaxLength(20)]
        public string? ToWarehouseCode { get; set; }

        /// <summary>目標儲位</summary>
        [MaxLength(20)]
        public string? ToLocationCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [MaxLength(50)]
        public string? SerialNo { get; set; }

        /// <summary>已退料量（退料時累加）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal ReturnedQty { get; set; } = 0M;

        [MaxLength(500)]
        public string? Remarks { get; set; }

        [ForeignKey(nameof(SlipId))]
        public MaterialSlip? Slip { get; set; }
    }
}

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 發退料單類別的流向判定（單一來源：過帳、桌機表單、手機作業共用）。
    /// 發料類從「來源倉/儲位」扣庫存；退料類把料退「回」目的倉/儲位。
    /// </summary>
    public static class MaterialSlipFlow
    {
        public static bool IsReturn(MaterialSlipCategory c) =>
            c is MaterialSlipCategory.InternalReturn
              or MaterialSlipCategory.OutsourceReturn
              or MaterialSlipCategory.ReworkReturn;

        /// <summary>
        /// 工單核准（投產）時自動開的 Push 發料單類別與單號設定：重工工單開重工發料（RI，過帳記 REWORK_MATERIAL，
        /// mfg-domain.md §17），其餘開廠內發料（MI）。原本一律 MI，重工用料混進正常製造的發料、成本無從分開統計。
        /// </summary>
        public static (MaterialSlipCategory Category, string DocType) PushIssueFor(WorkOrderType type) =>
            type == WorkOrderType.Rework
                ? (MaterialSlipCategory.ReworkIssue,   "MaterialSlip_RI")
                : (MaterialSlipCategory.InternalIssue, "MaterialSlip_MI");
    }
}
