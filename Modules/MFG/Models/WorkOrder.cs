using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SmartFactory.MES.Models;
using SmartFactory.MES.Modules._Shared;

namespace SmartFactory.MES.Modules.MFG.Models
{
    /// <summary>工單類型（Phase A 新增：Normal 自製 / Rework 重工 / Outsource 委外整品）</summary>
    public enum WorkOrderType
    {
        Normal    = 0,   // 一般自製
        Rework    = 1,   // 重工（ReworkFromWorkOrderId 填入來源工單）
        Outsource = 2    // 委外整品（IsOutsource = true，保持向後相容）
    }

    /// <summary>物料備齊狀態（mfg-domain.md §21.5）。v1 僅產生 NotChecked/Ready/Shortage。</summary>
    public enum MaterialReadinessStatus
    {
        NotChecked = 0,      // 預設，未執行確認
        Ready = 1,           // 全部物料確認可用
        PartialShortage = 2, // 部分缺料（保留供日後「非瓶頸料」擴充，v1 不產生）
        Shortage = 3         // 有物料缺料
    }

    /// <summary>工單狀態（mfg-domain.md §4.2）</summary>
    public enum WorkOrderStatus
    {
        Planned = 0,     // 已規劃，待釋出
        Released = 1,    // 已釋出，工序可排程
        InProgress = 2,  // 生產中（任一 Dispatch 開工自動同步）
        Completed = 3,   // 完工，待入庫
        Closed = 4,      // 入庫完成
        Void = 5         // 作廢
    }

