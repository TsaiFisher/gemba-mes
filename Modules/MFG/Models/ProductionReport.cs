using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>報工狀態（mfg-domain.md §4.6）</summary>
    public enum ProductionReportStatus
    {
        Reported = 0,   // 已報工（累計到 Dispatch.QtyDone）
        Inspected = 1   // 已檢驗（QT_OK / QT_NG 確定）
    }

    /// <summary>
    /// 報工明細 — 完成數量、合格/不良（mfg-domain.md §4.6 / §5.3 / §6 並發控制）。
    /// xmin 提供樂觀並發控制（§6：DbUpdateConcurrencyException → 409）。
    /// </summary>
    public class ProductionReport : IPlantEntity
    {
        [Key, MaxLength(40)]
        public string ReportId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>冗餘存派工號，方便查詢彙計</summary>
        [Required, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        /// <summary>冗餘存工單號，方便成本彙計</summary>
        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public ProductionReportStatus Status { get; set; } = ProductionReportStatus.Reported;

        /// <summary>合格數量 QT_OK（累計到 Dispatch.QtyDone）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal QtyOk { get; set; }

        /// <summary>不良數量 QT_NG</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal QtyNg { get; set; }

        /// <summary>容器代碼（可選，CD_BOR 定量轉移用）</summary>
        [MaxLength(30)]
        public string? ContainerCode { get; set; }

        /// <summary>
        /// 不良原因代碼（FK → 未來 QNI 不良項目主檔，mfg-domain.md §15.2）。
        /// 目前為 string，QNI 主檔建立後補接 FK。
        /// </summary>
        [MaxLength(20)]
        public string? NgReasonCode { get; set; }

        public DateTime ReportedAt { get; set; }

        /// <summary>
        /// 樂觀並發控制（mfg-domain.md §6）。PostgreSQL 系統欄位 xmin（UseXminAsConcurrencyToken）。
        /// 原 byte[] RowVersion 在 Npgsql 不會自動產值 → 形同無保護，改用 xmin。
        /// </summary>
        public uint xmin { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public ProductionSession? Session { get; set; }
        public ICollection<ProductionReportContainer> Containers { get; set; } = new List<ProductionReportContainer>();
    }
}
