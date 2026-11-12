using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>MRP 執行狀態（mfg-domain.md §4.1）</summary>
    public enum MrpRunStatus
    {
        Draft = 0,      // 建立中，可加入訂單列
        Executed = 1,   // 已展開，產生 ProductionBatch 與 WorkOrder

        /// <summary>
        /// 已封存（§10-5 L3）：下游已有實體／財務足跡而無法重跑，本次展開不再維護。
        /// **不連鎖作廢下游單據**——已投產工單、已收貨採購單保持原狀走各自的生命週期；
        /// 僅把完全未投產的生產批／工單作廢，讓其未滿足需求回到可匯入清單由新執行單接手。
        /// </summary>
        Archived = 2
    }

    /// <summary>MRP 執行單 — 一次選定一批待展開訂單列，一次跑完（mfg-domain.md §4.1）</summary>
    public class MrpRun : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(30)]
        public string MrpRunId { get; set; } = string.Empty;

        public MrpRunStatus Status { get; set; } = MrpRunStatus.Draft;

        /// <summary>展開執行時間（Run 後寫入）</summary>
        public DateTime? ExecutedAt { get; set; }

        [MaxLength(50)]
        public string? ExecutedBy { get; set; }

        [MaxLength(200)]
        public string? Remarks { get; set; }

        /// <summary>
        /// 訂單修改 / PO 異動後標記 true，提示使用者重跑（mfg-domain.md §21.6）。
        /// MrpEngine.RunAsync / ReRunAsync 執行後清除為 false。
        /// </summary>
        public bool IsDirty { get; set; } = false;

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        /// <summary>PostgreSQL 系統欄位 xmin，樂觀並發 token（UseXminAsConcurrencyToken；見 ord-module.md §13）</summary>
        public uint xmin { get; set; }

        public ICollection<MrpRunLine> Lines { get; set; } = new List<MrpRunLine>();
    }
}
