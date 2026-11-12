using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>工序開始條件</summary>
    public enum OperationMode
    {
        Continuous = 1,
        Batch = 2,
        WholeOrder = 3
    }

    /// <summary>工序時間單位（TM_SETUP / TM_UNIT 共用，mfg-domain.md §3.1(3)）</summary>
    public enum OperationTimeUnit
    {
        Second = 1,
        Minute = 2,
        Hour = 3
    }

    /// <summary>工序明細（CN_ORD）— Routing 子表，複合 PK</summary>
    public class RoutingOperation
    {
        [Required, MaxLength(30)]
        public string RoutingId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        [MaxLength(100)]
        public string? OperationName { get; set; }

        [Required, MaxLength(30)]
        public string WorkCenterId { get; set; } = string.Empty;

        /// <summary>設置/整備時間：換模、換線等開始生產前準備（每張派工一次）。單位見 <see cref="TimeUnit"/>。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal TM_SETUP { get; set; } = 0;

        /// <summary>單位標準工時：生產一個所需標準時間。單位見 <see cref="TimeUnit"/>。</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal TM_UNIT { get; set; }

        /// <summary>TM_UNIT / MoveTime / QueueTime 的時間單位（秒/分/時，預設分）。準備工時單位另見 <see cref="SetupTimeUnit"/>。</summary>
        public OperationTimeUnit TimeUnit { get; set; } = OperationTimeUnit.Minute;

        /// <summary>TM_SETUP（準備工時）的時間單位，與單件工時可不同（秒/分/時，預設分）。</summary>
        public OperationTimeUnit SetupTimeUnit { get; set; } = OperationTimeUnit.Minute;

        /// <summary>移轉時間：本工序完工到下一工序可開始的搬運時間。單位見 <see cref="TimeUnit"/>（mfg-domain.md §3.1(8)）。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal MoveTime { get; set; } = 0;

        /// <summary>等待時間：本工序開始前的排隊/等待時間。單位見 <see cref="TimeUnit"/>。</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal QueueTime { get; set; } = 0;

        public OperationMode CD_BOR { get; set; } = OperationMode.WholeOrder;

        [Column(TypeName = "decimal(18,6)")]
        public decimal QT_BOR { get; set; } = 1;

        [Required, MaxLength(20)]
        public string UomCode { get; set; } = string.Empty;

        public bool IsOutsource { get; set; } = false;
        public bool RequiresFpi { get; set; } = false;
        public bool RequiresInspection { get; set; } = false;

        /// <summary>工序產出良率 %（投入→良品）。預設 100。MRP 投產量以累積良率回推（mfg-domain.md §3.1(7)）。</summary>
        [Column(TypeName = "decimal(8,4)")]
        public decimal YieldRate { get; set; } = 100m;

        /// <summary>治具/模具 → 容器維護外的 Tooling 主檔 ToolingCode；供 Setup 規劃與模穴產出率（mfg-domain.md §3.1(8)）。</summary>
        [MaxLength(20)]
        public string? ToolingCode { get; set; }

        /// <summary>開工容器（FG_IN）→ 容器維護 Container.ContainerCode；空＝不以容器轉移（mfg-domain.md §3.1(5)）</summary>
        [MaxLength(30)]
        public string? ContainerInCode { get; set; }

        /// <summary>完工容器（FG_OUT）→ 容器維護 Container.ContainerCode；空＝不以容器轉移</summary>
        [MaxLength(30)]
        public string? ContainerOutCode { get; set; }

        /// <summary>作業標準編號（NO_SOP）</summary>
        [MaxLength(50)]
        public string? SopNumber { get; set; }

        public Routing? Routing { get; set; }
        public ICollection<RoutingBom> Boms { get; set; } = new List<RoutingBom>();
    }
}
