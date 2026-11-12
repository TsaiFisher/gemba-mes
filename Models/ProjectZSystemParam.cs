namespace SmartFactory.MES.Models
{
    /// <summary>ProjectZ 專案專屬的系統參數 Key 常數（不修改 Core 的 SystemParamKeys）</summary>
    public static class ProjectZSystemParamKeys
    {
        public const string BaseCurrency = "base_currency"; // 本幣代號，如 TWD；用於幣別匯率基準
        public const string InvSimplifiedMode = "inv_simplified_mode"; // 庫存簡化模式（true=建單即過帳，false=兩階段）
        public const string InvDefaultCostMethod = "inv_default_cost_method"; // 全系統預設計價方式 (MA / FIFO)

        // §25：製造簡化模式（mfg-domain.md §25）。true = MRP 展開後工單自動投產（免投產閘門）；
        // 未設定/false = 完整流程（工單停在 Planned 等人工投產）。與 inv_simplified_mode 各自獨立。
        public const string MfgSimplifiedMode = "mfg_simplified_mode";

        // §18.2：WorkOrder 簽核金額閥值（超過此金額強制送審，mfg-domain.md §18.2，預設 500000）
        public const string MfgWoApprovalThreshold = "mfg_wo_approval_threshold";

        // §18.2 其餘三條觸發（2026-10-20 A9）：有工單簽核流程時，符合者一律送審、不走門檻快速通道。
        // 未設定＝false＝舊行為（只看門檻）。委外／重工為規格的「可選」項；延期＝工單預計完工晚於生產批需求日（訂單交期）。
        public const string MfgWoApprovalOutsource = "mfg_wo_approval_outsource";
        public const string MfgWoApprovalRework    = "mfg_wo_approval_rework";
        public const string MfgWoApprovalOnDelay   = "mfg_wo_approval_on_delay";

        // §13 / §9：機台 Standby 狀態是否可排程（mfg-domain.md §9，預設 false）
        // ⚠️ 本參數是**全系統單一值，沒有廠別隔離**（`SystemParam` 主鍵只有 Key）。
        // 原註解寫「支援廠別隔離」與實作不符（`SchedulingEngine` 讀的是全域值），已於 2026-08-17 更正。
        // 不同廠別對 Standby 常有不同理解（A 廠=隨時可排，B 廠=無人待機），要真的分廠
        // 需先做參數廠別化，見 `.claude/specifications/plant-scoped-system-params.md`。
        public const string MfgStandbySchedulable = "mfg_standby_schedulable";

        // Phase F：完工入庫相關設定
        // ⚠️ 下面兩個存的是**倉庫／儲位代碼**，而 `Warehouse` 主鍵是 `WhCode` 單欄（全域唯一）
        // 且為 IPlantEntity——一個代碼只屬於一個廠。參數卻是全域單值，故在其他廠必然指不到／
        // 指到別廠。讀取端一律經 `IPlantDefaultLocationResolver` 收斂（驗不過即視同未設定）。
        public const string MfgFgWarehouse        = "mfg_fg_warehouse";        // 成品倉倉庫代號（完工入庫預設）
        public const string MfgFgLocation         = "mfg_fg_location";         // 成品倉儲位代號（完工入庫預設）

        // ⚠️ 全域單值，**不是**廠別設定（原註解寫「廠別預設發料方式」與實作不符，2026-08-17 更正）；
        // 且目前**沒有任何讀取端**，屬純宣告。要接上或要分廠，見 plant-scoped-system-params.md。
        public const string MfgDefaultIssueMethod = "mfg_default_issue_method"; // 預設發料方式 (Push/Pull/Backflush)
        public const string MfgMaterialGate       = "mfg_material_gate";        // §21.5 開工物料備齊閘口 (None/Warning/Block，預設 Warning)
        // 開工機台狀態閘口 (None/Warning/Block，預設 Warning)：機台不在可排程狀態（故障、保養、外修…）時
        // 擋下或警示開工。判定與排程共用 MachineAvailability（含 MfgStandbySchedulable）。
        public const string MfgMachineStatusGate  = "mfg_machine_status_gate";

        // 超量生產／超量收貨（2026-10-12，mfg-domain.md §4.6 超量）。可廠別覆寫，讀取一律經 IOverQuantityPolicy。
        //   *_tolerance_pct：可超過計畫量的百分比（非負數；未設定＝0，即一個都不能多）
        //   *_action：超過容許上限時 Block（拒收）／Supervisor（具「超量核准」權限者可收，其他人擋下）
        //   outsource_over_receipt_fee：超過委外採購單數量的部分是否付加工費 Pay／NoPay
        // 預設：廠內 0%＋Block（＝原本的硬擋）；委外 0%＋Supervisor＋Pay（最接近原本「超收只確認、照付」）。
        public const string MfgOverReportTolerancePct        = "mfg_over_report_tolerance_pct";
        public const string MfgOverReportAction              = "mfg_over_report_action";
        public const string MfgOutsourceOverReceiptTolerancePct = "mfg_outsource_over_receipt_tolerance_pct";
        public const string MfgOutsourceOverReceiptAction    = "mfg_outsource_over_receipt_action";
        public const string MfgOutsourceOverReceiptFee       = "mfg_outsource_over_receipt_fee";

        // 委外簡化（2026-10-12，mfg-domain.md §19.7）：小廠把委外從 7 步降到「投產 → 收貨驗收」。三者可各自開，未設定＝關（完整流程）。
        //   issue_on_receipt：送外料不另開委外發料單，收貨確認時依工單用料 × 收回量自動扣（從品號類別預設倉儲出庫）
        //   inspect_at_receipt：手機委外收料當場驗收（填不良數），合格直接入一般倉、不合格入待驗倉，不必再上架待驗倉＋品檢
        //   auto_po：整品委外工單投產時，品號在委外報價主檔只有一家有效報價就直接建委外採購單（已確認）
        public const string MfgOutsourceIssueOnReceipt   = "mfg_outsource_issue_on_receipt";
        public const string MfgOutsourceInspectAtReceipt = "mfg_outsource_inspect_at_receipt";
        public const string MfgOutsourceAutoPo           = "mfg_outsource_auto_po";

        // 自動重排排程（2026-10-06，mfg-domain.md §10 自動重排）。三者可並用，未設定＝關閉。
        // 可廠別覆寫（PlantParamCatalog），讀取一律經 IPlantParamService 帶明確 plantId。
        //   on_change：派工、工單、機台狀態、日曆／計劃停機有異動後，安靜 90 秒（最長 10 分鐘）自動重排
        //   before_dispatch：開派工清單、手機派工、生產準備看板、排程頁與 17:00 準備提醒前，若排程已過時先重排
        //   daily_time：每天固定時刻（HH:mm，廠別當地時間）全量重排一次；錯過時刻當天補跑
        public const string MfgAutoScheduleOnChange       = "mfg_auto_schedule_on_change";
        public const string MfgAutoScheduleBeforeDispatch = "mfg_auto_schedule_before_dispatch";
        public const string MfgAutoScheduleDailyTime      = "mfg_auto_schedule_daily_time";
        // 自動重排的排序規則（2026-10-14）：Priority／EDD／SPT／FIFO／LIFO，未設定＝Priority（原行為）。
        // 手動排程照舊由畫面選擇，本值只當排程頁規則下拉的預設。
        public const string MfgAutoScheduleRule           = "mfg_auto_schedule_rule";
        // 自動重排的凍結時界（小時，2026-10-14）：已排、且在 now＋N 小時內開工的派工保留原機台與時段。
        // 未設定或 0＝不使用（原行為）。手動排程不套用。
        public const string MfgAutoScheduleFreezeHours    = "mfg_auto_schedule_freeze_hours";

        // 備料倉預設值（**全系統單一值**，非廠別）。備料單的備料倉/儲位解析順序：
        // 開單當下指定 > WorkCenter.StagingWhCode/StagingLocationCode > 這兩個系統參數。
        // 同上：值是廠別相依的倉庫代碼，`StagingSourceService` 每一層都經
        // `IPlantDefaultLocationResolver` 驗過廠別才採用。
        public const string MfgDefaultStagingWh       = "mfg_default_staging_wh";
        public const string MfgDefaultStagingLocation = "mfg_default_staging_location";

        // §B 批量政策：LotForLot（依淨需求，預設）或 MinMultiple（最小批量+倍數）
        public const string MfgLotPolicy          = "mfg_lot_policy";

        // §10-5 重跑對帳：MRP 重跑後，既有請購單中「本次已無需求、且尚未轉採購」的明細
        // 是否自動結案（IsClosed）退場。true=自動結案；未設定/false=僅列為差異項提示人工處理。
        public const string MfgMrpAutoCloseObsoletePr = "mfg_mrp_auto_close_obsolete_pr";

        // business_data_flow_reconciliation.md §9 P-7：容忍值系統參數化（未設定時各服務用預設值，行為不變）
        public const string AuditQtyTolerance    = "audit_qty_tolerance";    // 稽核數量容忍值（預設 0.0001 = NUMERIC(18,4) 最小單位）
        public const string AuditAmountTolerance = "audit_amount_tolerance"; // 稽核/ERP 尾差金額容忍值（預設 0.01 = 本位幣最小精度）
        public const string FinSettleTolerance   = "fin_settle_tolerance";   // AP/AR 核銷判定尾差（預設 0.001，緊於金額容忍值一個量級以防過度寬鬆核銷）

        // pur-domain.md §6 Phase G+：供應商評分卡 TotalScore 加權平均權重（未設定時預設 30/30/20/20）
        public const string SrmWeightQuality  = "srm_weight_quality";
        public const string SrmWeightDelivery = "srm_weight_delivery";
        public const string SrmWeightPrice    = "srm_weight_price";
        public const string SrmWeightService  = "srm_weight_service";
    }
}
