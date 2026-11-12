using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>派工狀態（mfg-domain.md §4.3）</summary>
    public enum DispatchStatus
    {
        Pending = 0,          // 未排程，沒有指定時段
        Scheduled = 1,        // 已排定，等待開工
        SetupInProgress = 2,  // 準備中，建立 ProductionSetup
        ReadyToStart = 3,     // 準備完成，等待開工
        InProgress = 4,       // 生產中
        Closed = 5,           // 完工 FG_CLOSE='Y'
        OnHold = 6,           // 暫停：機台停用／首件或巡檢不合格；原因見 HoldReason，由「恢復」或「換機續做」解除
        PendingFPI = 7,       // 等待首件檢查，「開工」按鈕強制置灰（mfg-domain.md §4.3 Phase G）
        Cancelled = 8         // 已取消：工單作廢、或暫停後剩餘量結案且未曾產出。不佔產能、不算完工
    }

    /// <summary>派工暫停原因——決定由誰、在什麼條件下解除暫停。</summary>
    public enum DispatchHoldReason
    {
        /// <summary>機台轉為不可排程狀態（故障、保養、外修…含模具保養到期）。機台恢復可用時自動解除。</summary>
        MachineDown = 1,
        /// <summary>首件檢查不合格。恢復後回到等待首件檢查，重新量測。</summary>
        FpiFailed = 2,
        /// <summary>巡檢（IPQC）不合格。由品管確認後恢復。</summary>
        IpqcFailed = 3
    }

    /// <summary>
    /// 派工單 — 與 WorkOrderOperation 1:1，以 PrevDispatchId 順序鏈結、
    /// SplitGroupId 關聯拆分子派工（mfg-domain.md §4.3 / §10）。
    /// 委外時 IsOutsource=true 且 MachineCode 為 null。
    /// </summary>
    public class Dispatch : IPlantEntity, IDataAuthEntity
    {
        [Key, MaxLength(40)]
        public string DispatchId { get; set; } = string.Empty;

        [Required, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        public int OperationSeq { get; set; }

        /// <summary>派工數量 QT_WW</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        /// <summary>累計完工量 QT_OK；&gt;= Quantity 時 Closed</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal QtyDone { get; set; } = 0;

        public DispatchStatus Status { get; set; } = DispatchStatus.Pending;

        /// <summary>派工順序鏈結（每張派工只有一張前置派工，NO_WW_U）</summary>
        [MaxLength(40)]
        public string? PrevDispatchId { get; set; }

        /// <summary>拆分派工群組關聯（子派工數量加總 = 原派工數量）</summary>
        [MaxLength(40)]
        public string? SplitGroupId { get; set; }

        public bool IsOutsource { get; set; } = false;

        /// <summary>排程指派的機台（委外時為 null）</summary>
        [MaxLength(20)]
        public string? MachineCode { get; set; }

        /// <summary>
        /// MRP 逐工序倒推的計畫開工日（無限產能、工作日曆倒推；mfg-domain.md §5.1）。
        /// 與 <see cref="ScheduledStart"/>（Phase C 有限產能排程的精確時刻）區別：
        /// 此為「最晚應開工日」的規劃值。本派工用料的庫出日 = 本欄（消耗工序之計畫開工日）。
        /// </summary>
        public DateTime? PlannedStartDate { get; set; }

        /// <summary>MRP 逐工序倒推的計畫完工日（= 後一道工序計畫開工日，最末工序 = 工單需求日）。</summary>
        public DateTime? PlannedEndDate { get; set; }

        /// <summary>排程時段起（Scheduled 後有值）</summary>
        public DateTime? ScheduledStart { get; set; }
        public DateTime? ScheduledEnd { get; set; }

        /// <summary>
        /// 待料風險（mfg-domain.md §23.1）：排程時物料不足、但在途供給預計到貨日
        /// ≤ 工單計畫開工日而獲准排入。甘特以黃色標示。
        /// </summary>
        public bool MaterialRisk { get; set; } = false;

        /// <summary>
        /// 機台已由人指定（換機台、拆分指定機台、暫停後換機續做）。排程重排時只排在這台機台上，
        /// 不再改挑同工作中心最早可用的機台；指定的機台不可用時才退回一般挑選並清除此旗標。
        /// </summary>
        public bool IsMachineLocked { get; set; } = false;

        /// <summary>暫停原因（僅 <see cref="DispatchStatus.OnHold"/> 時有值）。</summary>
        public DispatchHoldReason? HoldReason { get; set; }

        /// <summary>暫停前的狀態——恢復時回到這裡（舊資料為 null，恢復時依完工量推定）。</summary>
        public DispatchStatus? HoldFromStatus { get; set; }

        /// <summary>暫停時刻（UTC）。</summary>
        public DateTime? HoldAt { get; set; }

        /// <summary>
        /// 模具/治具代號（§23.3）：MRP 產派工時自 RoutingOperation.ToolingCode 帶入。
        /// 供 T-1 生產準備看板顯示換模需求，以及同時段衝突警示（同一 ToolingCode
        /// 被排於重疊時段的不同機台）。不納入排程引擎硬約束。
        /// </summary>
        [MaxLength(30)]
        public string? ToolingCode { get; set; }

        /// <summary>
        /// 生產線多人上工（<c>WorkCenter.IsProductionLine</c>）的 overhead 已計費時間終點。
        /// 多人各自上下工時，人工成本仍按每人各自工時累加（本來就對），但機台/產線 overhead
        /// 不該隨人數重複疊加——改成「最後一人下工」時才依此欄位到目前時刻的運轉時間窗結算一次，
        /// 並把此欄位推進到 now，避免下次結算重複計費同一段時間。一般（非產線）派工恆為 null，
        /// overhead 仍沿用單一 session 工時直接累加。
        /// </summary>
        public DateTime? OverheadChargedThroughUtc { get; set; }

        /// <summary>
        /// 並發控制（mfg-domain.md §6）。PostgreSQL 系統欄位 xmin，樂觀並發 token
        /// （UseXminAsConcurrencyToken）。原 byte[] RowVersion 是 SQL Server 樣式，Npgsql 不替 bytea
        /// 產值 → 並發權杖恆 NULL、形同無保護（已由 DispatchConcurrencyTests 實證）；改用 xmin。
        /// </summary>
        public uint xmin { get; set; }

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public WorkOrderOperation? Operation { get; set; }
        public ICollection<DispatchMaterial> Materials { get; set; } = new List<DispatchMaterial>();
    }
}