    /// <summary>
    /// 工單 — 自製展開單位，支援 BOM 上下階層（ParentWorkOrderId）
    /// 與重工連結（ReworkFromWorkOrderId）（mfg-domain.md §4.2 / §5.1 / §14.2）。
    /// </summary>
    public class WorkOrder : IPlantEntity, IDataAuthEntity, IApprovalDocument
    {
        /// <summary>NO_WO = ID_PLN + "_" + 流水號</summary>
        [Key, MaxLength(40)]
        public string WorkOrderId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ProductionBatchId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string ProductCode { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string RoutingId { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,6)")]
        public decimal Quantity { get; set; }

        // ── 投產定案重算錨定（mfg-domain.md §24.2 / §24.8）────────────────────────
        /// <summary>
        /// 毛需求量（MRP 展開「當時」尚未扣庫存、未 yield 膨脹的需求量）。
        /// 投產（Release）時 net-at-release 重算錨定：生產量 = GrossDemandQty − 投產當下可用量（§24.2）。
        /// 與 <see cref="Quantity"/>（= 淨量 ÷ 累積良率，可於投產時重算覆寫）區分。
        /// </summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal GrossDemandQty { get; set; }

        /// <summary>來源 MRP 執行批號快照（參照 MrpRunLine.MrpRunId；供投產重算回溯需求列）</summary>
        [MaxLength(30)]
        public string? SourceMrpRunId { get; set; }

        /// <summary>來源 MRP 需求列序快照（參照 MrpRunLine.LineSeq）</summary>
        public int? SourceMrpRunLineSeq { get; set; }

        /// <summary>自製階層：成品工單 → 半成品工單</summary>
        [MaxLength(40)]
        public string? ParentWorkOrderId { get; set; }

        /// <summary>重工工單連結原始工單（mfg-domain.md §14.2）</summary>
        [MaxLength(40)]
        public string? ReworkFromWorkOrderId { get; set; }

        /// <summary>工單類型（Phase A）</summary>
        public WorkOrderType Type { get; set; } = WorkOrderType.Normal;

        public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Planned;

        /// <summary>倒推投產日 DT_START</summary>
        public DateTime PlannedStartDate { get; set; }

        /// <summary>最晚須完成日 DT_DLV（倒推）</summary>
        public DateTime PlannedEndDate { get; set; }

        // ── 生產成本（mfg-domain.md §16.2）─────────────────────────────────────
        // Standard* 於釋出（Released）時一次計算並快照；Actual* 於報工/完工累計。
        /// <summary>標準直接材料成本（釋出時 = Σ WorkOrderMaterial.RequiredQty × UnitCost）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardMaterialCost { get; set; }

        /// <summary>標準直接人工成本（釋出時 = Σ 工序 TM_UNIT × WorkCenter.StandardLaborCost / 3600）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardLaborCost { get; set; }

        /// <summary>標準製造費用（釋出時 = Σ 工序 TM_UNIT × WorkCenter.MachineHourlyRate / 3600）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal StandardOverheadCost { get; set; }

        /// <summary>實際材料成本（Phase F 完工入庫由 INV 模組回寫）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualMaterialCost { get; set; }

        /// <summary>實際人工成本（Phase D 每次 ProductionSession 關閉累計）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualLaborCost { get; set; }

        /// <summary>實際製造費用（Phase D 每次 ProductionSession 關閉累計）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualOverheadCost { get; set; }

        /// <summary>
        /// 實際委外成本（mfg-domain.md §19.7 C）：工序委外收回時累計「加工費＋帶出去被加工掉的料」，本幣。
        /// 完工入庫時併入單位成本。不併進 <see cref="ActualMaterialCost"/>（會污染材料差異）也不併進製費
        /// （完工時人工＋製費會再做一次吸收分錄，加工費已在收貨時借存貨，重複入帳）。
        /// </summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal ActualOutsourceCost { get; set; }

        /// <summary>報工累計報廢量（Σ ProductionReport.QtyNg）；用於報廢成本分析</summary>
        [Column(TypeName = "decimal(18,6)")]
        public decimal QtyScrap { get; set; }

        /// <summary>材料差異成本 = ActualMaterialCost − StandardMaterialCost（全料基準，D1-A；
        /// ActualMaterialCost 於完工彙總後沖＋Push/Pull 全部耗用材料後回寫）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal MaterialVarianceCost { get; set; }

        // ── 委外（mfg-domain.md §19.2）─────────────────────────────────────────
        /// <summary>此工單是否委外（RunJ 整品委外展開時為 true）</summary>
        public bool IsOutsource { get; set; } = false;

        /// <summary>委外廠商（FK → Company.IsSupplier，可空；釋出建立採購單時填入）</summary>
        [MaxLength(20)]
        public string? OutsourceSupplierCode { get; set; }

        /// <summary>對應委外採購單（FK → PurchaseOrder，可空；釋出時建立/連結）</summary>
        [MaxLength(30)]
        public string? OutsourcePurchaseOrderId { get; set; }

        /// <summary>委外廠商應交日期（倒推交期 − 委外提前期）</summary>
        public DateOnly? OutsourceExpectedDeliveryDate { get; set; }

        // ── 釋出簽核（mfg-domain.md §18.2）：釋出時依「WorkOrder」簽核設定決定是否送審 ──
        /// <summary>簽核狀態（釋出前為 Draft；釋出時依設定轉 PendingApproval 或 Approved）</summary>
        public DocumentApprovalStatus ApprovalStatus { get; set; } = DocumentApprovalStatus.Draft;

        /// <summary>對應簽核申請單（送審時填入）</summary>
        [MaxLength(50)]
        public string? ApprovalRequestId { get; set; }

        // ── 物料備齊閘口（mfg-domain.md §21.5）──
        /// <summary>物料備齊狀態（開工前確認；由 IWorkOrderMaterialReadinessService.CheckAsync 寫回）</summary>
        public MaterialReadinessStatus MaterialReadinessStatus { get; set; } = MaterialReadinessStatus.NotChecked;

        public string? PlantId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // IDataAuthEntity
        public string? CreatedByRoleId { get; set; }
        public string? OrganizationUnitPath { get; set; }

        public ProductionBatch? ProductionBatch { get; set; }
        public ICollection<WorkOrderOperation> Operations { get; set; } = new List<WorkOrderOperation>();
    }
}
