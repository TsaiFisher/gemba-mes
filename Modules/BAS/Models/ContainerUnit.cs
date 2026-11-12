using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.BAS.Models
{
    /// <summary>
    /// 容器個體（箱子）：一個實體箱子一筆，箱號＝固定 QR 碼內容（container-tracking.md §9）。
    ///
    /// 箱內數量**不另記**：只記「目前綁定的品號＋批號」，數量與儲位一律從庫存帳推導，
    /// 所以不會與庫存帳漂移。綁定批號的餘額歸零即視為空箱，可再綁定（判斷時才算，過帳不掛勾）。
    /// 箱號不能當批號：箱子重複使用，批號必須每裝一次都不同（§8.5 用過即拒）。
    /// </summary>
    public class ContainerUnit : IPlantEntity
    {
        /// <summary>箱號（全域唯一，跨廠不撞號）</summary>
        [Key, MaxLength(30)]
        public string ContainerUnitId { get; set; } = string.Empty;

        /// <summary>容器種類（Container 主檔）</summary>
        [Required, MaxLength(30)]
        public string ContainerCode { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? PlantId { get; set; }

        /// <summary>目前綁定的品號；null＝從未裝過或已清空</summary>
        [MaxLength(50)]
        public string? ProductCode { get; set; }

        /// <summary>目前綁定的批號</summary>
        [MaxLength(50)]
        public string? LotNo { get; set; }

        /// <summary>綁定時間（UTC）</summary>
        public DateTime? BoundAt { get; set; }

        /// <summary>綁定來源單號（例：完工入庫的工單號）</summary>
        [MaxLength(30)]
        public string? BoundDocNo { get; set; }

        /// <summary>
        /// 在製綁定（§9.4 生產中容器）：報工時裝進這個箱子的工單＋工序；null＝不在製。
        /// 與批號綁定互斥：在製時 <see cref="LotNo"/> 為 null，<see cref="ProductCode"/> 帶工單成品品號。
        /// 數量一樣不另記，從報工容器（ProductionReportContainer）推導；工單結案／作廢後視為空箱。
        /// </summary>
        [MaxLength(40)]
        public string? WorkOrderId { get; set; }

        public int? OperationSeq { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(200)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedBy { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>箱子歷程（只增不改）：建立、綁定、清空、停用／啟用。</summary>
    public class ContainerUnitHistory : IPlantEntity
    {
        public long Id { get; set; }

        [Required, MaxLength(30)]
        public string ContainerUnitId { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? PlantId { get; set; }

        /// <summary><see cref="ContainerUnitActions"/></summary>
        [Required, MaxLength(20)]
        public string Action { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? ProductCode { get; set; }

        [MaxLength(50)]
        public string? LotNo { get; set; }

        [Column(TypeName = "decimal(18,4)")]
        public decimal? Qty { get; set; }

        [MaxLength(30)]
        public string? DocNo { get; set; }

        [MaxLength(40)]
        public string? WorkOrderId { get; set; }

        public int? OperationSeq { get; set; }

        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UserId { get; set; }
    }

    public static class ContainerUnitActions
    {
        public const string Create     = "Create";
        public const string Bind       = "Bind";
        public const string WipLoad    = "WipLoad";
        public const string Clear      = "Clear";
        public const string Recover    = "Recover";
        public const string Deactivate = "Deactivate";
        public const string Activate   = "Activate";
    }
}
