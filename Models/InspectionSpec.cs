using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartFactory.MES.Models
{
    /// <summary>抽樣計數邊界（mfg-domain.md §15.3 Phase G）</summary>
    public enum SamplingScope
    {
        SpecLevel    = 1,   // 整個規格合算批次計數（預設）
        MachineLevel = 2,   // 依機台分別計數
        ToolingLevel = 3    // 依治具分別計數
    }

    /// <summary>檢驗單類型（mfg-domain.md §15.3 Phase G）</summary>
    public enum InspectionOrderType
    {
        FPI  = 1,   // 首件檢查
        IPQC = 2,   // 製程巡檢
        FQC  = 3    // 完工檢驗
    }


    /// <summary>
    /// 檢驗規格：定義產品（或產品族）的 MIL-STD-1916 驗證等級與抽樣類型。
    /// 供 IQC（進料）與 IPQC（製程）共用。
    /// </summary>
    public class InspectionSpec
    {
        public string SpecId { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string SpecCode { get; set; } = string.Empty;

        [Required, MaxLength(100)]
        public string SpecName { get; set; } = string.Empty;
        public string? PlantId    { get; set; }

        /// <summary>適用品號（null = 全品號通用）</summary>
        public string? ProductCode   { get; set; }

        /// <summary>適用供應商（null = 全供應商；IQC 使用）</summary>
        public string? SupplierCode  { get; set; }

        /// <summary>MIL-STD-1916 驗證等級（1-7）</summary>
        public int VerificationLevel { get; set; } = 4;   // 預設 VL-IV

        /// <summary>抽樣類型：Attribute（計數） / Variable（計量）</summary>
        public string SamplingType   { get; set; } = "Attribute";

        /// <summary>IPQC 觸發條件：Always / Periodic / OnDemand</summary>
        public string TriggerMode    { get; set; } = "Always";

        /// <summary>定期抽樣間隔（小時），TriggerMode=Periodic 時有效</summary>
        public int? PeriodHours     { get; set; }

        // ── 完工 / 退貨閘控 ──────────────────────────────────────────────────
        /// <summary>完工入庫前必須通過 FQC（mfg-domain.md §14.1）</summary>
        public bool RequiresFQC      { get; set; } = false;

        /// <summary>銷退回廠確認後自動建立 RQC（Return QC）待檢單</summary>
        public bool RequiresRQC      { get; set; } = false;

        // ── MIL-STD-1916 VL 切換規則 ────────────────────────────────────────
        /// <summary>正常抽樣 VL（預設 VL-IV = 4）</summary>
        public int NormalVl          { get; set; } = 4;

        /// <summary>加嚴抽樣 VL（任何不合格後啟用，預設 VL-VI = 6）</summary>
        public int TightenedVl       { get; set; } = 6;

        /// <summary>減量抽樣 VL（連續合格後降至，預設 VL-II = 2）</summary>
        public int ReducedVl         { get; set; } = 2;

        /// <summary>從「正常」轉「減量」所需連續合格批數（預設 5）</summary>
        public int PassesToReduce    { get; set; } = 5;

        /// <summary>從「加嚴」恢復「正常」所需連續合格批數（預設 3）</summary>
        public int PassesToNormal    { get; set; } = 3;

        // ── 跳批（Skip-Lot）— MIL-STD-1235 選配擴充 ────────────────────────
        /// <summary>啟用跳批（需先在「減量」狀態達 PassesToSkipLot 連續合格）</summary>
        public bool SkipLotEnabled   { get; set; } = false;

        /// <summary>跳批頻率：每 N 批才抽 1 批（預設 3）</summary>
        public int SkipLotFrequency  { get; set; } = 3;

        /// <summary>從「減量」進入「跳批」所需連續合格批數（預設 10）</summary>
        public int PassesToSkipLot   { get; set; } = 10;

        /// <summary>抽樣計數邊界（null = 舊資料，視同 SpecLevel）</summary>
        public SamplingScope? SamplingScope { get; set; }

        // ── 執行狀態（由 SamplingPlanService 動態維護）─────────────────────
        /// <summary>現行抽樣狀態：Normal / Tightened / Reduced / SkipLot</summary>
        public string SamplingState     { get; set; } = "Normal";

        /// <summary>目前連續合格批數</summary>
        public int ConsecutivePasses    { get; set; } = 0;

        /// <summary>跳批模式下的批次計數器（每批 +1，符合頻率才抽）</summary>
        public int LotCounter           { get; set; } = 0;

        public bool IsActive         { get; set; } = true;

        /// <summary>備註。長度上限對齊全庫慣例（varchar(500)）；原為無上限的 PG text。</summary>
        [MaxLength(500)]
        public string? Remarks       { get; set; }
        public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt    { get; set; } = DateTime.UtcNow;

        public ICollection<InspectionSpecItem> Items { get; set; } = new List<InspectionSpecItem>();
    }

    /// <summary>
    /// 檢驗規格項目：定義此規格下每個量測特性的規格上下限與目標值。
    /// 供 IPQC 製程檢驗量測值紀錄（InspectionItem）快照使用。
    /// </summary>
    public class InspectionSpecItem
    {
        public string SpecItemId    { get; set; } = Guid.NewGuid().ToString("N");
        public string SpecId        { get; set; } = string.Empty;
        public int    SeqNo         { get; set; }
        public string ItemName      { get; set; } = string.Empty;
        public string? UomCode      { get; set; }
        public decimal? LowerSpec   { get; set; }
        public decimal? TargetValue { get; set; }
        public decimal? UpperSpec   { get; set; }

        /// <summary>量測所需儀器代碼（最多 3 支，對應舊系統 QNO_D.ID_QIN1/2/3）</summary>
        [System.ComponentModel.DataAnnotations.MaxLength(20)]
        public string? InstrumentCode1 { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(20)]
        public string? InstrumentCode2 { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(20)]
        public string? InstrumentCode3 { get; set; }

        public bool   IsActive      { get; set; } = true;

        /// <summary>預設製程標準差 σ（null = 跳過 3-sigma 警告，Phase H 使用）</summary>
        public decimal? TargetStdDev { get; set; }

        public InspectionSpec? Spec { get; set; }
    }

    /// <summary>
    /// 檢驗單：IQC（進料）或 IPQC（製程）的一次抽樣記錄。
    /// 依 InspectionSpec 取得應抽樣數，執行後記錄結果。
    /// </summary>
    public class InspectionOrder
    {
        public string InspectionOrderId { get; set; } = Guid.NewGuid().ToString("N");
        public string? PlantId           { get; set; }

        /// <summary>檢驗類型：IQC / IPQC / FQC</summary>
        public string InspectionType     { get; set; } = "IQC";

        /// <summary>製程檢驗單類型（FPI/IPQC/FQC）；IQC 來源的單可為 null（Phase G 加入）</summary>
        public InspectionOrderType? OrderType { get; set; }

        /// <summary>投產原料批號快照（JSON 陣列，如 ["RM24-001","RM24-002"]）；Phase I NCR 交叉分析用</summary>
        public string? ActiveMaterialLots { get; set; }

        /// <summary>來源單號（IQC：GoodsReceiptId；IPQC：DispatchId）</summary>
        public string SourceDocId        { get; set; } = string.Empty;
        public int?   SourceLineNo       { get; set; }

        public string ProductCode        { get; set; } = string.Empty;

        /// <summary>批量（來自進貨或派工數量）</summary>
        [Column(TypeName = "decimal(18,4)")]
        public decimal LotSize           { get; set; }

        /// <summary>應抽樣數（由 MIL-STD-1916 依批量 + VL 計算）</summary>
        public int SampleSize            { get; set; }

        /// <summary>驗證等級（1-7）</summary>
        public int VerificationLevel     { get; set; } = 4;

        /// <summary>實際抽取數</summary>
        public int ActualSampleQty       { get; set; }

        /// <summary>不良數（MIL-STD-1916：只要 ≥1 即拒批）</summary>
        public int FailQty               { get; set; }

        /// <summary>Pass / Fail / Pending</summary>
        public string Status             { get; set; } = "Pending";

        public string? Remarks           { get; set; }
        public string? InspectorId       { get; set; }
        public DateTime? InspectedAt     { get; set; }
        public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt        { get; set; } = DateTime.UtcNow;

        public ICollection<InspectionItem> Items { get; set; } = new List<InspectionItem>();
    }

    /// <summary>
    /// 檢驗項目明細（計量值量測紀錄）。
    /// 計數值抽樣可選填；計量值抽樣時必填各量測值。
    /// </summary>
    public class InspectionItem
    {
        [System.ComponentModel.DataAnnotations.Key]
        public string ItemId             { get; set; } = Guid.NewGuid().ToString("N");
        public string InspectionOrderId  { get; set; } = string.Empty;
        public int    SeqNo              { get; set; }
        public string ItemName           { get; set; } = string.Empty;
        public string? UomCode           { get; set; }
        public decimal? LowerSpec        { get; set; }
        public decimal? TargetValue      { get; set; }
        public decimal? UpperSpec        { get; set; }
        public decimal? MeasuredValue    { get; set; }
        public bool?   IsConforming      { get; set; }

        /// <summary>快照自 InspectionSpecItem.TargetStdDev（null = 不做 3-sigma 警告）</summary>
        public decimal? TargetStdDev     { get; set; }

        /// <summary>後端計算：|MeasuredValue - TargetValue| > 3σ 時為 true</summary>
        public bool HasSigmaWarning      { get; set; } = false;

        public InspectionOrder? Order    { get; set; }
    }

    /// <summary>
    /// 依 SamplingScope 分區的批次計數器（mfg-domain.md §15.3 Phase H）。
    /// ScopeKey 規則：SpecLevel = ""；MachineLevel = machineCode；ToolingLevel = toolingCode。
    /// </summary>
    public class InspectionSpecSamplingCounter
    {
        [System.ComponentModel.DataAnnotations.Key]
        public string CounterId { get; set; } = Guid.NewGuid().ToString("N");

        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(40)]
        public string SpecId { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(50)]
        public string ScopeKey { get; set; } = string.Empty;

        public string SamplingState    { get; set; } = "Normal";
        public int    ConsecutivePasses { get; set; } = 0;
        public int    LotCounter        { get; set; } = 0;
        public DateTime UpdatedAt       { get; set; } = DateTime.UtcNow;

        public InspectionSpec? Spec { get; set; }
    }
}
