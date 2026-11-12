using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    /// <summary>
    /// 盤點清冊主檔（對應舊系統 IC / IC01）。
    /// WithSheet：電腦依範圍預先產生明細清冊；WithoutSheet：手機邊掃邊建。
    /// </summary>
    public class InventoryCountSheet : IPlantEntity, IDataAuthEntity
    {
        /// <summary>清冊單號（IC{YY}{MM}-{nnnnn}）</summary>
        [Key, MaxLength(30)]
        public string SheetId { get; set; } = string.Empty;

        public string? PlantId { get; set; }

        /// <summary>WithSheet（使用清冊）| WithoutSheet（不使用清冊）</summary>
        [Required, MaxLength(20)]
        public string CountMode { get; set; } = "WithSheet";

        // ── 盤點範圍（WithSheet 使用）────────────────────────────────────
        [MaxLength(20)] public string? WhCodeFrom   { get; set; }
        [MaxLength(20)] public string? WhCodeTo     { get; set; }
        [MaxLength(20)] public string? LocCodeFrom  { get; set; }
        [MaxLength(20)] public string? LocCodeTo    { get; set; }
        [MaxLength(30)] public string? ProdCodeFrom { get; set; }
        [MaxLength(30)] public string? ProdCodeTo   { get; set; }

        /// <summary>庫存凍結時間（快照時間點，建立清冊時記錄）</summary>
        public DateTime? SnapshotAt { get; set; }

        /// <summary>Counting | Closing | Closed | Void</summary>
        [Required, MaxLength(20)]
        public string SheetStatus { get; set; } = "Counting";

        [MaxLength(450)] public string? ClosedBy  { get; set; }
        public DateTime? ClosedAt { get; set; }

        /// <summary>
        /// 結帳時產生的盤盈虧調整單號（一張清冊終身只對一張）。
        ///
        /// 結帳的冪等性原本只靠 <c>SheetStatus != "Counting"</c> 這道行為守門：
        /// 一旦狀態被改回 Counting（人為改庫、或未來加「重開」功能），第二次結帳會
        /// **另採一個新單號**，而過帳層的冪等檢查是以「單號＋行號＋類別」為鍵的——
        /// 新單號等於換了一把鑰匙，那道防線完全攔不到，同批差異就被扣兩次庫存，
        /// 兩張調整單各自看起來都合法。
        ///
        /// 記下單號並在 <c>Close</c> 開頭擋掉，把冪等從「行為」變成「資料上的事實」；
        /// 搭配 (PlantId, AdjustmentDocId) 唯一索引，連繞過應用層都擋得住。
        /// </summary>
        [MaxLength(30)] public string? AdjustmentDocId { get; set; }

        [MaxLength(500)] public string? Remarks { get; set; }

        [Required, MaxLength(450)]
        public string CreatedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId      { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>樂觀並發控制（PostgreSQL 系統欄位 xmin，UseXminAsConcurrencyToken）。
        /// INV 單據表頭補 xmin（見 reference-concurrency-catch-audit）：確定性擋下同單並發過帳/盤點調整雙重套用。</summary>
        public uint xmin { get; set; }

        public ICollection<InventoryCountItem> Items { get; set; } = new List<InventoryCountItem>();
    }

    /// <summary>
    /// 盤點明細（對應舊系統 ICC_D）。
    /// 儲存初盤（FirstCount）與覆盤（Recount）兩層數量，職責分離由 Service 強制驗證。
    /// </summary>
    public class InventoryCountItem
    {
        [Key]
        public int ItemId { get; set; }

        [Required, MaxLength(30)]
        public string SheetId { get; set; } = string.Empty;

        /// <summary>清冊序號（供手機掃碼對應，WithSheet 時由系統產生）</summary>
        public int SeqNo { get; set; }

        // ── 位置與品號（快照時記錄）──────────────────────────────────────
        [MaxLength(20)] public string WhCode       { get; set; } = "";
        [MaxLength(20)] public string LocationCode { get; set; } = "";
        [MaxLength(50)] public string LotNo        { get; set; } = "";
        [MaxLength(30)] public string ProductCode  { get; set; } = "";

        /// <summary>帳面量（SnapshotAt 時點的 QtyOnHand）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal BookQty { get; set; }

        // ── 初盤（第一次盤點）────────────────────────────────────────────
        [Column(TypeName = "decimal(18,4)")]
        public decimal? FirstCountQty { get; set; }

        [MaxLength(450)] public string? FirstCountedBy { get; set; }
        public DateTime? FirstCountedAt { get; set; }

        // ── 覆盤（管理者抽驗，RecountedBy ≠ FirstCountedBy）────────────
        [Column(TypeName = "decimal(18,4)")]
        public decimal? RecountQty { get; set; }

        [MaxLength(450)] public string? RecountedBy { get; set; }
        public DateTime? RecountedAt { get; set; }

        // ── 計算屬性（不存 DB）──────────────────────────────────────────
        [NotMapped]
        public decimal? FinalQty => RecountQty ?? FirstCountQty;

        [NotMapped]
        public decimal? Variance => FinalQty.HasValue ? FinalQty.Value - BookQty : (decimal?)null;

        /// <summary>Pending | Counted | Recounted | Finalized</summary>
        [MaxLength(20)]
        public string ItemStatus { get; set; } = "Pending";

        /// <summary>差異說明（主管審核時填寫）</summary>
        [MaxLength(500)]
        public string? AuditNote { get; set; }

        public InventoryCountSheet? Sheet { get; set; }
    }
}
