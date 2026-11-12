using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>
    /// 工單工序 — RunMPS 展開時複製 RoutingOperation 資料（mfg-domain.md §5.1 step a）。
    /// 與 Dispatch 1:N（拆分會產生同工序兄弟派工），下含 WorkOrderMaterial。
    /// </summary>
    public class WorkOrderOperation
    {
        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        [MaxLength(100)]
        public string? OperationName { get; set; }

        [Required, MaxLength(30)]
        public string WorkCenterId { get; set; } = string.Empty;

        /// <summary>準備工時（單位見 <see cref="TimeUnit"/>，複製自 RoutingOperation.TM_SETUP）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal SetupTime { get; set; } = 0;

        /// <summary>單位加工工時（單位見 <see cref="TimeUnit"/>，複製自 RoutingOperation.TM_UNIT）</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal UnitTime { get; set; }

        /// <summary>UnitTime / MoveTime / QueueTime 的時間單位（複製自 RoutingOperation.TimeUnit，秒/分/時，預設分）。準備工時單位另見 <see cref="SetupTimeUnit"/>。</summary>
        public OperationTimeUnit TimeUnit { get; set; } = OperationTimeUnit.Minute;

        /// <summary>SetupTime（準備工時）的時間單位（複製自 RoutingOperation.SetupTimeUnit，可與單件工時不同，預設分）。</summary>
        public OperationTimeUnit SetupTimeUnit { get; set; } = OperationTimeUnit.Minute;

        /// <summary>移轉時間：本工序完工到下一工序可開始的搬運時間（單位見 <see cref="TimeUnit"/>，複製自 RoutingOperation.MoveTime）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal MoveTime { get; set; } = 0;

        /// <summary>等候時間：本工序開工前於工作中心排隊等待的時間（單位見 <see cref="TimeUnit"/>，複製自 RoutingOperation.QueueTime）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QueueTime { get; set; } = 0;

        /// <summary>生產批量模式 CD_BOR</summary>
        public OperationMode BatchMode { get; set; } = OperationMode.WholeOrder;

        [Column(TypeName = "decimal(18,6)")]
        public decimal BatchQty { get; set; } = 1;

        public bool IsOutsource { get; set; } = false;
        public bool RequiresFpi { get; set; } = false;
        public bool RequiresInspection { get; set; } = false;

        public WorkOrder? WorkOrder { get; set; }
        public ICollection<WorkOrderMaterial> Materials { get; set; } = new List<WorkOrderMaterial>();
        // 1:N —— 一個工序可有多個派工（拆分 §10-7 會產生同工序的兄弟派工）。
        public ICollection<Dispatch> Dispatches { get; set; } = new List<Dispatch>();
    }
}
